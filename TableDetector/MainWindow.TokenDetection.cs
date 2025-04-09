// This is an update for the MainWindow.TokenDetection.cs file

using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;
using System.Diagnostics;
using System.Windows.Controls;

namespace TableDetector
{
    public partial class MainWindow
    {
        // Add these fields to MainWindow class
        private bool hasValidROI = false;
        private bool hasValidTableDepth = false;
        private int maxMiniatureHeight = 120; // Maximum height in mm for miniature detection
        private double miniatureBaseThreshold = 0.5; // Base-to-height ratio threshold for miniatures
        private int miniDetectionSensitivity = 10; // Lower value = more sensitive detection

        /// <summary>
        /// Validates if the current setup is ready for token detection
        /// </summary>
        private bool IsReadyForTokenDetection()
        {
            hasValidROI = detectedTableROI.Width > 10 && detectedTableROI.Height > 10;
            hasValidTableDepth = tableDepth > 500; // Minimum 500mm depth

            return hasValidROI && hasValidTableDepth && trackTokens;
        }

        /// <summary>
        /// Enhanced token detection method with improved handling of tapered miniatures and better calibration
        /// Only runs after a valid ROI is established
        /// </summary>
        private void DetectTokensEnhanced()
        {
            // Check if we have valid ROI and table depth before proceeding
            if (!IsReadyForTokenDetection())
            {
                if (trackTokens && (detectedTokens.Count > 0))
                {
                    // If we were tracking but lost conditions, clear tokens
                    detectedTokens.Clear();
                    this.Dispatcher.Invoke(() => {
                        UpdateTokenOverlay();
                        TokenCountText = "Detection inactive - define ROI";
                    });
                }
                return;
            }

            // Store previous tokens for tracking and comparison
            List<TTRPGToken> previousTokens = new List<TTRPGToken>(detectedTokens);
            detectedTokens.Clear();

            // First pass: build a height map to help with segmentation
            int[,] heightMap = new int[depthWidth, depthHeight];
            bool[,] processed = new bool[depthWidth, depthHeight];

            // Calculate height above table for each point in the ROI
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // Skip invalid or background depths
                    if (depth == 0 || depth >= tableDepth)
                        continue;

                    // Calculate height above table
                    int heightAboveTable = tableDepth - depth;

                    // Only consider heights within our miniature detection range
                    if (heightAboveTable >= MIN_TOKEN_HEIGHT && heightAboveTable <= maxMiniatureHeight)
                    {
                        // Store in height map
                        heightMap[x, y] = heightAboveTable;
                    }
                }
            }

