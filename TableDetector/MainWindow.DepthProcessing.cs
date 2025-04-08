using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace TableDetector
{
    public partial class MainWindow
    {
        private const int ANGLED_DEG_MIN = 10;
        private const int ANGLED_DEG_MAX = 30;
        private const int DEPTH_QUANTIZATION = 25;      // Group depths into 25mm ranges

        // UI Binding properties
        private string _statusText = "Initializing...";
        public string StatusText
        {
            get { return _statusText; }
            set
            {
                _statusText = value;
                OnPropertyChanged("StatusText");
            }
        }

        private string _tableDepthText = "Unknown";
        public string TableDepthText
        {
            get { return _tableDepthText; }
            set
            {
                _tableDepthText = value;
                OnPropertyChanged("TableDepthText");
            }
        }

        private string _tokenCountText = "0 tokens detected";
        public string TokenCountText
        {
            get { return _tokenCountText; }
            set
            {
                _tokenCountText = value;
                OnPropertyChanged("TokenCountText");
            }
        }

        private void DetermineTableSurfaceDepth()
        {
            if (depthData == null || depthData.Length == 0)
                return;

            // Find planar surfaces - we don't just want a histogram of depths,
            // we want to find continuous flat surfaces in the scene

            // Step 1: Scan the entire depth image to find potential surface regions
            Dictionary<ushort, List<Point>> depthRegions = new Dictionary<ushort, List<Point>>();
            // We'll use a broader quantization to group similar depths

            // Define a reasonable search range for the table
            ushort searchMin = 500;   // 0.5m
            ushort searchMax = 3000;  // 3m (increased range to handle angled views)

            // Full scan of depth image to collect all potential surface points
            for (int y = 0; y < depthHeight; y += 3) // Sample every 3rd row
            {
                for (int x = 0; x < depthWidth; x += 3) // Sample every 3rd column
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // Only consider depths in our focused range
                    if (depth >= searchMin && depth <= searchMax)
                    {
                        // Quantize depth to find similar depths
                        ushort quantizedDepth = (ushort)(depth / DEPTH_QUANTIZATION * DEPTH_QUANTIZATION);

                        if (!depthRegions.ContainsKey(quantizedDepth))
                            depthRegions[quantizedDepth] = new List<Point>();

                        depthRegions[quantizedDepth].Add(new Point(x, y));
                    }
                }
            }

            // Step 2: Find the largest contiguous surfaces
            Dictionary<ushort, double> surfaceMetrics = new Dictionary<ushort, double>();

            foreach (var depthGroup in depthRegions)
            {
                ushort groupDepth = depthGroup.Key;
                List<Point> points = depthGroup.Value;

                // Need a minimum number of points to consider as a surface
                if (points.Count < 100)
                    continue;

                // Calculate surface metrics to help determine if this is a likely table
                // 1. Size of region (number of points)
                // 2. Flatness (consistency of depth values)
                // 3. Contiguity (how well connected the points are)

                // Check flatness by sampling a subset of these points and measuring actual depth variance
                double depthSum = 0;
                double depthSqSum = 0;
                int sampleSize = Math.Min(100, points.Count);

                for (int i = 0; i < sampleSize; i++)
                {
                    int idx = (int)(points[i].Y * depthWidth + points[i].X);
                    ushort actualDepth = depthData[idx];
                    depthSum += actualDepth;
                    depthSqSum += actualDepth * actualDepth;
                }

                // Calculate variance to measure flatness
                double mean = depthSum / sampleSize;
                double variance = (depthSqSum / sampleSize) - (mean * mean);
                double stdDev = Math.Sqrt(variance);

                // Calculate contiguity by analyzing how close points are to each other
                // (simplified approach - we'll use spatial binning)
                int binSize = 20; // 20x20 pixel bins
                int binCountX = (depthWidth + binSize - 1) / binSize;
                int binCountY = (depthHeight + binSize - 1) / binSize;
                bool[,] occupiedBins = new bool[binCountX, binCountY];
                int occupiedBinCount = 0;

                foreach (var pt in points)
                {
                    int binX = (int)pt.X / binSize;
                    int binY = (int)pt.Y / binSize;

                    if (!occupiedBins[binX, binY])
                    {
                        occupiedBins[binX, binY] = true;
                        occupiedBinCount++;
                    }
                }

                // Calculate contiguity as the ratio of occupied bins to total possible bins in the region
                double regionWidth = points.Max(p => p.X) - points.Min(p => p.X);
                double regionHeight = points.Max(p => p.Y) - points.Min(p => p.Y);
                int possibleBins = (int)((regionWidth / binSize + 1) * (regionHeight / binSize + 1));
                double contiguity = possibleBins > 0 ? (double)occupiedBinCount / possibleBins : 0;

                // Combined metric: larger size, lower depth variance, higher contiguity = better surface
                double metric = (points.Count * contiguity) / (stdDev + 1.0);

                surfaceMetrics[groupDepth] = metric;
            }

            // Step 3: Select the best surface based on metrics
            ushort bestDepth = 0;
            double bestMetric = 0;

            foreach (var entry in surfaceMetrics)
            {
                if (entry.Value > bestMetric)
                {
                    bestMetric = entry.Value;
                    bestDepth = entry.Key;
                }
            }

            // If we found a good surface
            if (bestDepth > 0)
            {
                // Get the median of actual depth values at this quantized depth
                List<ushort> actualDepths = new List<ushort>();

                foreach (var point in depthRegions[bestDepth])
                {
                    int idx = (int)(point.Y * depthWidth + point.X);
                    actualDepths.Add(depthData[idx]);
                }

                actualDepths.Sort();
                ushort medianDepth = actualDepths[actualDepths.Count / 2];

                // Apply temporal smoothing
                depthHistory.Enqueue(medianDepth);

                // Keep history to specified size
                while (depthHistory.Count > maxHistorySize)
                    depthHistory.Dequeue();

                // Calculate median from history for stability
                ushort[] depthArray = depthHistory.ToArray();
                Array.Sort(depthArray);

                ushort smoothedDepth = depthArray[depthArray.Length / 2];

                // Update the stored table depth
                tableDepth = smoothedDepth;

                // Log success with detailed information
                this.Dispatcher.Invoke(() => {
                    StatusText = $"Found table at {tableDepth}mm (metric: {bestMetric:F1})";
                });
            }
        }

        private void ProcessDepthData_()
        {
            // Depth ranges
            ushort minDepth = 400;
            ushort maxDepth = 4000;

            // Check if we have a valid table depth
            bool hasTableDepth = tableDepth > 500;

            // For angled views, we need a more sophisticated approach that accounts for depth variation
            // across the surface plane. We'll use an adaptive threshold.

            // First, calculate a depth gradient map to identify planar surfaces
            bool[,] isPotentialTable = new bool[depthWidth, depthHeight];
            bool[,] isPotentialToken = new bool[depthWidth, depthHeight];
            int tableSurfaceCount = 0;

            if (hasTableDepth)
            {
                // Enhanced token and table detection visualization
                for (int y = 0; y < depthHeight; y++)
                {
                    for (int x = 0; x < depthWidth; x++)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        if (depth > 0 && depth < maxDepth)
                        {
                            // Detect table surface - within threshold of table depth
                            int tableThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;
                            if (Math.Abs(depth - tableDepth) <= tableThreshold)
                            {
                                isPotentialTable[x, y] = true;
                                tableSurfaceCount++;
                            }

                            // Detect potential tokens - objects above the table surface
                            int heightFromTable = tableDepth - depth;
                            if (heightFromTable >= MIN_TOKEN_HEIGHT && heightFromTable <= MAX_TOKEN_HEIGHT)
                            {
                                isPotentialToken[x, y] = true;
                            }
                        }
                    }
                }
            }

            // Now visualize the depth image with improved highlights for table surface and tokens
            int colorIndex = 0;

            for (int y = 0; y < depthHeight; y++)
            {
                for (int x = 0; x < depthWidth; x++)
                {
                    int i = y * depthWidth + x;
                    ushort depth = depthData[i];

                    // Standard grayscale visualization as baseline
                    byte intensity = (byte)(depth >= minDepth && depth <= maxDepth ?
                                          (255 * (depth - minDepth) / (maxDepth - minDepth)) : 0);

                    if (hasTableDepth && showDepthContours && depth > 0)
                    {
                        if (isPotentialToken[x, y])
                        {
                            // This is a potential token - use bright green
                            int heightAboveTable = tableDepth - depth;
                            // Scale green intensity based on height (brighter for taller objects)
                            byte greenIntensity = (byte)Math.Min(255, 100 + (heightAboveTable * 5));

                            depthPixels[colorIndex++] = 50;  // B
                            depthPixels[colorIndex++] = greenIntensity; // G 
                            depthPixels[colorIndex++] = 50;  // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                        else if (isPotentialTable[x, y])
                        {
                            // This is part of the table surface plane - use blue with highlight
                            double depthDiff = Math.Abs(depth - tableDepth);
                            // Adjust color intensity based on how close to the exact table depth
                            byte blueIntensity = (byte)Math.Max(180, 255 - depthDiff * 3);

                            depthPixels[colorIndex++] = blueIntensity; // B
                            depthPixels[colorIndex++] = 200; // G
                            depthPixels[colorIndex++] = 100; // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                        else
                        {
                            // Standard grayscale for other areas
                            depthPixels[colorIndex++] = intensity; // B
                            depthPixels[colorIndex++] = intensity; // G
                            depthPixels[colorIndex++] = intensity; // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                    }
                    else
                    {
                        // Standard grayscale when contours disabled
                        depthPixels[colorIndex++] = intensity; // B
                        depthPixels[colorIndex++] = intensity; // G
                        depthPixels[colorIndex++] = intensity; // R
                        depthPixels[colorIndex++] = 255; // A
                    }
                }
            }

            // Draw ROI rectangle on the depth image if enabled
            if (showROIOverlay && detectedTableROI.Width > 0 && detectedTableROI.Height > 0)
            {
                // Draw a rectangle around the ROI
                DrawRectangle(depthPixels, depthWidth, depthHeight,
                              (int)detectedTableROI.X, (int)detectedTableROI.Y,
                              (int)detectedTableROI.Width, (int)detectedTableROI.Height,
                              255, 50, 50); // Bright red
            }

            // Highlight the detected tokens on the depth image
            if (trackTokens && detectedTokens.Count > 0)
            {
                foreach (var token in detectedTokens)
                {
                    // Draw a circle around each token
                    int radius = (int)(token.DiameterPixels / 2);
                    int centerX = (int)token.Position.X;
                    int centerY = (int)token.Position.Y;

                    // Draw a yellow circle around the token
                    for (int angle = 0; angle < 360; angle += 5)
                    {
                        double radians = angle * Math.PI / 180;
                        int x = (int)(centerX + radius * Math.Cos(radians));
                        int y = (int)(centerY + radius * Math.Sin(radians));

                        if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                        {
                            int pixelIdx = (y * depthWidth + x) * 4;
                            depthPixels[pixelIdx] = 0;     // B
                            depthPixels[pixelIdx + 1] = 255; // G
                            depthPixels[pixelIdx + 2] = 255; // R
                            depthPixels[pixelIdx + 3] = 255; // A
                        }
                    }
                }
            }

            // Update the UI to show surface detection stats
            this.Dispatcher.Invoke(() => {
                if (hasTableDepth && showDepthContours)
                {
                    double surfacePercentage = (100.0 * tableSurfaceCount) / (depthWidth * depthHeight);
                    TableDepthText = $"{tableDepth} mm ({surfacePercentage:F1}%)";
                }
            });
        }

        private void ProcessDepthData()
        {
            // Depth ranges
            ushort minDepth = 400;
            ushort maxDepth = 4000;

            // Check if we have a valid table depth
            bool hasTableDepth = tableDepth > 500;

            // For angled views, we need a more sophisticated approach that accounts for depth variation
            // across the surface plane. We'll use an adaptive threshold.

            // First, calculate a depth gradient map to identify planar surfaces
            bool[,] isPotentialTable = new bool[depthWidth, depthHeight];
            int tableSurfaceCount = 0;

            if (hasTableDepth && showDepthContours)
            {
                // Use a more adaptive threshold for table surface - the variation is larger when viewed at an angle
                int tableThreshold = 30; // 30mm variation tolerance (increased from 15mm)

                // Calculate local slope for each region to determine if it's part of the same plane
                // This helps with angled views where the depth changes across the surface
                const int gridSize = 8; // Check 8x8 cells

                for (int y = gridSize; y < depthHeight - gridSize; y += gridSize)
                {
                    for (int x = gridSize; x < depthWidth - gridSize; x += gridSize)
                    {
                        // Sample points in this grid cell
                        int validSamples = 0;
                        double sumDepth = 0;

                        for (int dy = -gridSize / 2; dy <= gridSize / 2; dy++)
                        {
                            for (int dx = -gridSize / 2; dx <= gridSize / 2; dx++)
                            {
                                int idx = (y + dy) * depthWidth + (x + dx);
                                if (idx >= 0 && idx < depthData.Length)
                                {
                                    ushort depth = depthData[idx];
                                    if (depth > 0)
                                    {
                                        validSamples++;
                                        sumDepth += depth;
                                    }
                                }
                            }
                        }

                        if (validSamples > 0)
                        {
                            double avgDepth = sumDepth / validSamples;

                            // If this depth is close to the estimated table depth or part of a consistent plane
                            if (Math.Abs(avgDepth - tableDepth) < tableThreshold)
                            {
                                // Mark all points in this cell as potential table
                                for (int dy = -gridSize / 2; dy <= gridSize / 2; dy++)
                                {
                                    for (int dx = -gridSize / 2; dx <= gridSize / 2; dx++)
                                    {
                                        int px = x + dx;
                                        int py = y + dy;

                                        if (px >= 0 && px < depthWidth && py >= 0 && py < depthHeight)
                                        {
                                            isPotentialTable[px, py] = true;
                                            tableSurfaceCount++;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Now visualize the depth image with improved highlights for table surface
            int colorIndex = 0;

            for (int y = 0; y < depthHeight; y++)
            {
                for (int x = 0; x < depthWidth; x++)
                {
                    int i = y * depthWidth + x;
                    ushort depth = depthData[i];

                    // Standard grayscale visualization as baseline
                    byte intensity = (byte)(depth >= minDepth && depth <= maxDepth ?
                                          (255 * (depth - minDepth) / (maxDepth - minDepth)) : 0);

                    if (hasTableDepth && showDepthContours && depth > 0)
                    {
                        if (isPotentialTable[x, y])
                        {
                            // This is part of the table surface plane - use blue with highlight
                            double depthDiff = Math.Abs(depth - tableDepth);
                            // Adjust color intensity based on how close to the exact table depth
                            byte blueIntensity = (byte)Math.Max(180, 255 - depthDiff * 3);

                            depthPixels[colorIndex++] = blueIntensity; // B
                            depthPixels[colorIndex++] = 200; // G
                            depthPixels[colorIndex++] = 100; // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                        else if (depth < tableDepth - 15)
                        {
                            // Objects above table - highlight in green
                            int heightAboveTable = (int)(tableDepth - depth);

                            // Brighter green for taller objects
                            byte greenIntensity = (byte)Math.Min(255, 100 + (heightAboveTable * 3));

                            depthPixels[colorIndex++] = 50;  // B
                            depthPixels[colorIndex++] = greenIntensity; // G
                            depthPixels[colorIndex++] = 50;  // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                        else
                        {
                            // Standard grayscale for other areas
                            depthPixels[colorIndex++] = intensity; // B
                            depthPixels[colorIndex++] = intensity; // G
                            depthPixels[colorIndex++] = intensity; // R
                            depthPixels[colorIndex++] = 255; // A
                        }
                    }
                    else
                    {
                        // Standard grayscale when contours disabled
                        depthPixels[colorIndex++] = intensity; // B
                        depthPixels[colorIndex++] = intensity; // G
                        depthPixels[colorIndex++] = intensity; // R
                        depthPixels[colorIndex++] = 255; // A
                    }
                }
            }

            // Draw ROI rectangle on the depth image if enabled
            if (showROIOverlay && detectedTableROI.Width > 0 && detectedTableROI.Height > 0)
            {
                // Draw a rectangle around the ROI
                DrawRectangle(depthPixels, depthWidth, depthHeight,
                              (int)detectedTableROI.X, (int)detectedTableROI.Y,
                              (int)detectedTableROI.Width, (int)detectedTableROI.Height,
                              255, 50, 50); // Bright red
            }

            // Update the UI to show surface detection stats
            this.Dispatcher.Invoke(() => {
                if (hasTableDepth && showDepthContours)
                {
                    double surfacePercentage = (100.0 * tableSurfaceCount) / (depthWidth * depthHeight);
                    TableDepthText = $"{tableDepth} mm ({surfacePercentage:F1}%)";
                }
            });
        }

        // Enhanced table detection method that scans the entire scene exhaustively
        private void DetermineTableSurfaceDepthExhaustive()
        {
            if (depthData == null || depthData.Length == 0)
                return;

            // We want to find the largest planar surface by checking the entire scene
            // This is computationally intensive but more thorough

            // Full scene analysis to find planar regions
            Dictionary<ushort, List<Point>> planePoints = new Dictionary<ushort, List<Point>>();
            Dictionary<ushort, double> planeScores = new Dictionary<ushort, double>();

            // Scan the scene in a grid to find planar regions
            int gridStep = 4; // Scan every 4th pixel to balance speed and accuracy
            int planeThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN; // Depth variation tolerance (mm)
            int searchRange = 500; // +/- 500mm search range from most common depth

            // First find the most common depth to narrow our search range
            Dictionary<ushort, int> depthHistogram = new Dictionary<ushort, int>();

            for (int y = 0; y < depthHeight; y += gridStep)
            {
                for (int x = 0; x < depthWidth; x += gridStep)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    if (depth > 400 && depth < 4000) // Valid depth range
                    {
                        if (!depthHistogram.ContainsKey(depth))
                            depthHistogram[depth] = 0;
                        depthHistogram[depth]++;
                    }
                }
            }

            // Find most common depth
            ushort commonDepth = 0;
            int maxCount = 0;

            foreach (var pair in depthHistogram)
            {
                if (pair.Value > maxCount)
                {
                    maxCount = pair.Value;
                    commonDepth = pair.Key;
                }
            }

            // Set search range around the most common depth
            ushort minSearchDepth = (ushort)Math.Max(400, commonDepth - searchRange);
            ushort maxSearchDepth = (ushort)Math.Min(4000, commonDepth + searchRange);

            // Now scan for planar regions within this range
            // We'll quantize depths into bins to group similar depths
            int quantizationStep = 20; // 20mm quantization

            for (int y = 0; y < depthHeight; y += gridStep)
            {
                for (int x = 0; x < depthWidth; x += gridStep)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    if (depth >= minSearchDepth && depth <= maxSearchDepth)
                    {
                        // Quantize depth to group similar depths
                        ushort quantizedDepth = (ushort)(depth / quantizationStep * quantizationStep);

                        if (!planePoints.ContainsKey(quantizedDepth))
                            planePoints[quantizedDepth] = new List<Point>();

                        planePoints[quantizedDepth].Add(new Point(x, y));
                    }
                }
            }

            // Calculate plane metrics for each depth
            foreach (var plane in planePoints)
            {
                ushort planeDepth = plane.Key;
                List<Point> points = plane.Value;

                // Skip small regions
                if (points.Count < 100)
                    continue;

                // Calculate spatial distribution
                double minX = points.Min(p => p.X);
                double maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y);
                double maxY = points.Max(p => p.Y);

                double width = maxX - minX;
                double height = maxY - minY;
                double area = width * height;

                // Calculate spatial density (points per area)
                double density = points.Count / area;

                // Calculate depth consistency
                double depthVariance = 0;
                List<ushort> actualDepths = new List<ushort>();

                // Sample a subset of points for efficiency
                int sampleSize = Math.Min(100, points.Count);
                for (int i = 0; i < sampleSize; i++)
                {
                    int index = (i * points.Count) / sampleSize;
                    int idx = (int)(points[index].Y * depthWidth + points[index].X);
                    ushort actualDepth = depthData[idx];
                    actualDepths.Add(actualDepth);
                }

                // Calculate variance
                if (actualDepths.Count > 0)
                {
                    // Manual calculation of mean to avoid LINQ dependency
                    double sum = 0;
                    foreach (var depth in actualDepths)
                    {
                        sum += depth;
                    }
                    double mean = sum / actualDepths.Count;

                    // Manual calculation of variance
                    double sumSquaredDiff = 0;
                    foreach (var depth in actualDepths)
                    {
                        double diff = depth - mean;
                        sumSquaredDiff += diff * diff;
                    }
                    depthVariance = sumSquaredDiff / actualDepths.Count;
                }

                // Calculate plane score - higher is better
                // We want: many points, large area, high density, low depth variance
                double score = (points.Count * Math.Sqrt(area) * density) / (Math.Sqrt(depthVariance) + 1);

                // Bonus for being close to center of image (likely where the game area is)
                double centerX = depthWidth / 2.0;
                double centerY = depthHeight / 2.0;
                double centerDistX = Math.Abs((minX + maxX) / 2 - centerX) / depthWidth;
                double centerDistY = Math.Abs((minY + maxY) / 2 - centerY) / depthHeight;
                double centerDist = Math.Sqrt(centerDistX * centerDistX + centerDistY * centerDistY);

                // Apply center proximity bonus (1.0 = center, 0.5 = edge)
                double centerBonus = 1.0 - centerDist;
                score *= (1.0 + centerBonus);

                planeScores[planeDepth] = score;
            }

            // Find the plane with the highest score
            ushort bestPlaneDepth = 0;
            double bestScore = 0;

            foreach (var score in planeScores)
            {
                if (score.Value > bestScore)
                {
                    bestScore = score.Value;
                    bestPlaneDepth = score.Key;
                }
            }

            // If we found a good plane
            if (bestPlaneDepth > 0)
            {
                // Get the median of actual depth values in this plane
                List<ushort> planeActualDepths = new List<ushort>();
                foreach (var point in planePoints[bestPlaneDepth].Take(100)) // Sample up to 100 points
                {
                    int idx = (int)(point.Y * depthWidth + point.X);
                    planeActualDepths.Add(depthData[idx]);
                }

                planeActualDepths.Sort();
                ushort medianPlaneDepth = planeActualDepths[planeActualDepths.Count / 2];

                // Apply temporal smoothing
                depthHistory.Enqueue(medianPlaneDepth);

                // Keep history to specified size
                while (depthHistory.Count > maxHistorySize)
                    depthHistory.Dequeue();

                // Calculate median from history for stability
                ushort[] depthArray = depthHistory.ToArray();
                Array.Sort(depthArray);

                ushort smoothedDepth = depthArray[depthArray.Length / 2];

                // Update the stored table depth
                tableDepth = smoothedDepth;

                // Log success
                string details = $"Found table plane at {tableDepth}mm (score: {bestScore:F0}, size: {planePoints[bestPlaneDepth].Count} points)";

                this.Dispatcher.Invoke(() => {
                    StatusText = details;
                });
            }
        }

        // Simple table depth detection method for direct overhead view
        private void DetermineTableSurfaceDepthSimple()
        {
            if (depthData == null || depthData.Length == 0)
                return;

            // Find table surface depth using a simple histogram approach
            Dictionary<ushort, int> depthHistogram = new Dictionary<ushort, int>();
            int totalValidSamples = 0;

            // Define a reasonable search range for the table
            ushort searchMin = 500;   // 0.5m
            ushort searchMax = 2000;  // 2m

            // Sample the center region which is more likely to be the table
            int centerX = depthWidth / 2;
            int centerY = depthHeight / 2;
            int sampleRadius = Math.Min(depthWidth, depthHeight) / 4; // Use 1/4 of the smaller dimension

            for (int y = centerY - sampleRadius; y < centerY + sampleRadius; y += 4) // Sample every 4th pixel
            {
                for (int x = centerX - sampleRadius; x < centerX + sampleRadius; x += 4)
                {
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        // Only consider depths in our focused range
                        if (depth >= searchMin && depth <= searchMax)
                        {
                            totalValidSamples++;
                            if (!depthHistogram.ContainsKey(depth))
                                depthHistogram[depth] = 0;
                            depthHistogram[depth]++;
                        }
                    }
                }
            }

            // We need a minimum amount of data to make a good estimate
            if (totalValidSamples < 100)
                return;

            // For better robustness, bin depths into larger clusters
            Dictionary<int, int> binnedHistogram = new Dictionary<int, int>();
            int binSize = 10; // Group depths into 10mm bins

            foreach (var pair in depthHistogram)
            {
                int bin = pair.Key / binSize;
                if (!binnedHistogram.ContainsKey(bin))
                    binnedHistogram[bin] = 0;
                binnedHistogram[bin] += pair.Value;
            }

            // Find the largest bin (most common depth range)
            int maxBin = -1;
            int maxBinCount = 0;

            foreach (var pair in binnedHistogram)
            {
                if (pair.Value > maxBinCount)
                {
                    maxBinCount = pair.Value;
                    maxBin = pair.Key;
                }
            }

            // Sanity check
            if (maxBin < 0 || (maxBinCount < totalValidSamples * 0.1))
                return;

            // Now find the most common specific depth within this bin
            int binStart = maxBin * binSize;
            int binEnd = binStart + binSize - 1;

            ushort mostCommonDepth = 0;
            int mostCommonCount = 0;

            foreach (var pair in depthHistogram)
            {
                if (pair.Key >= binStart && pair.Key <= binEnd && pair.Value > mostCommonCount)
                {
                    mostCommonCount = pair.Value;
                    mostCommonDepth = pair.Key;
                }
            }

            // Apply temporal smoothing if we found a valid depth
            if (mostCommonDepth > 0)
            {
                // Add to history queue
                depthHistory.Enqueue(mostCommonDepth);

                // Keep history to specified size
                while (depthHistory.Count > maxHistorySize)
                    depthHistory.Dequeue();

                // Calculate median from history for stability
                ushort[] depthArray = depthHistory.ToArray();
                Array.Sort(depthArray);

                ushort medianDepth = depthArray[depthArray.Length / 2];

                // Update the stored table depth
                tableDepth = medianDepth;
            }
        }
        
        // Analyze depth data in the ROI to determine table surface
        private void AnalyzeROIDepth()
        {
            if (depthData == null || detectedTableROI.Width <= 0 || detectedTableROI.Height <= 0)
                return;

            List<ushort> validDepths = new List<ushort>();

            // Scan the ROI for valid depth values
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    // Ensure we're in bounds
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        // Only include valid depths
                        if (depth > 400 && depth < 4000)
                        {
                            validDepths.Add(depth);
                        }
                    }
                }
            }

            if (validDepths.Count > 0)
            {
                // Sort the depths to find median
                validDepths.Sort();
                ushort medianDepth = validDepths[validDepths.Count / 2];

                // Update table depth
                tableDepth = medianDepth;
                tableDepthLocked = true;

                // Update UI
                TableDepthText = $"{tableDepth} mm (from ROI)";
                StatusText = $"Table depth set to {tableDepth}mm from ROI analysis ({validDepths.Count} samples)";
            }
        }

        // 
        private void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            // Get reference to the multi frame
            var reference = e.FrameReference.AcquireFrame();

            bool hasColorFrame = false;
            bool hasDepthFrame = false;

            // Process Color Frame
            using (var colorFrame = reference.ColorFrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    var colorDesc = colorFrame.FrameDescription;

                    try
                    {
                        // Process Color Frame
                        colorFrame.CopyConvertedFrameDataToArray(colorData, ColorImageFormat.Bgra);

                        // Write to bitmap source
                        this.Dispatcher.Invoke(() => {
                            this.colorBitmap.Lock();
                            Marshal.Copy(colorData, 0, this.colorBitmap.BackBuffer, colorData.Length);
                            this.colorBitmap.AddDirtyRect(new Int32Rect(0, 0, colorDesc.Width, colorDesc.Height));
                            this.colorBitmap.Unlock();
                        });

                        hasColorFrame = true;
                    }
                    catch (Exception ex)
                    {
                        StatusText = $"Error processing color frame: {ex.Message}";
                    }
                }
            }

            // Process Depth Frame
            using (var depthFrame = reference.DepthFrameReference.AcquireFrame())
            {
                if (depthFrame != null)
                {
                    var depthDesc = depthFrame.FrameDescription;

                    try
                    {
                        // Get raw depth data
                        depthFrame.CopyFrameDataToArray(depthData);

                        // Determine table surface depth
                        if (!tableDepthLocked)
                        {
                            // Use appropriate detection algorithm based on view mode
                            if (isAngledView)
                            {
                                DetermineTableSurfaceDepth(); // Enhanced algorithm that handles angles
                            }
                            else
                            {
                                // Simpler algorithm for direct overhead view
                                DetermineTableSurfaceDepthSimple();
                            }
                        }

                        // Update table ROI if needed
                        if (detectedTableROI.IsEmpty || !tableDepthLocked)
                        {
                            DetectTableROI();
                        }

                        // Process depth into visualization
                        ProcessDepthData();

                        // Check if it's time to update token tracking
                        if (trackTokens && DateTime.Now - lastTokenUpdateTime > tokenUpdateInterval)
                        {
                            //DetectTokens();
                            DetectTokensEnhanced();
                            lastTokenUpdateTime = DateTime.Now;
                        }

                        // TODO NOTE Dev removal
                        // Process depth into visualization
                        //ProcessDepthData();

                        // Write to bitmap source
                        this.Dispatcher.Invoke(() => {
                            this.depthBitmap.Lock();
                            Marshal.Copy(depthPixels, 0, this.depthBitmap.BackBuffer, depthPixels.Length);
                            this.depthBitmap.AddDirtyRect(new Int32Rect(0, 0, depthDesc.Width, depthDesc.Height));
                            this.depthBitmap.Unlock();

                            // Update table depth display
                            TableDepthText = $"{tableDepth} mm";
                        });

                        hasDepthFrame = true;
                    }
                    catch (Exception ex)
                    {
                        StatusText = $"Error processing depth frame: {ex.Message}";
                    }
                }
            }

            if (hasColorFrame && hasDepthFrame)
            {
                this.Dispatcher.Invoke(() => {
                    StatusText = $"Processing frames - Table depth: {tableDepth} mm";
                });
            }
        }
        
        /// <summary>
        /// Detects the region of interest that defines the table surface
        /// </summary>
        private void DetectTableROI()
        {
            if (tableDepth == 0 || depthData == null)
                return;

            // Scan the depth image to find the ROI of the table surface
            int minX = depthWidth;
            int minY = depthHeight;
            int maxX = 0;
            int maxY = 0;
            int pointCount = 0;

            // The threshold for considering a point part of the table
            int depthTolerance = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;

            // Scan the depth data to find table surface points
            for (int y = 0; y < depthHeight; y += 4) // Sample every 4th pixel for performance
            {
                for (int x = 0; x < depthWidth; x += 4)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // Check if this point is at table depth (with tolerance)
                    if (depth > 0 && Math.Abs(depth - tableDepth) <= depthTolerance)
                    {
                        // Update bounds
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                        pointCount++;
                    }
                }
            }

            // Only update if we found enough points
            if (pointCount > 500) // Arbitrary threshold to ensure we have a solid surface
            {
                // Add margins to the ROI
                int margin = 10;
                minX = Math.Max(0, minX - margin);
                minY = Math.Max(0, minY - margin);
                maxX = Math.Min(depthWidth - 1, maxX + margin);
                maxY = Math.Min(depthHeight - 1, maxY + margin);

                // Create the ROI rectangle
                Rect proposedROI = new Rect(minX, minY, maxX - minX, maxY - minY);

                if (ValidateTableROI(proposedROI))
                {
                    // Create the ROI rectangle
                    detectedTableROI = proposedROI;

                    this.Dispatcher.Invoke(() =>
                    {
                        StatusText = $"Updated table ROI: {detectedTableROI.Width}x{detectedTableROI.Height}";
                    });
                }
                else
                {
                    StatusText = "Detected ROI had invalid dimensions - using previous or default";
                }
            }
        }

        // Add to MainWindow class
        private bool ValidateTableROI(Rect roi)
        {
            // Check if ROI is reasonable for a table
            double minSize = 100; // Minimum pixels
            double maxSize = Math.Min(depthWidth, depthHeight) * 0.9; // Max 90% of frame
            double aspectRatio = roi.Width / roi.Height;

            // Tables are usually somewhat square-ish (aspect ratio between 0.5 and 2.0)
            bool validSize = roi.Width > minSize && roi.Height > minSize &&
                             roi.Width < maxSize && roi.Height < maxSize;
            bool validRatio = aspectRatio >= 0.5 && aspectRatio <= 2.0;

            return validSize && validRatio;
        }

    }
}
