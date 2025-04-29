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
        private void DetectTokensEnhancedOld()
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

        private void DetectTokensEnhanced()
        {
            if (!IsReadyForTokenDetection())
            {
                if (trackTokens && (detectedTokens.Count > 0))
                {
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

            // Create a height map with better noise filtering
            int[,] heightMap = new int[depthWidth, depthHeight];
            bool[,] processed = new bool[depthWidth, depthHeight];

            // Add median filtering for the height map
            List<int>[,] heightSamples = new List<int>[depthWidth, depthHeight];

            // First, collect height samples in a neighborhood
            int neighborhoodSize = 2; // 5x5 neighborhood

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
                        // Collect height samples for this pixel and its neighborhood
                        for (int ny = Math.Max(0, y - neighborhoodSize); ny <= Math.Min(depthHeight - 1, y + neighborhoodSize); ny++)
                        {
                            for (int nx = Math.Max(0, x - neighborhoodSize); nx <= Math.Min(depthWidth - 1, x + neighborhoodSize); nx++)
                            {
                                if (heightSamples[nx, ny] == null)
                                    heightSamples[nx, ny] = new List<int>();

                                heightSamples[nx, ny].Add(heightAboveTable);
                            }
                        }
                    }
                }
            }

            // Apply median filtering to reduce noise
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    var samples = heightSamples[x, y];
                    if (samples != null && samples.Count > 0)
                    {
                        samples.Sort();
                        int medianHeight = samples[samples.Count / 2];

                        // Apply height threshold to further reduce noise
                        if (medianHeight >= MIN_TOKEN_HEIGHT)
                        {
                            heightMap[x, y] = medianHeight;
                        }
                    }
                }
            }

            // Detect tokens using flood-fill with edge-aware parameters
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight || processed[x, y])
                        continue;

                    int height = heightMap[x, y];

                    // Skip pixels with no height or too small
                    if (height <= 0)
                        continue;

                    // Check if this is close to the edge of the ROI - more strict near edges
                    bool isNearEdge = x < detectedTableROI.X + 20 || x > detectedTableROI.X + detectedTableROI.Width - 20 ||
                                     y < detectedTableROI.Y + 20 || y > detectedTableROI.Y + detectedTableROI.Height - 20;

                    // Flood fill with size threshold to reduce noise
                    // Stricter requirement near edges
                    int minPointsRequired = isNearEdge ? miniDetectionSensitivity * 2 : miniDetectionSensitivity;

                    // Start flood fill for object detection
                    MiniatureCandidate candidate = new MiniatureCandidate();
                    Queue<Point> queue = new Queue<Point>();
                    Dictionary<int, List<Point>> heightLayers = new Dictionary<int, List<Point>>();

                    queue.Enqueue(new Point(x, y));
                    processed[x, y] = true;

                    heightLayers[height] = new List<Point> { new Point(x, y) };
                    int highestPoint = height;

                    // Flood fill with adaptive height tolerance
                    while (queue.Count > 0)
                    {
                        Point p = queue.Dequeue();
                        int px = (int)p.X;
                        int py = (int)p.Y;
                        int currentHeight = heightMap[px, py];

                        // Check 8-connected neighbors
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

                                // Skip invalid heights
                                if (neighborHeight <= 0)
                                    continue;

                                // Use adaptive height tolerance based on current height
                                // Lower tolerance for base, higher for body
                                int heightTolerance;

                                if (currentHeight < 15) // For bases (flatter parts)
                                    heightTolerance = 5;
                                else if (currentHeight < 30) // For mid-height
                                    heightTolerance = 10;
                                else // For taller parts
                                    heightTolerance = 15;

                                if (Math.Abs(neighborHeight - currentHeight) <= heightTolerance)
                                {
                                    queue.Enqueue(new Point(nx, ny));
                                    processed[nx, ny] = true;

                                    // Track in height layers
                                    if (!heightLayers.ContainsKey(neighborHeight))
                                        heightLayers[neighborHeight] = new List<Point>();

                                    heightLayers[neighborHeight].Add(new Point(nx, ny));

                                    // Track highest point
                                    if (neighborHeight > highestPoint)
                                        highestPoint = neighborHeight;
                                }
                            }
                        }
                    }

                    // Count total points
                    int totalPoints = heightLayers.Values.Sum(list => list.Count);

                    // Skip small objects
                    if (totalPoints < minPointsRequired)
                        continue;

                    // Extract base layer (lower height points)
                    var sortedLayers = heightLayers.OrderBy(kv => kv.Key).ToList();
                    List<Point> basePoints = new List<Point>();
                    List<Point> allPoints = new List<Point>();

                    // Use lowest 30% of points for base
                    int baseLayerCount = Math.Max(1, (int)(sortedLayers.Count * 0.3));

                    for (int i = 0; i < baseLayerCount && i < sortedLayers.Count; i++)
                    {
                        basePoints.AddRange(sortedLayers[i].Value);
                    }

                    // Collect all points
                    foreach (var layer in sortedLayers)
                    {
                        allPoints.AddRange(layer.Value);
                    }

                    // Calculate base metrics
                    if (basePoints.Count >= MIN_BASE_SIZE)
                    {
                        // Calculate centroid
                        double sumX = 0, sumY = 0;
                        foreach (var pt in basePoints)
                        {
                            sumX += pt.X;
                            sumY += pt.Y;
                        }
                        double baseX = sumX / basePoints.Count;
                        double baseY = sumY / basePoints.Count;

                        // Calculate base size
                        double minX = double.MaxValue, maxX = double.MinValue;
                        double minY = double.MaxValue, maxY = double.MinValue;

                        foreach (var pt in basePoints)
                        {
                            minX = Math.Min(minX, pt.X);
                            maxX = Math.Max(maxX, pt.X);
                            minY = Math.Min(minY, pt.Y);
                            maxY = Math.Max(maxY, pt.Y);
                        }

                        double baseWidth = maxX - minX;
                        double baseHeight = maxY - minY;
                        double baseDiameter = Math.Max(baseWidth, baseHeight);

                        // Create candidate
                        candidate.BaseCenter = new Point(baseX, baseY);
                        candidate.BaseDiameter = baseDiameter;
                        candidate.BasePoints = basePoints;
                        candidate.AllPoints = allPoints;
                        candidate.MaxHeight = highestPoint;
                        candidate.BaseToHeightRatio = baseDiameter / Math.Max(1, highestPoint);

                        // Skip candidates that appear to be noise (too small or too large)
                        if (allPoints.Count >= MIN_TOKEN_SIZE && allPoints.Count <= MAX_TOKEN_SIZE)
                        {
                            // Create token
                            TTRPGToken token = new TTRPGToken
                            {
                                Position = candidate.BaseCenter,
                                DiameterPixels = candidate.BaseDiameter,
                                HeightMm = (ushort)candidate.MaxHeight,
                                Points = candidate.AllPoints
                            };

                            // Calculate average depth
                            double sumDepth = 0;
                            int validDepths = 0;

                            foreach (Point pt in candidate.BasePoints)
                            {
                                int idx = (int)pt.Y * depthWidth + (int)pt.X;
                                if (idx >= 0 && idx < depthData.Length && depthData[idx] > 0)
                                {
                                    sumDepth += depthData[idx];
                                    validDepths++;
                                }
                            }

                            if (validDepths > 0)
                            {
                                token.Depth = (ushort)(sumDepth / validDepths);
                            }
                            else
                            {
                                token.Depth = tableDepth;
                            }

                            // Classify token
                            ClassifyToken(token, candidate);

                            // Add to detected tokens
                            detectedTokens.Add(token);
                        }
                    }
                }
            }

            // Track tokens between frames
            TrackTokensOverTime(previousTokens, detectedTokens);

            // Map to real-world coordinates
            MapTokensToRealWorld();

            // Update UI
            this.Dispatcher.Invoke(() => {
                UpdateTokenOverlay();
                TokenCountText = $"{detectedTokens.Count} objects detected";
            });
        }

        // Improved token classification
        private void ClassifyToken(TTRPGToken token, MiniatureCandidate candidate)
        {
            // Use base-to-height ratio as primary classifier
            if (candidate.BaseToHeightRatio < miniatureBaseThreshold)
            {
                // Tall and narrow - likely a miniature
                token.Type = TokenType.Miniature;
            }
            else if (candidate.MaxHeight < 15)
            {
                // Flat token classification by diameter
                if (candidate.BaseDiameter < 25)
                    token.Type = TokenType.SmallToken;
                else if (candidate.BaseDiameter < 50)
                    token.Type = TokenType.MediumToken;
                else
                    token.Type = TokenType.LargeToken;
            }
            else if (candidate.MaxHeight < 30 && candidate.BaseDiameter < 30)
            {
                // Medium height, small diameter - likely a die
                token.Type = TokenType.Dice;
            }
            else if (candidate.MaxHeight > 30 && candidate.MaxHeight < 60 &&
                     candidate.BaseToHeightRatio < 1.5) // Still somewhat tall
            {
                // Medium-tall object - could be a larger miniature
                token.Type = TokenType.Miniature;
            }
            else
            {
                // Large object - default to large token
                token.Type = TokenType.LargeToken;
            }
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

        /// <summary>
        /// Enhanced method to detect the table surface with improved noise handling
        /// </summary>
        private void EnhancedTableSurfaceDetection()
        {
            if (depthData == null || depthData.Length == 0)
                return;

            // Define search parameters
            ushort minDepth = 500;   // 0.5m
            ushort maxDepth = 3000;  // 3m
            int sampleStep = 4;      // Sample every 4th pixel for performance
            int depthBinSize = 25;   // 25mm quantization to group similar depths

            // Use a histogram approach with weighted spatial coherence
            Dictionary<ushort, PlaneCandidate> planeCandidates = new Dictionary<ushort, PlaneCandidate>();

            // First pass - build histogram of depth values
            for (int y = 0; y < depthHeight; y += sampleStep)
            {
                for (int x = 0; x < depthWidth; x += sampleStep)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // Skip invalid depths
                    if (depth < minDepth || depth > maxDepth)
                        continue;

                    // Quantize depth to find similar depths
                    ushort quantizedDepth = (ushort)(depth / depthBinSize * depthBinSize);

                    // Add to plane candidates
                    if (!planeCandidates.ContainsKey(quantizedDepth))
                    {
                        planeCandidates[quantizedDepth] = new PlaneCandidate
                        {
                            Depth = quantizedDepth,
                            Points = new List<Point>(),
                            WeightedScore = 0
                        };
                    }

                    planeCandidates[quantizedDepth].Points.Add(new Point(x, y));
                }
            }

            // Second pass - calculate scores for each candidate plane
            foreach (var candidate in planeCandidates.Values)
            {
                // Skip candidates with too few points
                if (candidate.Points.Count < 100)
                    continue;

                // Calculate spatial distribution
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                double sumX = 0, sumY = 0;

                foreach (var pt in candidate.Points)
                {
                    minX = Math.Min(minX, pt.X);
                    maxX = Math.Max(maxX, pt.X);
                    minY = Math.Min(minY, pt.Y);
                    maxY = Math.Max(maxY, pt.Y);
                    sumX += pt.X;
                    sumY += pt.Y;
                }

                double width = maxX - minX;
                double height = maxY - minY;
                double area = width * height;
                double centerX = sumX / candidate.Points.Count;
                double centerY = sumY / candidate.Points.Count;

                // Calculate density and coherence score
                double density = candidate.Points.Count / area;
                double coverageRatio = candidate.Points.Count / (width * height / (sampleStep * sampleStep));

                // Calculate distance to center of frame (prefer centered tables)
                double distToCenter = Math.Sqrt(
                    Math.Pow((centerX - depthWidth / 2.0) / depthWidth, 2) +
                    Math.Pow((centerY - depthHeight / 2.0) / depthHeight, 2));

                // Calculate depth variance (lower is better)
                double depthVariance = CalculateDepthVariance(candidate.Points);

                // Consolidate metrics into a single score
                // Higher score = more likely to be a table surface
                candidate.WeightedScore = candidate.Points.Count *  // More points is better
                                         coverageRatio *            // Higher coverage is better
                                         (1.0 - distToCenter) *     // Closer to center is better
                                         (1.0 / (depthVariance + 1.0)); // Lower variance is better

                // Store candidate region
                candidate.Region = new Rect(minX, minY, width, height);
            }

            // Find the best candidate
            PlaneCandidate bestCandidate = null;
            double bestScore = 0;

            foreach (var candidate in planeCandidates.Values)
            {
                if (candidate.WeightedScore > bestScore)
                {
                    bestScore = candidate.WeightedScore;
                    bestCandidate = candidate;
                }
            }

            // If we found a good candidate
            if (bestCandidate != null && bestScore > 0)
            {
                // Update table depth with median value
                List<ushort> actualDepths = new List<ushort>();
                foreach (var pt in bestCandidate.Points.Take(Math.Min(1000, bestCandidate.Points.Count)))
                {
                    int idx = (int)(pt.Y * depthWidth + pt.X);
                    actualDepths.Add(depthData[idx]);
                }

                actualDepths.Sort();
                ushort medianDepth = actualDepths[actualDepths.Count / 2];

                // Apply temporal smoothing
                depthHistory.Enqueue(medianDepth);
                while (depthHistory.Count > maxHistorySize)
                    depthHistory.Dequeue();

                ushort[] depthArray = depthHistory.ToArray();
                Array.Sort(depthArray);
                tableDepth = depthArray[depthArray.Length / 2];

                // Update ROI
                detectedTableROI = bestCandidate.Region;
                hasValidROI = true;
                hasValidTableDepth = true;

                // Update UI
                this.Dispatcher.Invoke(() =>
                {
                    StatusText = $"Table detected at {tableDepth}mm (score: {bestScore:F1})";
                    TableDepthText = $"{tableDepth} mm";
                });

                // Initialize height grid if enabled
                if (showHeightGrid)
                {
                    InitializeHeightGrid();
                }
            }
        }

        /// <summary>
        /// Calculate depth variance for a set of points
        /// </summary>
        private double CalculateDepthVariance(List<Point> points, int maxSamples = 100)
        {
            // Sample a subset of points for efficiency
            int sampleCount = Math.Min(maxSamples, points.Count);
            int step = Math.Max(1, points.Count / sampleCount);

            List<ushort> depths = new List<ushort>();
            for (int i = 0; i < points.Count; i += step)
            {
                int idx = (int)(points[i].Y * depthWidth + points[i].X);
                if (idx >= 0 && idx < depthData.Length)
                {
                    depths.Add(depthData[idx]);
                }
            }

            if (depths.Count < 3)
                return double.MaxValue;

            // Calculate mean
            double sum = 0;
            foreach (var depth in depths)
            {
                sum += depth;
            }
            double mean = sum / depths.Count;

            // Calculate variance
            double sumSquaredDiff = 0;
            foreach (var depth in depths)
            {
                double diff = depth - mean;
                sumSquaredDiff += diff * diff;
            }

            return sumSquaredDiff / depths.Count;
        }

        /// <summary>
        /// Class to store plane candidate information
        /// </summary>
        private class PlaneCandidate
        {
            public ushort Depth { get; set; }
            public List<Point> Points { get; set; }
            public double WeightedScore { get; set; }
            public Rect Region { get; set; }
        }

        /// <summary>
        /// Enhanced token detection with adaptive thresholding
        /// </summary>
        private void EnhancedTokenDetection()
        {
            if (!IsReadyForTokenDetection())
                return;

            // Store previous tokens for tracking
            List<TTRPGToken> previousTokens = new List<TTRPGToken>(detectedTokens);
            detectedTokens.Clear();

            // Apply a bilateral filter to the depth data to reduce noise while preserving edges
            ushort[] filteredDepthData = BilateralFilterDepth(depthData, 2, 10, 30);

            // Create a height map with median filtering
            int[,] heightMap = new int[depthWidth, depthHeight];
            bool[,] processed = new bool[depthWidth, depthHeight];

            // Fill height map within ROI only
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    int idx = y * depthWidth + x;
                    ushort depth = filteredDepthData[idx];

                    // Skip invalid or background depths
                    if (depth == 0 || depth >= tableDepth)
                        continue;

                    // Calculate height above table
                    int heightAboveTable = tableDepth - depth;

                    // Only consider heights within our miniature detection range
                    if (heightAboveTable >= MIN_TOKEN_HEIGHT && heightAboveTable <= maxMiniatureHeight)
                    {
                        heightMap[x, y] = heightAboveTable;
                    }
                }
            }

            // Apply connected component labeling for object detection
            Dictionary<int, List<Point>> connectedComponents = FindConnectedComponents(heightMap);

            // Convert components to token candidates
            foreach (var component in connectedComponents.Values)
            {
                // Skip components that are too small
                if (component.Count < miniDetectionSensitivity)
                    continue;

                // Skip if too large
                if (component.Count > MAX_TOKEN_SIZE)
                    continue;

                // Calculate token metrics
                CalculateTokenFromComponent(component, heightMap, filteredDepthData);
            }

            // Track tokens between frames
            TrackTokensOverTime(previousTokens, detectedTokens);

            // Map to real-world coordinates
            MapTokensToRealWorld();

            // Update UI
            this.Dispatcher.Invoke(() =>
            {
                UpdateTokenOverlay();
                TokenCountText = $"{detectedTokens.Count} objects detected";
            });
        }

        /// <summary>
        /// Apply a bilateral filter to depth data to reduce noise while preserving edges
        /// </summary>
        private ushort[] BilateralFilterDepth(ushort[] inputDepth, int radius, double sigmaSpace, double sigmaDepth)
        {
            ushort[] output = new ushort[inputDepth.Length];

            // Apply filter only within ROI for performance
            int roiX = (int)Math.Max(radius, detectedTableROI.X);
            int roiY = (int)Math.Max(radius, detectedTableROI.Y);
            int roiWidth = (int)Math.Min(depthWidth - radius, detectedTableROI.X + detectedTableROI.Width);
            int roiHeight = (int)Math.Min(depthHeight - radius, detectedTableROI.Y + detectedTableROI.Height);

            // Copy input to output for out-of-ROI areas
            Array.Copy(inputDepth, output, inputDepth.Length);

            // Apply filter within ROI
            for (int y = roiY; y < roiHeight; y++)
            {
                for (int x = roiX; x < roiWidth; x++)
                {
                    int centerIdx = y * depthWidth + x;
                    ushort centerDepth = inputDepth[centerIdx];

                    // Skip invalid depths
                    if (centerDepth == 0)
                    {
                        output[centerIdx] = 0;
                        continue;
                    }

                    double weightSum = 0;
                    double valueSum = 0;

                    // Process neighborhood
                    for (int ny = -radius; ny <= radius; ny++)
                    {
                        for (int nx = -radius; nx <= radius; nx++)
                        {
                            int neighborX = x + nx;
                            int neighborY = y + ny;
                            int neighborIdx = neighborY * depthWidth + neighborX;

                            ushort neighborDepth = inputDepth[neighborIdx];

                            // Skip invalid neighbors
                            if (neighborDepth == 0)
                                continue;

                            // Calculate spatial and depth weights
                            double spatialDist = Math.Sqrt(nx * nx + ny * ny);
                            double depthDist = Math.Abs(centerDepth - neighborDepth);

                            double spatialWeight = Math.Exp(-(spatialDist * spatialDist) / (2 * sigmaSpace * sigmaSpace));
                            double depthWeight = Math.Exp(-(depthDist * depthDist) / (2 * sigmaDepth * sigmaDepth));

                            double weight = spatialWeight * depthWeight;

                            weightSum += weight;
                            valueSum += weight * neighborDepth;
                        }
                    }

                    // Calculate weighted average
                    if (weightSum > 0)
                    {
                        output[centerIdx] = (ushort)(valueSum / weightSum);
                    }
                    else
                    {
                        output[centerIdx] = centerDepth;
                    }
                }
            }

            return output;
        }

        /// <summary>
        /// Find connected components in the height map
        /// </summary>
        private Dictionary<int, List<Point>> FindConnectedComponents(int[,] heightMap)
        {
            int width = heightMap.GetLength(0);
            int height = heightMap.GetLength(1);

            // Initialize label map and processed flag
            int[,] labelMap = new int[width, height];
            bool[,] processed = new bool[width, height];

            // Initialize result dictionary
            Dictionary<int, List<Point>> components = new Dictionary<int, List<Point>>();
            int nextLabel = 1;

            // Process each pixel within ROI
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= width || y < 0 || y >= height || processed[x, y])
                        continue;

                    height = heightMap[x, y];

                    // Skip pixels with no height
                    if (height <= 0)
                        continue;

                    // Start a new component
                    int label = nextLabel++;
                    components[label] = new List<Point>();

                    // Use flood fill to find connected component
                    Queue<Point> queue = new Queue<Point>();
                    queue.Enqueue(new Point(x, y));
                    processed[x, y] = true;
                    labelMap[x, y] = label;
                    components[label].Add(new Point(x, y));

                    while (queue.Count > 0)
                    {
                        Point p = queue.Dequeue();
                        int px = (int)p.X;
                        int py = (int)p.Y;
                        int pHeight = heightMap[px, py];

                        // Check 8-connected neighbors
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = px + dx;
                                int ny = py + dy;

                                // Skip out of bounds or already processed
                                if (nx < 0 || nx >= width || ny < 0 || ny >= height || processed[nx, ny])
                                    continue;

                                int neighborHeight = heightMap[nx, ny];

                                // Skip invalid heights
                                if (neighborHeight <= 0)
                                    continue;

                                // Use adaptive height tolerance based on current height
                                int heightTolerance;
                                if (pHeight < 15) // For bases (flatter parts)
                                    heightTolerance = 5;
                                else if (pHeight < 30) // For mid-height
                                    heightTolerance = 10;
                                else // For taller parts
                                    heightTolerance = 15;

                                // Check if neighbor belongs to this component
                                if (Math.Abs(neighborHeight - pHeight) <= heightTolerance)
                                {
                                    queue.Enqueue(new Point(nx, ny));
                                    processed[nx, ny] = true;
                                    labelMap[nx, ny] = label;
                                    components[label].Add(new Point(nx, ny));
                                }
                            }
                        }
                    }
                }
            }

            return components;
        }

        /// <summary>
        /// Calculate token from connected component
        /// </summary>
        private void CalculateTokenFromComponent(List<Point> component, int[,] heightMap, ushort[] depthData)
        {
            if (component.Count < miniDetectionSensitivity)
                return;

            // Extract height layers for better analysis
            Dictionary<int, List<Point>> heightLayers = new Dictionary<int, List<Point>>();
            int maxHeight = 0;

            foreach (var point in component)
            {
                int x = (int)point.X;
                int y = (int)point.Y;
                int height = heightMap[x, y];

                if (height > 0)
                {
                    if (!heightLayers.ContainsKey(height))
                        heightLayers[height] = new List<Point>();

                    heightLayers[height].Add(point);
                    maxHeight = Math.Max(maxHeight, height);
                }
            }

            // Sort height layers
            var sortedLayers = heightLayers.OrderBy(kv => kv.Key).ToList();

            // Identify base layer (lowest 30% of points)
            List<Point> basePoints = new List<Point>();
            int totalPoints = component.Count;
            int baseLayerLimit = Math.Max(1, (int)(sortedLayers.Count * 0.3));

            for (int i = 0; i < baseLayerLimit && i < sortedLayers.Count; i++)
            {
                basePoints.AddRange(sortedLayers[i].Value);
            }

            // Calculate base metrics
            if (basePoints.Count >= MIN_BASE_SIZE)
            {
                // Calculate centroid
                double sumX = 0, sumY = 0;
                foreach (var pt in basePoints)
                {
                    sumX += pt.X;
                    sumY += pt.Y;
                }
                double baseX = sumX / basePoints.Count;
                double baseY = sumY / basePoints.Count;

                // Calculate base size using RANSAC circle fitting for robustness
                (double centerX, double centerY, double radius) = FitCircleToPoints(basePoints);

                // Calculate average depth of the base
                double sumDepth = 0;
                int validDepths = 0;

                foreach (Point pt in basePoints)
                {
                    int idx = (int)pt.Y * depthWidth + (int)pt.X;
                    if (idx >= 0 && idx < depthData.Length && depthData[idx] > 0)
                    {
                        sumDepth += depthData[idx];
                        validDepths++;
                    }
                }

                ushort avgBaseDepth = validDepths > 0 ?
                    (ushort)(sumDepth / validDepths) : (ushort)tableDepth;

                // Calculate base-to-height ratio
                double baseToHeightRatio = 2 * radius / Math.Max(1, maxHeight);

                // Create token
                TTRPGToken token = new TTRPGToken
                {
                    Position = new Point(centerX, centerY), // Use fitted circle center
                    Depth = avgBaseDepth,
                    DiameterPixels = radius * 2,
                    HeightMm = (ushort)maxHeight,
                    Points = component
                };

                // Classify token
                if (baseToHeightRatio < miniatureBaseThreshold)
                {
                    // Taller than it is wide = likely miniature
                    token.Type = TokenType.Miniature;
                }
                else if (maxHeight < 15)
                {
                    // Flat tokens based on size
                    if (radius * 2 < 25)
                        token.Type = TokenType.SmallToken;
                    else if (radius * 2 < 50)
                        token.Type = TokenType.MediumToken;
                    else
                        token.Type = TokenType.LargeToken;
                }
                else if (maxHeight < 30 && radius * 2 < 30)
                {
                    // Medium height, small base = likely dice
                    token.Type = TokenType.Dice;
                }
                else
                {
                    // Default to large token
                    token.Type = TokenType.LargeToken;
                }

                // Add to detected tokens
                detectedTokens.Add(token);
            }
        }

        /// <summary>
        /// Fit circle to points using RANSAC for robustness
        /// </summary>
        private (double centerX, double centerY, double radius) FitCircleToPoints(List<Point> points)
        {
            // Implementation of RANSAC circle fitting
            Random random = new Random();
            int maxIterations = 50;
            int minPointsForFit = 3;
            double inlierThreshold = 3.0; // Pixels

            double bestCenterX = 0;
            double bestCenterY = 0;
            double bestRadius = 0;
            int bestInlierCount = 0;

            // Safety check
            if (points.Count < minPointsForFit)
            {
                // Calculate simple bounding circle
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;

                foreach (var pt in points)
                {
                    minX = Math.Min(minX, pt.X);
                    maxX = Math.Max(maxX, pt.X);
                    minY = Math.Min(minY, pt.Y);
                    maxY = Math.Max(maxY, pt.Y);
                }

                double centerX = (minX + maxX) / 2;
                double centerY = (minY + maxY) / 2;
                double radius = Math.Max(maxX - minX, maxY - minY) / 2;
                return (centerX, centerY, radius);
            }

            // RANSAC iterations
            for (int iter = 0; iter < maxIterations; iter++)
            {
                // Randomly select 3 points
                List<Point> sample = new List<Point>();
                List<int> indices = new List<int>();

                while (sample.Count < minPointsForFit)
                {
                    int idx = random.Next(points.Count);
                    if (!indices.Contains(idx))
                    {
                        indices.Add(idx);
                        sample.Add(points[idx]);
                    }
                }

                // Fit circle to 3 points
                (double centerX, double centerY, double radius) = FitCircleTo3Points(sample[0], sample[1], sample[2]);

                // Count inliers
                int inlierCount = 0;
                foreach (var pt in points)
                {
                    double distance = Math.Abs(
                        Math.Sqrt(Math.Pow(pt.X - centerX, 2) + Math.Pow(pt.Y - centerY, 2)) - radius);

                    if (distance < inlierThreshold)
                        inlierCount++;
                }

                // Check if this is the best model so far
                if (inlierCount > bestInlierCount)
                {
                    bestInlierCount = inlierCount;
                    bestCenterX = centerX;
                    bestCenterY = centerY;
                    bestRadius = radius;
                }
            }

            // Refine fit using all inliers
            List<Point> inliers = new List<Point>();
            foreach (var pt in points)
            {
                double distance = Math.Abs(
                    Math.Sqrt(Math.Pow(pt.X - bestCenterX, 2) + Math.Pow(pt.Y - bestCenterY, 2)) - bestRadius);

                if (distance < inlierThreshold)
                    inliers.Add(pt);
            }

            // Calculate refined circle using all inliers
            if (inliers.Count >= 5) // Need more points for stable algebraic fit
            {
                (bestCenterX, bestCenterY, bestRadius) = FitCircleAlgebraic(inliers);
            }

            return (bestCenterX, bestCenterY, bestRadius);
        }

        /// <summary>
        /// Fit circle to exactly 3 points
        /// </summary>
        private (double centerX, double centerY, double radius) FitCircleTo3Points(Point p1, Point p2, Point p3)
        {
            double x1 = p1.X;
            double y1 = p1.Y;
            double x2 = p2.X;
            double y2 = p2.Y;
            double x3 = p3.X;
            double y3 = p3.Y;
            double centerX;
            double centerY;
            double radius;

            // Handle collinear or duplicate points
            double epsilon = 1e-10;
            double det = (x1 - x2) * (y2 - y3) - (x2 - x3) * (y1 - y2);
            if (Math.Abs(det) < epsilon)
            {
                // Fallback: return bounding circle
                double minX = Math.Min(Math.Min(x1, x2), x3);
                double maxX = Math.Max(Math.Max(x1, x2), x3);
                double minY = Math.Min(Math.Min(y1, y2), y3);
                double maxY = Math.Max(Math.Max(y1, y2), y3);

                centerX = (minX + maxX) / 2;
                centerY = (minY + maxY) / 2;
                radius = Math.Sqrt(
                    Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2)) / 2;

                return (centerX, centerY, radius);
            }

            // Calculate center using perpendicular bisectors
            double a = x1 * x1 + y1 * y1;
            double b = x2 * x2 + y2 * y2;
            double c = x3 * x3 + y3 * y3;

            double d = 2 * ((x1 * (y2 - y3)) + (x2 * (y3 - y1)) + (x3 * (y1 - y2)));
            double ux = ((a * (y2 - y3)) + (b * (y3 - y1)) + (c * (y1 - y2))) / d;
            double uy = ((a * (x3 - x2)) + (b * (x1 - x3)) + (c * (x2 - x1))) / d;

            centerX = ux;
            centerY = uy;
            radius = Math.Sqrt(Math.Pow(centerX - x1, 2) + Math.Pow(centerY - y1, 2));

            return (centerX, centerY, radius);
        }

        /// <summary>
        /// Fit circle to multiple points using algebraic method
        /// </summary>
        private (double centerX, double centerY, double radius) FitCircleAlgebraic(List<Point> points)
        {
            // Calculate centroid
            double sumX = 0, sumY = 0;
            foreach (var pt in points)
            {
                sumX += pt.X;
                sumY += pt.Y;
            }
            double meanX = sumX / points.Count;
            double meanY = sumY / points.Count;

            // Shift coordinates to center at origin to improve numerical stability
            double[] shiftedX = new double[points.Count];
            double[] shiftedY = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                shiftedX[i] = points[i].X - meanX;
                shiftedY[i] = points[i].Y - meanY;
            }

            // Compute moments
            double sumXX = 0, sumYY = 0, sumXY = 0, sumXXX = 0, sumXXY = 0, sumXYY = 0, sumYYY = 0;
            foreach (double x in shiftedX)
                foreach (double y in shiftedY)
                {
                    double xx = x * x;
                    double yy = y * y;
                    sumXX += xx;
                    sumYY += yy;
                    sumXY += x * y;
                    sumXXX += xx * x;
                    sumXXY += xx * y;
                    sumXYY += x * yy;
                    sumYYY += yy * y;
                }

            // Calculate matrix elements
            double A = points.Count;
            double B = 2 * sumXX + 2 * sumYY;
            double C = sumXXX + sumXYY;
            double D = sumXXY + sumYYY;
            double E = sumXX + sumYY;

            // Solve for parameters
            double det = A * E - B * B / 4;
            double centerX, centerY;

            if (Math.Abs(det) < 1e-10)
            {
                // Fallback: use centroid
                centerX = meanX;
                centerY = meanY;
            }
            else
            {
                centerX = meanX + (C * E - B * D / 2) / (2 * det);
                centerY = meanY + (A * D - B * C / 2) / (2 * det);
            }

            // Calculate radius
            double radius = 0;
            foreach (var pt in points)
            {
                double distance = Math.Sqrt(Math.Pow(pt.X - centerX, 2) + Math.Pow(pt.Y - centerY, 2));
                radius += distance;
            }
            radius /= points.Count;

            return (centerX, centerY, radius);
        }

        /// <summary>
        /// Map tokens to real world coordinates
        /// </summary>
        private void MapTokensToRealWorld()
        {
            if (kinectSensor == null || kinectSensor.CoordinateMapper == null || detectedTokens.Count == 0)
                return;

            var mapper = kinectSensor.CoordinateMapper;

            foreach (var token in detectedTokens)
            {
                try
                {
                    // Convert token position from depth space to camera space
                    DepthSpacePoint depthPoint = new DepthSpacePoint
                    {
                        X = (float)token.Position.X,
                        Y = (float)token.Position.Y
                    };

                    CameraSpacePoint cameraPoint = mapper.MapDepthPointToCameraSpace(depthPoint, token.Depth);

                    // Handle invalid points (might happen at edges)
                    if (float.IsInfinity(cameraPoint.X) || float.IsNaN(cameraPoint.X) ||
                        float.IsInfinity(cameraPoint.Y) || float.IsNaN(cameraPoint.Y) ||
                        float.IsInfinity(cameraPoint.Z) || float.IsNaN(cameraPoint.Z))
                    {
                        // Use previous position or default
                        if (token.RealWorldPosition == new Point3D(0, 0, 0))
                        {
                            // Default position - roughly center of the ROI
                            token.RealWorldPosition = new Point3D(0, 0, token.Depth / 1000.0);
                        }
                    }
                    else
                    {
                        // Store real-world position
                        token.RealWorldPosition = new Point3D(cameraPoint.X, cameraPoint.Y, cameraPoint.Z);
                    }

                    // Calculate real-world diameter based on depth
                    double distanceInMeters = token.Depth / 1000.0;

                    // Calculate pixel size at this distance using the known properties
                    // Kinect v2 has approximately 70.6° horizontal and 60° vertical field of view
                    double verticalFovRadians = 60.0 * Math.PI / 180.0; // 60 degrees in radians

                    double pixelSizeAtDistance = (2.0 * distanceInMeters * Math.Tan(verticalFovRadians / 2.0)) / depthHeight;

                    token.DiameterMeters = token.DiameterPixels * pixelSizeAtDistance;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error mapping token to real world: {ex.Message}");

                    // Use default position
                    if (token.RealWorldPosition == new Point3D(0, 0, 0))
                    {
                        token.RealWorldPosition = new Point3D(0, 0, token.Depth / 1000.0);
                        token.DiameterMeters = token.DiameterPixels / 1000.0; // Rough estimate
                    }
                }
            }
        }
    }
}