using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace TableDetector
{
    public partial class MainWindow
    {

        /// <summary>
        /// Detects objects (tokens) on the table surface
        /// </summary>
        /// <summary>
        /// Detects objects (tokens) on the table surface
        /// </summary>
        private void DetectTokens()
        {
            if (tableDepth == 0 || depthData == null || detectedTableROI.IsEmpty)
                return;

            // Store previous tokens for tracking and comparison
            List<TTRPGToken> previousTokens = new List<TTRPGToken>(detectedTokens);
            detectedTokens.Clear();

            // Create a temporary bitmap for token/miniature analysis
            bool[,] tokenMap = new bool[depthWidth, depthHeight];
            bool[,] visited = new bool[depthWidth, depthHeight];

            // First pass: identify all potential token/miniature pixels
            // This is the critical part for detecting miniatures with smaller footprints but greater height
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // For miniatures, we need to look for ANY point above the table
                    // with a reasonable height (not just noise)
                    if (depth > 0 && depth < tableDepth)
                    {
                        int heightAboveTable = tableDepth - depth;

                        // Include even smaller heights for miniature bases
                        // and ensure we capture taller miniatures too
                        if (heightAboveTable >= MIN_TOKEN_HEIGHT && heightAboveTable <= MAX_TOKEN_HEIGHT * 2)
                        {
                            tokenMap[x, y] = true;
                        }
                    }
                }
            }

            // Second pass: find connected components (tokens/miniatures)
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    // Skip out of bounds
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    // If this is a token pixel and hasn't been visited yet
                    if (tokenMap[x, y] && !visited[x, y])
                    {
                        // Start a new token/miniature
                        List<Point> tokenPoints = new List<Point>();
                        Queue<Point> queue = new Queue<Point>();

                        // Depth values for height analysis
                        List<ushort> depthValues = new List<ushort>();

                        // Add the starting point
                        queue.Enqueue(new Point(x, y));
                        visited[x, y] = true;

                        // Flood fill to find all connected points
                        while (queue.Count > 0)
                        {
                            Point p = queue.Dequeue();
                            int px = (int)p.X;
                            int py = (int)p.Y;

                            // Add to token points
                            tokenPoints.Add(p);

                            // Store the depth value for this point
                            int idx = py * depthWidth + px;
                            depthValues.Add(depthData[idx]);

                            // Check neighbors (8-connectivity)
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = px + dx;
                                    int ny = py + dy;

                                    // Skip out of bounds
                                    if (nx < 0 || nx >= depthWidth || ny < 0 || ny >= depthHeight)
                                        continue;

                                    // If neighbor is a token pixel and not visited yet
                                    if (tokenMap[nx, ny] && !visited[nx, ny])
                                    {
                                        queue.Enqueue(new Point(nx, ny));
                                        visited[nx, ny] = true;
                                    }
                                }
                            }
                        }

                        // Lower the threshold for miniatures based on height
                        int effectiveThreshold = tokenDetectionThreshold;

                        // If the object is tall, it might be a miniature with a small footprint
                        if (depthValues.Count > 0)
                        {
                            // Get the maximum height of this object
                            ushort minDepth = depthValues.Min(); // Highest point (smallest depth value)
                            int maxHeight = tableDepth - minDepth;

                            // If this is taller than typical tokens, reduce the pixel count threshold
                            // This helps detect miniatures with smaller footprints
                            if (maxHeight > 25)
                            {
                                // Reduce threshold for taller objects - the taller it is, the lower the threshold
                                effectiveThreshold = Math.Max(5, tokenDetectionThreshold - (int)(maxHeight / 5));
                            }
                        }

                        // If this blob is large enough to be a token/miniature
                        if (tokenPoints.Count < MAX_TOKEN_SIZE && tokenPoints.Count >= effectiveThreshold)
                        {
                            // Calculate center position manually instead of using LINQ Average
                            double sumX = 0;
                            double sumY = 0;
                            foreach (Point pt in tokenPoints)
                            {
                                sumX += pt.X;
                                sumY += pt.Y;
                            }
                            double avgX = sumX / tokenPoints.Count;
                            double avgY = sumY / tokenPoints.Count;

                            // Sort depth values to get a more reliable measure
                            depthValues.Sort();

                            // Use a percentile approach to remove outliers
                            int startIndex = (int)(depthValues.Count * 0.1); // Skip lowest 10%
                            int endIndex = (int)(depthValues.Count * 0.9); // Skip highest 10%
                            List<ushort> filteredDepths = depthValues.Skip(startIndex).Take(endIndex - startIndex).ToList();

                            // Calculate average manually to avoid LINQ extension method issues with ushort
                            double avgDepth;
                            if (filteredDepths.Count > 0)
                            {
                                double sum = 0;
                                foreach (ushort depth in filteredDepths)
                                {
                                    sum += depth;
                                }
                                avgDepth = sum / filteredDepths.Count;
                            }
                            else
                            {
                                double sum = 0;
                                foreach (ushort depth in depthValues)
                                {
                                    sum += depth;
                                }
                                avgDepth = sum / depthValues.Count;
                            }

                            // For miniature detection, get the minimum depth (highest point)
                            ushort minDepth = ushort.MaxValue;
                            foreach (ushort depth in depthValues)
                            {
                                if (depth < minDepth && depth > 0) // Avoid zero values
                                {
                                    minDepth = depth;
                                }
                            }
                            ushort maxHeight = (ushort)(tableDepth - minDepth);

                            // Estimate token size (diameter in pixels)
                            // Calculate min/max boundaries manually
                            double minX = double.MaxValue;
                            double maxX = double.MinValue;
                            double minY = double.MaxValue;
                            double maxY = double.MinValue;

                            foreach (Point pt in tokenPoints)
                            {
                                if (pt.X < minX) minX = pt.X;
                                if (pt.X > maxX) maxX = pt.X;
                                if (pt.Y < minY) minY = pt.Y;
                                if (pt.Y > maxY) maxY = pt.Y;
                            }
                            double width = maxX - minX;
                            double height = maxY - minY;
                            double diameter = Math.Max(width, height);

                            // Create a new token object
                            TTRPGToken token = new TTRPGToken
                            {
                                Position = new Point(avgX, avgY),
                                Depth = (ushort)avgDepth,
                                DiameterPixels = diameter,
                                HeightMm = maxHeight, // Use the maximum height for miniatures
                                Points = tokenPoints
                            };

                            // Auto-detect if this is likely a miniature based on size/height ratio
                            if (maxHeight > 25 && (maxHeight / diameter) > 0.5)
                            {
                                token.Type = TokenType.Miniature;
                            }
                            else if (maxHeight < 15)
                            {
                                // Flatter objects are likely tokens
                                token.Type = TokenType.SmallToken;
                                if (diameter > 50) token.Type = TokenType.MediumToken;
                                if (diameter > 100) token.Type = TokenType.LargeToken;
                            }

                            detectedTokens.Add(token);
                        }
                    }
                }
            }

            // Track tokens between frames to maintain identity
            TrackTokensOverTime(previousTokens, detectedTokens);

            // If we have the Coordinate Mapper available, map the tokens to real-world coordinates
            if (kinectSensor != null && kinectSensor.CoordinateMapper != null)
            {
                MapTokensToRealWorld();
            }

            // Update the token overlay visuals
            this.Dispatcher.Invoke(() => {
                UpdateTokenOverlay();
                TokenCountText = $"{detectedTokens.Count} objects detected";
            });
        }

        /// <summary>
        /// Enhanced token detection method with improved handling of tapered miniatures
        /// </summary>
        private void DetectTokensEnhanced()
        {
            if (tableDepth == 0 || depthData == null || detectedTableROI.IsEmpty)
                return;

            // Store previous tokens for tracking and comparison
            List<TTRPGToken> previousTokens = new List<TTRPGToken>(detectedTokens);
            detectedTokens.Clear();

            // First pass: build a height map to help with segmentation
            int[,] heightMap = new int[depthWidth, depthHeight];
            bool[,] processed = new bool[depthWidth, depthHeight];

            // Multi-level approach: first detect bases, then detect height profiles
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

                    // Store in height map
                    heightMap[x, y] = heightAboveTable;
                }
            }

            // Second pass: detect bases of objects
            List<MiniatureCandidate> miniatureCandidates = new List<MiniatureCandidate>();

            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight || processed[x, y])
                        continue;

                    int height = heightMap[x, y];

                    // If this is potentially a base (low height)
                    if (height >= MIN_TOKEN_HEIGHT && height <= MAX_TOKEN_HEIGHT)
                    {
                        // Collect connected base points
                        List<Point> basePoints = new List<Point>();
                        Queue<Point> queue = new Queue<Point>();

                        queue.Enqueue(new Point(x, y));
                        processed[x, y] = true;

                        // Collect similar height connected components
                        while (queue.Count > 0)
                        {
                            Point p = queue.Dequeue();
                            int px = (int)p.X;
                            int py = (int)p.Y;

                            basePoints.Add(p);

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

                                    // Connect similar heights
                                    if (neighborHeight > 0 && Math.Abs(neighborHeight - height) <= 10)
                                    {
                                        queue.Enqueue(new Point(nx, ny));
                                        processed[nx, ny] = true;
                                    }
                                }
                            }
                        }

                        // If we have enough points for a base
                        if (basePoints.Count >= MIN_BASE_SIZE) // Minimum base size (can be adjusted)
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

                            // Add as a candidate
                            miniatureCandidates.Add(new MiniatureCandidate
                            {
                                BaseCenter = new Point(baseX, baseY),
                                BaseDiameter = baseDiameter,
                                BasePoints = basePoints,
                                MaxHeight = height  // Will be updated in the next pass
                            });
                        }
                    }
                }
            }

            // Third pass: for each base, look for taller parts above it
            foreach (var candidate in miniatureCandidates)
            {
                // Define a search region above the base
                int searchRadius = (int)(candidate.BaseDiameter / 2) + 5; // Extra margin
                int baseX = (int)candidate.BaseCenter.X;
                int baseY = (int)candidate.BaseCenter.Y;
                int maxDetectedHeight = 0;
                List<Point> allPoints = new List<Point>(candidate.BasePoints);

                // Scan the region above the base
                for (int y = Math.Max(0, baseY - searchRadius); y <= Math.Min(depthHeight - 1, baseY + searchRadius); y++)
                {
                    for (int x = Math.Max(0, baseX - searchRadius); x <= Math.Min(depthWidth - 1, baseX + searchRadius); x++)
                    {
                        // Skip points outside reasonable distance from base center
                        double distance = Math.Sqrt(Math.Pow(x - baseX, 2) + Math.Pow(y - baseY, 2));
                        if (distance > searchRadius)
                            continue;

                        int height = heightMap[x, y];

                        // If this point is above the base and within max token height
                        if (height > candidate.MaxHeight && height <= MAX_TOKEN_HEIGHT * 2)
                        {
                            // Track the highest point
                            if (height > maxDetectedHeight)
                            {
                                maxDetectedHeight = height;
                            }

                            // Add to the full miniature points
                            if (!candidate.BasePoints.Contains(new Point(x, y)))
                            {
                                allPoints.Add(new Point(x, y));
                            }
                        }
                    }
                }

                // Update the candidate with the full height and points
                if (maxDetectedHeight > candidate.MaxHeight)
                {
                    candidate.MaxHeight = maxDetectedHeight;
                    candidate.AllPoints = allPoints;
                }
                else
                {
                    candidate.AllPoints = candidate.BasePoints;
                }
            }

            // Create tokens from the candidates
            foreach (var candidate in miniatureCandidates)
            {
                // Only consider candidates with enough points
                // Use a dynamic threshold - larger miniatures need more points
                int effectiveThreshold = Math.Max(10, (int)(tokenDetectionThreshold * (candidate.BaseDiameter / 50.0)));

                // For tall, skinny miniatures, we can be more lenient
                if (candidate.MaxHeight > 30)
                {
                    effectiveThreshold = Math.Max(5, effectiveThreshold / 2);
                }

                if (candidate.AllPoints.Count < MAX_TOKEN_SIZE && candidate.AllPoints.Count >= effectiveThreshold)
                {
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

                    // Auto-classify based on height and diameter
                    if (candidate.MaxHeight > 25)
                    {
                        token.Type = TokenType.Miniature;
                    }
                    else if (candidate.BaseDiameter < 25)
                    {
                        token.Type = TokenType.SmallToken;
                    }
                    else if (candidate.BaseDiameter < 50)
                    {
                        token.Type = TokenType.MediumToken;
                    }
                    else
                    {
                        token.Type = TokenType.LargeToken;
                    }

                    detectedTokens.Add(token);
                }
            }

            // Track tokens between frames
            TrackTokensOverTime(previousTokens, detectedTokens);

            // Map to real-world coordinates if available
            if (kinectSensor != null && kinectSensor.CoordinateMapper != null)
            {
                MapTokensToRealWorld();
            }

            // Update the UI
            this.Dispatcher.Invoke(() => {
                UpdateTokenOverlay();
                TokenCountText = $"{detectedTokens.Count} objects detected";
            });
        }

        // Helper class for multi-stage miniature detection
        private class MiniatureCandidate
        {
            public Point BaseCenter { get; set; }
            public double BaseDiameter { get; set; }
            public List<Point> BasePoints { get; set; } = new List<Point>();
            public List<Point> AllPoints { get; set; } = new List<Point>();
            public int MaxHeight { get; set; }
        }

        // Add or modify this method to export to Foundry VTT format
        private void ExportToFoundryVTT(string filePath)
        {
            try
            {
                // Create a structured object compatible with Foundry VTT
                var foundryExport = new
                {
                    scene = "Physical Table", // Could be customizable
                    tokens = detectedTokens.Select(t => new
                    {
                        id = t.Id.ToString(),
                        name = !string.IsNullOrEmpty(t.Label) ? t.Label : GetTokenTypeString(t.Type),
                        x = t.RealWorldPosition.X * 100, // Convert to Foundry grid units
                        y = t.RealWorldPosition.Y * 100, // Convert to Foundry grid units
                        height = GetSizeForFoundry(t),
                        width = GetSizeForFoundry(t),
                        scale = 1.0,
                        type = t.Type.ToString(),
                        elevation = 0,
                        // Add any other Foundry VTT specific properties as needed
                    }).ToArray(),
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // Serialize to JSON
                string json = System.Text.Json.JsonSerializer.Serialize(foundryExport,
                    new JsonSerializerOptions { WriteIndented = true });

                // Write to file
                File.WriteAllText(filePath, json);

                StatusText = $"Tokens exported to Foundry VTT format: {filePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error exporting to Foundry VTT: {ex.Message}";
            }
        }

        // Helper method to determine appropriate size for Foundry
        private double GetSizeForFoundry(TTRPGToken token)
        {
            switch (token.Type)
            {
                case TokenType.SmallToken:
                    return 1.0;
                case TokenType.MediumToken:
                    return 1.0;
                case TokenType.LargeToken:
                    return 2.0;
                case TokenType.Miniature:
                    // Base size estimate from diameter
                    double baseSize = token.DiameterMeters * 39.37 / 1.0; // Convert to grid squares (assuming 1" grid)
                    return Math.Max(1.0, Math.Round(baseSize * 2) / 2); // Round to nearest 0.5
                default:
                    return 1.0;
            }
        }

        // Add this method to the MainWindow class to add a dedicated Foundry VTT export button
        public void AddFoundryExportButton()
        {
            // Find the parent panel in the UI
            var panel = this.FindName("TokenTrackingPanel") as StackPanel;
            if (panel == null)
                return;

            // Create the export button
            var exportButton = new Button
            {
                Content = "Export to Foundry VTT",
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(5, 0, 0, 0)
            };

            // Add click handler
            exportButton.Click += FoundryVTTExport_Click;

            // Add to panel
            panel.Children.Add(exportButton);
        }

        private void FoundryVTTExport_Click(object sender, RoutedEventArgs e)
        {
            // Show save dialog
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".json",
                Title = "Export to Foundry VTT"
            };

            if (saveDialog.ShowDialog() == true)
            {
                ExportToFoundryVTT(saveDialog.FileName);
            }
        }

        /*
        // Enhanced calibration dialog with miniature-specific settings
        private void UpdateCalibrateTokens_Click()
        {
            CalibrateTokens_Click = (sender, e) =>
            {
                // Create a calibration dialog
                var dialog = new Window
                {
                    Title = "Token & Miniature Calibration",
                    Width = 450,
                    Height = 450,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this
                };

                // Create the calibration UI with scrolling
                var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                var panel = new StackPanel { Margin = new Thickness(10) };
                scrollViewer.Content = panel;

                panel.Children.Add(new TextBlock
                {
                    Text = "Configure detection settings for both flat tokens and 3D miniatures.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                // Token height range sliders
                panel.Children.Add(new TextBlock
                {
                    Text = "Flat Token Settings:",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 5, 0, 5)
                });

                panel.Children.Add(new TextBlock { Text = "Token Height Range (mm):" });

                var minHeightSlider = new Slider
                {
                    Minimum = 2,
                    Maximum = 20,
                    Value = MIN_TOKEN_HEIGHT,
                    TickFrequency = 2,
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

                // Add Miniature-specific settings
                panel.Children.Add(new TextBlock
                {
                    Text = "Miniature Settings:",
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 15, 0, 5)
                });

                panel.Children.Add(new TextBlock { Text = "Miniature Max Height (mm):" });

                var maxHeightSlider = new Slider
                {
                    Minimum = 30,
                    Maximum = 150,
                    Value = MAX_TOKEN_HEIGHT,
                    TickFrequency = 10,
                    TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                    IsSnapToTickEnabled = true,
                    Margin = new Thickness(0, 5, 0, 5)
                };

                var maxHeightLabel = new TextBlock { Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm" };
                maxHeightSlider.ValueChanged += (s, args) =>
                {
                    MAX_TOKEN_HEIGHT = (int)args.NewValue;
                    maxHeightLabel.Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm";
                };

                panel.Children.Add(maxHeightSlider);
                panel.Children.Add(maxHeightLabel);

                // Miniature detection sensitivity
                panel.Children.Add(new TextBlock { Text = "Miniature Detection Sensitivity:" });

                var miniatureThresholdSlider = new Slider
                {
                    Minimum = 3,
                    Maximum = 20,
                    Value = Math.Max(3, tokenDetectionThreshold / 2), // Default: half of normal threshold
                    TickFrequency = 1,
                    TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                    IsSnapToTickEnabled = true,
                    Margin = new Thickness(0, 5, 0, 5)
                };

                var miniatureThresholdLabel = new TextBlock
                {
                    Text = $"Minimum Pixel Count: {Math.Max(3, tokenDetectionThreshold / 2)}"
                };

                miniatureThresholdSlider.ValueChanged += (s, args) =>
                {
                    // Store this value in a new app setting
                    int miniatureDetectionThreshold = (int)args.NewValue;
                    miniatureThresholdLabel.Text = $"Minimum Pixel Count: {miniatureDetectionThreshold}";

                    // Use App.Current.Properties to store this setting or add a dedicated field
                    // For now we'll adjust the main threshold
                    tokenDetectionThreshold = Math.Max(miniatureDetectionThreshold * 2, 10);
                };

                panel.Children.Add(miniatureThresholdSlider);
                panel.Children.Add(miniatureThresholdLabel);

                // Add test button to run a detection cycle
                var testButton = new Button
                {
                    Content = "Test Current Settings",
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 10, 0, 0)
                };

                testButton.Click += (s, args) =>
                {
                    // Force a detection cycle
                    DetectTokens();
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
            };
        }
        */
        private void DetectTokensOld()
        {
            if (tableDepth == 0 || depthData == null || detectedTableROI.IsEmpty)
                return;

            // Reset the tokens list
            detectedTokens.Clear();

            // Create a temporary bitmap for token analysis
            bool[,] tokenMap = new bool[depthWidth, depthHeight];
            bool[,] visited = new bool[depthWidth, depthHeight];

            // First pass: identify all potential token pixels
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x < 0 || x >= depthWidth || y < 0 || y >= depthHeight)
                        continue;

                    // If this is a token pixel and hasn't been visited yet
                    if (tokenMap[x, y] && !visited[x, y])
                    {
                        // Start a new token
                        List<Point> tokenPoints = new List<Point>();
                        Queue<Point> queue = new Queue<Point>();

                        // Add the starting point
                        queue.Enqueue(new Point(x, y));
                        visited[x, y] = true;

                        // Flood fill to find all connected points
                        while (queue.Count > 0)
                        {
                            Point p = queue.Dequeue();
                            int px = (int)p.X;
                            int py = (int)p.Y;

                            // Add to token points
                            tokenPoints.Add(p);

                            // Check neighbors (8-connectivity)
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = px + dx;
                                    int ny = py + dy;

                                    // Skip out of bounds
                                    if (nx < 0 || nx >= depthWidth || ny < 0 || ny >= depthHeight)
                                        continue;

                                    // If neighbor is a token pixel and not visited yet
                                    if (tokenMap[nx, ny] && !visited[nx, ny])
                                    {
                                        queue.Enqueue(new Point(nx, ny));
                                        visited[nx, ny] = true;
                                    }
                                }
                            }
                        }

                        // If this blob is large enough to be a token
                        if (tokenPoints.Count >= tokenDetectionThreshold)
                        {
                            // Calculate token properties
                            double avgX = tokenPoints.Average(p => p.X);
                            double avgY = tokenPoints.Average(p => p.Y);

                            // Calculate average depth (height) of the token
                            double sumDepth = 0;
                            foreach (var p in tokenPoints)
                            {
                                int idx = (int)p.Y * depthWidth + (int)p.X;
                                sumDepth += depthData[idx];
                            }
                            double avgDepth = sumDepth / tokenPoints.Count;

                            // Estimate token size (diameter in pixels)
                            double minX = tokenPoints.Min(p => p.X);
                            double maxX = tokenPoints.Max(p => p.X);
                            double minY = tokenPoints.Min(p => p.Y);
                            double maxY = tokenPoints.Max(p => p.Y);
                            double width = maxX - minX;
                            double height = maxY - minY;
                            double diameter = Math.Max(width, height);

                            // Create a new token object
                            TTRPGToken token = new TTRPGToken
                            {
                                Position = new Point(avgX, avgY),
                                Depth = (ushort)avgDepth,
                                DiameterPixels = diameter,
                                HeightMm = (ushort)Math.Max(0,tableDepth - (ushort)avgDepth),
                                Points = tokenPoints
                            };

                            detectedTokens.Add(token);
                        }
                    }
                }
            }

            // Post-process tokens to merge any that are likely part of the same object
            MergeCloseTokens();

            // If we have the Coordinate Mapper available, map the tokens to real-world coordinates
            if (kinectSensor != null && kinectSensor.CoordinateMapper != null)
            {
                MapTokensToRealWorld();
            }
        }

        /// <summary>
        /// Merges tokens that are very close to each other and likely part of the same physical token
        /// </summary>
        private void MergeCloseTokens()
        {
            // If we have 2 or more tokens, check for merging
            if (detectedTokens.Count < 2)
                return;

            bool mergeOccurred;
            do
            {
                mergeOccurred = false;

                // Compare each pair of tokens
                for (int i = 0; i < detectedTokens.Count - 1; i++)
                {
                    for (int j = i + 1; j < detectedTokens.Count; j++)
                    {
                        var token1 = detectedTokens[i];
                        var token2 = detectedTokens[j];

                        // Calculate distance between tokens
                        double dx = token1.Position.X - token2.Position.X;
                        double dy = token1.Position.Y - token2.Position.Y;
                        double distance = Math.Sqrt(dx * dx + dy * dy);

                        // If tokens are very close and similar height, merge them
                        if (distance < Math.Max(token1.DiameterPixels, token2.DiameterPixels) * 0.5 &&
                            Math.Abs(token1.Depth - token2.Depth) < 10)
                        {
                            // Merge token2 into token1
                            token1.Points.AddRange(token2.Points);

                            // Recalculate token1 properties
                            token1.Position = new Point(
                                token1.Points.Average(p => p.X),
                                token1.Points.Average(p => p.Y));

                            double sumDepth = 0;
                            foreach (var p in token1.Points)
                            {
                                int idx = (int)p.Y * depthWidth + (int)p.X;
                                sumDepth += depthData[idx];
                            }
                            token1.Depth = (ushort)(sumDepth / token1.Points.Count);
                            token1.HeightMm = (ushort)Math.Max(0, tableDepth - token1.Depth);

                            // Recalculate diameter
                            double minX = token1.Points.Min(p => p.X);
                            double maxX = token1.Points.Max(p => p.X);
                            double minY = token1.Points.Min(p => p.Y);
                            double maxY = token1.Points.Max(p => p.Y);
                            token1.DiameterPixels = Math.Max(maxX - minX, maxY - minY);

                            // Remove token2 from the list
                            detectedTokens.RemoveAt(j);

                            // Mark that a merge occurred so we do another pass
                            mergeOccurred = true;

                            // Break out of inner loop
                            break;
                        }
                    }

                    if (mergeOccurred)
                        break;
                }

            } while (mergeOccurred);
        }

        /// <summary>
        /// Maps tokens from screen coordinates to real-world coordinates
        /// </summary>
        private void MapTokensToRealWorld()
        {
            // Get coordinate mapper
            var mapper = kinectSensor.CoordinateMapper;

            foreach (var token in detectedTokens)
            {
                // Create a depth space point for the token center
                DepthSpacePoint depthPoint = new DepthSpacePoint
                {
                    X = (float)token.Position.X,
                    Y = (float)token.Position.Y
                };

                // Get the depth value for this point
                int idx = (int)token.Position.Y * depthWidth + (int)token.Position.X;
                ushort depth = depthData[idx];

                // Map to camera space
                CameraSpacePoint cameraPoint = mapper.MapDepthPointToCameraSpace(depthPoint, depth);

                // Store the real-world coordinates (in meters)
                token.RealWorldPosition = new Point3D(cameraPoint.X, cameraPoint.Y, cameraPoint.Z);

                // Calculate real-world dimensions
                // We'll use the diameter in pixels and the depth to estimate real-world size
                double pixelsPerMeter = token.DiameterPixels / (0.05 * token.Depth / 1000.0);
                token.DiameterMeters = token.DiameterPixels / pixelsPerMeter;
            }
        }


    }
}