            // Second pass: detect bases of objects using a flood-fill approach
            List<MiniatureCandidate> miniatureCandidates = new List<MiniatureCandidate>();

            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight || processed[x, y])
                        continue;

                    int height = heightMap[x, y];

                    // If this point has a valid height (part of a potential miniature)
                    if (height > 0)
                    {
                        // Start a new candidate
                        MiniatureCandidate candidate = new MiniatureCandidate();

                        // Collect connected points with similar heights
                        List<Point> basePoints = new List<Point>();
                        Dictionary<int, List<Point>> heightLayers = new Dictionary<int, List<Point>>();
                        Queue<Point> queue = new Queue<Point>();
                        int highestPoint = height;

                        // Add initial height layer
                        heightLayers[height] = new List<Point>();

                        queue.Enqueue(new Point(x, y));
                        processed[x, y] = true;

                        // Flood fill to find all connected points
                        while (queue.Count > 0)
                        {
                            Point p = queue.Dequeue();
                            int px = (int)p.X;
                            int py = (int)p.Y;
                            int currentHeight = heightMap[px, py];

                            // Add point to its height layer
                            if (!heightLayers.ContainsKey(currentHeight))
                                heightLayers[currentHeight] = new List<Point>();

                            heightLayers[currentHeight].Add(p);

                            // Track highest point
                            if (currentHeight > highestPoint)
                                highestPoint = currentHeight;

                            // Check neighbors (8-connectivity)
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = px + dx;
                                    int ny = py + dy;

                                    // Skip out of bounds or already processed
                                    if (nx < 0 || nx >= depthWidth || ny < 0 || ny >= depthHeight || processed[nx, ny])
                                        continue;

                                    int neighborHeight = heightMap[nx, ny];

                                    // Only connect if the neighbor has a valid height
                                    // and is within a reasonable height difference
                                    if (neighborHeight > 0)
                                    {
                                        // Allow greater height differences for taller parts (miniature body)
                                        // but stricter for lower parts (base)
                                        int heightTolerance;

                                        if (currentHeight < 15) // For bases (flatter parts)
                                            heightTolerance = 5;
                                        else // For bodies (taller parts)
                                            heightTolerance = 15;

                                        if (Math.Abs(neighborHeight - currentHeight) <= heightTolerance)
                                        {
                                            queue.Enqueue(new Point(nx, ny));
                                            processed[nx, ny] = true;
                                        }
                                    }
                                }
                            }
                        }

                        // Extract the base layer (lower height points)
                        // Sort height layers by height
                        var sortedLayers = heightLayers.OrderBy(kv => kv.Key).ToList();

                        // Use the lowest 30% of heights as the base
                        int totalPoints = sortedLayers.Sum(kv => kv.Value.Count);
                        int basePointCount = 0;
                        int baseLayerLimit = Math.Max(1, sortedLayers.Count / 3);

                        for (int i = 0; i < baseLayerLimit && i < sortedLayers.Count; i++)
                        {
                            basePoints.AddRange(sortedLayers[i].Value);
                            basePointCount += sortedLayers[i].Value.Count;
                        }

                        // Collect all points (base and body)
                        List<Point> allPoints = new List<Point>();
                        foreach (var layer in sortedLayers)
                        {
                            allPoints.AddRange(layer.Value);
                        }

                        // Only consider candidates with enough points
                        if (allPoints.Count >= miniDetectionSensitivity && basePoints.Count >= MIN_BASE_SIZE)
                        {
                            // Calculate base center
                            double sumX = 0;
                            double sumY = 0;
                            foreach (Point pt in basePoints)
                            {
                                sumX += pt.X;
                                sumY += pt.Y;
                            }
                            double baseX = sumX / basePoints.Count;
                            double baseY = sumY / basePoints.Count;

                            // Calculate base size
                            double minX = double.MaxValue, maxX = double.MinValue;
                            double minY = double.MaxValue, maxY = double.MinValue;
                            foreach (Point pt in basePoints)
                            {
                                if (pt.X < minX) minX = pt.X;
                                if (pt.X > maxX) maxX = pt.X;
                                if (pt.Y < minY) minY = pt.Y;
                                if (pt.Y > maxY) maxY = pt.Y;
                            }
                            double baseWidth = maxX - minX;
                            double baseHeight = maxY - minY;
                            double baseDiameter = Math.Max(baseWidth, baseHeight);

                            // Create the candidate
                            candidate.BaseCenter = new Point(baseX, baseY);
                            candidate.BaseDiameter = baseDiameter;
                            candidate.BasePoints = basePoints;
                            candidate.AllPoints = allPoints;
                            candidate.MaxHeight = highestPoint;

                            // Calculate the base-to-height ratio to determine if it's a miniature or token
                            candidate.BaseToHeightRatio = baseDiameter / Math.Max(1, highestPoint);

                            miniatureCandidates.Add(candidate);
                        }
                    }
                }
            }

            // Process candidates into tokens
            foreach (var candidate in miniatureCandidates)
            {
                // Skip if too large
                if (candidate.AllPoints.Count >= MAX_TOKEN_SIZE)
                    continue;

                // Calculate average depth of the base (not the full miniature)
                double sumDepth = 0;
                foreach (Point pt in candidate.BasePoints)
                {
                    int idx = (int)pt.Y * depthWidth + (int)pt.X;
                    sumDepth += depthData[idx];
                }
                double avgBaseDepth = sumDepth / candidate.BasePoints.Count;

                // Create token
                TTRPGToken token = new TTRPGToken
                {
                    Position = candidate.BaseCenter,
                    Depth = (ushort)avgBaseDepth,
                    DiameterPixels = candidate.BaseDiameter,
                    HeightMm = (ushort)candidate.MaxHeight,
                    Points = candidate.AllPoints
                };

                // Improved classification logic
                if (candidate.BaseToHeightRatio < miniatureBaseThreshold)
                {
                    // Taller than it is wide = likely miniature
                    token.Type = TokenType.Miniature;
                }
                else if (candidate.MaxHeight < 15)
                {
                    // Flat tokens based on size
                    if (candidate.BaseDiameter < 25)
                        token.Type = TokenType.SmallToken;
                    else if (candidate.BaseDiameter < 50)
                        token.Type = TokenType.MediumToken;
                    else
                        token.Type = TokenType.LargeToken;
                }
                else
                {
                    // Medium height objects - could be dice or other game pieces
                    if (candidate.BaseDiameter < 30 && candidate.MaxHeight > 15 && candidate.MaxHeight < 30)
                        token.Type = TokenType.Dice;
                    else
                        token.Type = TokenType.LargeToken;
                }

                detectedTokens.Add(token);
            }

            // Track tokens between frames
            TrackTokensOverTime(previousTokens, detectedTokens);

            // Map to real-world coordinates if available
            if (kinectSensor != null && kinectSensor.CoordinateMapper != null)
            {
                //MapTokensToRealWorld();
            }

            // Update the UI
            this.Dispatcher.Invoke(() => {
                UpdateTokenOverlay();
                TokenCountText = $"{detectedTokens.Count} objects detected";
            });
        }

        // Enhanced MiniatureCandidate class with additional properties
        private class MiniatureCandidate
        {
            public Point BaseCenter { get; set; }
            public double BaseDiameter { get; set; }
            public List<Point> BasePoints { get; set; } = new List<Point>();
            public List<Point> AllPoints { get; set; } = new List<Point>();
            public int MaxHeight { get; set; }
            public double BaseToHeightRatio { get; set; } // For distinguishing miniatures from tokens
        }

        /// <summary>
        /// Enhanced calibration dialog with more precise miniature detection settings
        /// </summary>
        private void CalibrateTokens_Click(object sender, RoutedEventArgs e)
        {
            // Create a calibration dialog
            var dialog = new Window
            {
                Title = "Token & Miniature Calibration",
                Width = 500,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create the calibration UI with scrolling
            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel { Margin = new Thickness(10) };
            scrollViewer.Content = panel;

            // Header and instructions
            panel.Children.Add(new TextBlock
            {
                Text = "Configure detection settings for both flat tokens and 3D miniatures.",
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Place representative miniatures and tokens on your table and adjust these settings until detection works optimally.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Table Detection Section
            panel.Children.Add(new TextBlock
            {
                Text = "TABLE DETECTION",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 5, 0, 5)
            });

            // Table ROI status
            var roiStatusText = new TextBlock
            {
                Text = hasValidROI ?
                    $"ROI Status: Valid ({detectedTableROI.Width:F0}x{detectedTableROI.Height:F0})" :
                    "ROI Status: Not defined (draw ROI on depth image)",
                Margin = new Thickness(0, 0, 0, 5)
            };
            panel.Children.Add(roiStatusText);

            // Table depth status
            var tableDepthStatusText = new TextBlock
            {
                Text = hasValidTableDepth ?
                    $"Table Depth: {tableDepth}mm (Valid)" :
                    "Table Depth: Not calibrated (define ROI)",
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(tableDepthStatusText);

            // Flat Token Settings
            panel.Children.Add(new TextBlock
            {
                Text = "FLAT TOKEN SETTINGS",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 15, 0, 5)
            });

            panel.Children.Add(new TextBlock { Text = "Minimum Height (mm):" });
            var minHeightSlider = new Slider
            {
                Minimum = 2,
                Maximum = 20,
                Value = MIN_TOKEN_HEIGHT,
                TickFrequency = 1,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var minHeightLabel = new TextBlock { Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm" };
            minHeightSlider.ValueChanged += (s, args) =>
            {
                MIN_TOKEN_HEIGHT = (int)args.NewValue;
                minHeightLabel.Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm";
            };

            panel.Children.Add(minHeightSlider);
            panel.Children.Add(minHeightLabel);

            // Miniature Settings
            panel.Children.Add(new TextBlock
            {
                Text = "MINIATURE SETTINGS",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 15, 0, 5)
            });

            panel.Children.Add(new TextBlock { Text = "Maximum Height (mm):" });
            var maxHeightSlider = new Slider
            {
                Minimum = 30,
                Maximum = 200,
                Value = maxMiniatureHeight,
                TickFrequency = 10,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var maxHeightLabel = new TextBlock { Text = $"Max Height: {maxMiniatureHeight}mm" };
            maxHeightSlider.ValueChanged += (s, args) =>
            {
                maxMiniatureHeight = (int)args.NewValue;
                maxHeightLabel.Text = $"Max Height: {maxMiniatureHeight}mm";
            };

            panel.Children.Add(maxHeightSlider);
            panel.Children.Add(maxHeightLabel);

            // Base-to-Height Ratio Slider
            panel.Children.Add(new TextBlock { Text = "Miniature Classification Ratio:" });
            var ratioSlider = new Slider
            {
                Minimum = 0.2,
                Maximum = 2.0,
                Value = miniatureBaseThreshold,
                TickFrequency = 0.1,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var ratioLabel = new TextBlock
            {
                Text = $"Base-to-Height Ratio: {miniatureBaseThreshold:F1} (Lower = more miniatures detected)"
            };

            ratioSlider.ValueChanged += (s, args) =>
            {
                miniatureBaseThreshold = args.NewValue;
                ratioLabel.Text = $"Base-to-Height Ratio: {miniatureBaseThreshold:F1} (Lower = more miniatures detected)";
            };

            panel.Children.Add(ratioSlider);
            panel.Children.Add(ratioLabel);

            // Detection Sensitivity
            panel.Children.Add(new TextBlock { Text = "Detection Sensitivity:" });
            var sensitivitySlider = new Slider
            {
                Minimum = 3,
                Maximum = 30,
                Value = miniDetectionSensitivity,
                TickFrequency = 1,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var sensitivityLabel = new TextBlock { Text = $"Point Threshold: {miniDetectionSensitivity} (Lower = more sensitive)" };
            sensitivitySlider.ValueChanged += (s, args) =>
            {
                miniDetectionSensitivity = (int)args.NewValue;
                sensitivityLabel.Text = $"Point Threshold: {miniDetectionSensitivity} (Lower = more sensitive)";
            };

            panel.Children.Add(sensitivitySlider);
            panel.Children.Add(sensitivityLabel);

            // Add test button to run a detection cycle
            var testButton = new Button
            {
                Content = "Test Current Settings",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            testButton.Click += (s, args) =>
            {
                // Update ROI and table depth status
                roiStatusText.Text = hasValidROI ?
                    $"ROI Status: Valid ({detectedTableROI.Width:F0}x{detectedTableROI.Height:F0})" :
                    "ROI Status: Not defined (draw ROI on depth image)";

                tableDepthStatusText.Text = hasValidTableDepth ?
                    $"Table Depth: {tableDepth}mm (Valid)" :
                    "Table Depth: Not calibrated (define ROI)";

                // Force a detection cycle
                DetectTokensEnhanced();
                StatusText = $"Detected {detectedTokens.Count} objects with current settings";
            };

            panel.Children.Add(testButton);

            // Add buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var saveButton = new Button
            {
                Content = "Save & Close",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            saveButton.Click += (s, args) =>
            {
                // Save the calibration settings
                SaveSettings();
                dialog.Close();
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(10, 5, 10, 5)
            };

            cancelButton.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = scrollViewer;
            dialog.ShowDialog();

            StatusText = "Token and miniature calibration updated";
        }

        /// <summary>
        /// Updates detection settings with new calibrated values
        /// </summary>
        protected void OnPropertyChanged(string name)
        {
            // Make sure base implementation still runs
            //base.OnPropertyChanged(name);

            // Update detection conditions
            if (name == "TableDepthText" || name == "StatusText")
            {
                IsReadyForTokenDetection();
            }
        }
    }
}