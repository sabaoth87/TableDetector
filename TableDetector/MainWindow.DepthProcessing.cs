using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace TableDetector
{
    public partial class MainWindow
    {
        private const int ANGLED_DEG_MIN = 10;
        private const int ANGLED_DEG_MAX = 30;
        private const int DEPTH_QUANTIZATION = 25;      // Group depths into 25mm ranges
        //
        private int[,] heightGrid; // Grid to store height values
        private int gridCellSize = 20; // Size of each grid cell in pixels
        private int gridWidth, gridHeight; // Dimensions of the grid
        // Height Grid addition
        private Canvas HeightGridCanvas;
        private bool showHeightGrid = false;

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

        // Initialize grid based on ROI dimensions
        private void InitializeHeightGridOld()
        {
            if (!hasValidROI) return;

            gridWidth = (int)Math.Ceiling(detectedTableROI.Width / gridCellSize);
            gridHeight = (int)Math.Ceiling(detectedTableROI.Height / gridCellSize);
            heightGrid = new int[gridWidth, gridHeight];
        }

        // Update the height grid with current depth data
        private void UpdateHeightGridOld()
        {
            if (!hasValidROI || !hasValidTableDepth || heightGrid == null) return;

            // Reset the grid
            Array.Clear(heightGrid, 0, heightGrid.Length);

            // Counters for each cell to compute averages
            int[,] cellCounts = new int[gridWidth, gridHeight];

            // Process depth data into the grid
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        // Skip invalid depths
                        if (depth == 0 || depth >= tableDepth) continue;

                        // Calculate height above table
                        int heightAboveTable = tableDepth - depth;

                        // Calculate grid cell
                        int cellX = (int)((x - detectedTableROI.X) / gridCellSize);
                        int cellY = (int)((y - detectedTableROI.Y) / gridCellSize);

                        if (cellX >= 0 && cellX < gridWidth && cellY >= 0 && cellY < gridHeight)
                        {
                            // Accumulate height for averaging
                            heightGrid[cellX, cellY] += heightAboveTable;
                            cellCounts[cellX, cellY]++;
                        }
                    }
                }
            }

            // Calculate averages
            for (int cellY = 0; cellY < gridHeight; cellY++)
            {
                for (int cellX = 0; cellX < gridWidth; cellX++)
                {
                    if (cellCounts[cellX, cellY] > 0)
                    {
                        heightGrid[cellX, cellY] /= cellCounts[cellX, cellY];
                    }
                    else
                    {
                        heightGrid[cellX, cellY] = 0; // No data
                    }
                }
            }
        }
        // Fields for playfield calibration
        private bool isCalibrationMode = false;
        private List<Point> calibrationPoints = new List<Point>();
        private const int CALIBRATION_POINTS_REQUIRED = 4;
        private Rect manualPlayfieldRect = Rect.Empty;
        private Canvas calibrationOverlayCanvas = null;

        // Add method to visualize the height grid
        private void VisualizeHeightGridOld()
        {
            if (heightGrid == null || !hasValidROI) return;

            // Create a visualization canvas if it doesn't exist
            if (HeightGridCanvas == null)
            {
                HeightGridCanvas = new Canvas();
                DepthROICanvas.Children.Add(HeightGridCanvas);
            }
            else
            {
                HeightGridCanvas.Children.Clear();
            }

            // Scale factors to fit in the depth image display
            double scaleX = DepthImage.ActualWidth / depthWidth;
            double scaleY = DepthImage.ActualHeight / depthHeight;
            double cellSizeDisplayX = gridCellSize * scaleX;
            double cellSizeDisplayY = gridCellSize * scaleY;

            // Draw each cell
            for (int cellY = 0; cellY < gridHeight; cellY++)
            {
                for (int cellX = 0; cellX < gridWidth; cellX++)
                {
                    int height = heightGrid[cellX, cellY];

                    if (height > 0)
                    {
                        // Create a rectangle for the cell
                        Rectangle cellRect = new Rectangle
                        {
                            Width = cellSizeDisplayX,
                            Height = cellSizeDisplayY,
                            Stroke = new SolidColorBrush(Colors.Yellow),
                            StrokeThickness = 1
                        };

                        // Color based on height (0-100mm range)
                        byte intensity = (byte)Math.Min(255, height * 255 / 100);
                        cellRect.Fill = new SolidColorBrush(Color.FromArgb(100, (byte)(255 - intensity), intensity, 0));

                        // Position
                        double left = (detectedTableROI.X + cellX * gridCellSize) * scaleX;
                        double top = (detectedTableROI.Y + cellY * gridCellSize) * scaleY;
                        Canvas.SetLeft(cellRect, left);
                        Canvas.SetTop(cellRect, top);

                        HeightGridCanvas.Children.Add(cellRect);
                    }
                }
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

        // Updated 2025-04-08
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

                        // Process height grid after depth processing if enabled
                        if (hasValidROI && hasValidTableDepth && showHeightGrid)
                        {
                            // Update the height grid
                            UpdateHeightGrid();

                            // Optionally update the plane fitting for angled tables
                            if (isAngledView)
                            {
                                FitPlaneToTableSurfaceOld();
                            }
                        }

                        // Check if it's time to update token tracking
                        if (trackTokens && DateTime.Now - lastTokenUpdateTime > tokenUpdateInterval)
                        {
                            // Use the appropriate token detection method based on enabled features
                            if (enableColorDetection)
                            {
                                // Use the enhanced method with color detection
                                DetectTokensWithColor();
                            }
                            else
                            {
                                // Use the standard enhanced method
                                DetectTokensEnhanced();
                            }

                            // If grid mapping is active, apply transformations
                            if (isGridMappingActive)
                            {
                                ApplyGridMappingToTokens();
                            }

                            lastTokenUpdateTime = DateTime.Now;
                        }

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

            if (hasValidROI && hasValidTableDepth && showHeightGrid)
            {
                UpdateHeightGrid();

                // Optionally update the plane fitting
                if (isAngledView)
                {
                    FitPlaneToTableSurfaceOld();
                }
            }

            // If we have all the required data, start the height map
            if (hasValidROI && hasValidTableDepth)
            {
                UpdateHeightGrid();
                VisualizeHeightGrid();
            }

            // In DetectTableROI, after creating a new ROI
            InitializeHeightGrid();

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

            // Use more aggressive noise filtering
            int depthTolerance = isAngledView ? 30 : 15; // Increased tolerance
            int sampleStep = 4; // Skip pixels for performance
            int minPointCount = 1000; // Higher threshold for valid surface

            // More robust point filtering
            List<Point> tablePoints = new List<Point>();

            // First scan to find points at table depth
            for (int y = 0; y < depthHeight; y += sampleStep)
            {
                for (int x = 0; x < depthWidth; x += sampleStep)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // More strict tolerance close to edges
                    bool isEdgeRegion = x < depthWidth * 0.1 || x > depthWidth * 0.9 ||
                                       y < depthHeight * 0.1 || y > depthHeight * 0.9;

                    int localTolerance = isEdgeRegion ? depthTolerance / 2 : depthTolerance;

                    if (depth > 0 && Math.Abs(depth - tableDepth) <= localTolerance)
                    {
                        tablePoints.Add(new Point(x, y));
                    }
                }
            }

            if (tablePoints.Count > minPointCount)
            {
                // Use convex hull or robust boundary calculation
                // For simplicity, we'll use min/max but with outlier rejection

                // Sort points by X and Y
                var sortedX = tablePoints.OrderBy(p => p.X).ToList();
                var sortedY = tablePoints.OrderBy(p => p.Y).ToList();

                // Skip outliers (e.g., 5% from each end)
                int skipCount = (int)(tablePoints.Count * 0.05);
                int validCount = tablePoints.Count - (skipCount * 2);

                if (validCount > minPointCount / 2)
                {
                    double minX = sortedX[skipCount].X;
                    double maxX = sortedX[sortedX.Count - skipCount - 1].X;
                    double minY = sortedY[skipCount].Y;
                    double maxY = sortedY[sortedY.Count - skipCount - 1].Y;

                    // Create ROI with margins
                    int margin = 10;
                    Rect proposedROI = new Rect(
                        minX - margin,
                        minY - margin,
                        maxX - minX + (margin * 2),
                        maxY - minY + (margin * 2)
                    );

                    if (ValidateTableROI(proposedROI))
                    {
                        detectedTableROI = proposedROI;
                        hasValidROI = true;

                        // Update UI
                        this.Dispatcher.Invoke(() => {
                            StatusText = $"Table ROI updated: {detectedTableROI.Width:F0}x{detectedTableROI.Height:F0}";
                        });

                        // Initialize height grid
                        if (showHeightGrid)
                        {
                            InitializeHeightGrid();
                        }
                    }
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

        // Plane fitting to handle angled tables
        private void FitPlaneToTableSurfaceOld()
        {
            if (depthData == null || !hasValidROI) return;

            List<Point3D> surfacePoints = new List<Point3D>();

            // Sample points on the table surface
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y += 5)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x += 5)
                {
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        // Only include depths near the table surface
                        if (Math.Abs(depth - tableDepth) <= depthThreshold)
                        {
                            surfacePoints.Add(new Point3D(x, y, depth));
                        }
                    }
                }
            }

            // If we have enough points, fit a plane
            if (surfacePoints.Count > 30)
            {
                // Calculate plane coefficients (Ax + By + Cz + D = 0)
                // This is a simplified approach - a real RANSAC implementation would be more robust
                // but is beyond the scope of this feedback

                // For now, just calculate the average normal
                Vector3D normal = CalculateAverageSurfaceNormal(surfacePoints);

                // Use this normal to correct heights in the grid
                // The correction would adjust heights based on their position relative to the plane
            }
        }

        private Vector3D CalculateAverageSurfaceNormalOld(List<Point3D> points)
        {
            // Calculate centroid
            Point3D centroid = new Point3D(
                points.Average(p => p.X),
                points.Average(p => p.Y),
                points.Average(p => p.Z)
            );

            // Calculate covariance matrix
            double[,] covariance = new double[3, 3];
            foreach (var point in points)
            {
                double dx = point.X - centroid.X;
                double dy = point.Y - centroid.Y;
                double dz = point.Z - centroid.Z;

                covariance[0, 0] += dx * dx;
                covariance[0, 1] += dx * dy;
                covariance[0, 2] += dx * dz;
                covariance[1, 0] += dy * dx;
                covariance[1, 1] += dy * dy;
                covariance[1, 2] += dy * dz;
                covariance[2, 0] += dz * dx;
                covariance[2, 1] += dz * dy;
                covariance[2, 2] += dz * dz;
            }

            // Normalize
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    covariance[i, j] /= points.Count;
                }
            }

            // For a proper implementation, you would find the eigenvector with the smallest eigenvalue
            // As a simplification, we'll just use a simple heuristic

            // Simplified approach: assume the normal is roughly in the z direction
            return new Vector3D(0, 0, 1);
        }

        /// <summary>
        /// Initialize grid based on ROI dimensions
        /// </summary>
        private void InitializeHeightGrid()
        {
            if (!hasValidROI) return;

            gridWidth = (int)Math.Ceiling(detectedTableROI.Width / gridCellSize);
            gridHeight = (int)Math.Ceiling(detectedTableROI.Height / gridCellSize);
            heightGrid = new int[gridWidth, gridHeight];

            this.Dispatcher.Invoke(() => {
                StatusText = $"Height grid initialized: {gridWidth}x{gridHeight} cells ({gridCellSize}px)";
            });
        }

        /// <summary>
        /// Update the height grid with current depth data
        /// </summary>
        private void UpdateHeightGrid()
        {
            if (!hasValidROI || !hasValidTableDepth || heightGrid == null || !showHeightGrid)
                return;

            // Reset the grid
            Array.Clear(heightGrid, 0, heightGrid.Length);

            // Counters for each cell to compute averages
            int[,] cellCounts = new int[gridWidth, gridHeight];

            // Plane fitting parameters for angled surface correction
            Vector3D planeNormal = new Vector3D(0, 0, 1); // Default to flat
            Point3D planeOrigin = new Point3D(0, 0, tableDepth);

            // If using angled view, fit a plane to the table surface
            if (isAngledView)
            {
                FitPlaneToTableSurface(out planeNormal, out planeOrigin);
            }

            // Process depth data into the grid with plane correction
            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y++)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x++)
                {
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        // Skip invalid depths
                        if (depth == 0) continue;

                        // Apply plane correction for angled surfaces
                        double expectedTableDepth = tableDepth;

                        if (isAngledView)
                        {
                            // Calculate expected depth at this pixel using the plane equation
                            Point3D pixelPoint = new Point3D(x, y, 0);
                            Vector3D toPixel = new Vector3D(
                                pixelPoint.X - planeOrigin.X,
                                pixelPoint.Y - planeOrigin.Y,
                                0);

                            // Project to get expected Z (depth)
                            double dot = Vector3D.DotProduct(toPixel, planeNormal);
                            expectedTableDepth = planeOrigin.Z - (dot / planeNormal.Z);
                        }

                        // Skip if deeper than expected table surface (with tolerance)
                        if (depth >= expectedTableDepth + depthThreshold)
                            continue;

                        // Calculate height above expected table
                        int heightAboveTable = (int)(expectedTableDepth - depth);

                        // Only consider positive heights
                        if (heightAboveTable <= 0)
                            continue;

                        // Calculate grid cell
                        int cellX = (int)((x - detectedTableROI.X) / gridCellSize);
                        int cellY = (int)((y - detectedTableROI.Y) / gridCellSize);

                        if (cellX >= 0 && cellX < gridWidth && cellY >= 0 && cellY < gridHeight)
                        {
                            // Accumulate height for averaging
                            heightGrid[cellX, cellY] += heightAboveTable;
                            cellCounts[cellX, cellY]++;
                        }
                    }
                }
            }

            // Calculate averages with outlier rejection
            for (int cellY = 0; cellY < gridHeight; cellY++)
            {
                for (int cellX = 0; cellX < gridWidth; cellX++)
                {
                    if (cellCounts[cellX, cellY] > 0)
                    {
                        // Simple outlier rejection - require minimum number of points
                        if (cellCounts[cellX, cellY] >= 3)
                        {
                            heightGrid[cellX, cellY] /= cellCounts[cellX, cellY];
                        }
                        else
                        {
                            // Not enough samples, could be noise
                            heightGrid[cellX, cellY] = 0;
                        }
                    }
                    else
                    {
                        heightGrid[cellX, cellY] = 0; // No data
                    }
                }
            }

            // Update visualization
            this.Dispatcher.Invoke(() => {
                VisualizeHeightGrid();
            });
        }

        /// <summary>
        /// Fit a plane to the table surface to handle angled tables
        /// </summary>
        private void FitPlaneToTableSurface(out Vector3D normal, out Point3D origin)
        {
            normal = new Vector3D(0, 0, 1); // Default (flat table)
            origin = new Point3D(0, 0, tableDepth);

            if (depthData == null || !hasValidROI) return;

            List<Point3D> tablePoints = new List<Point3D>();

            // Sample points from the detected table surface
            int sampleStep = Math.Max(1, depthWidth / 50); // 50 samples across width

            for (int y = (int)detectedTableROI.Y; y < (int)(detectedTableROI.Y + detectedTableROI.Height); y += sampleStep)
            {
                for (int x = (int)detectedTableROI.X; x < (int)(detectedTableROI.X + detectedTableROI.Width); x += sampleStep)
                {
                    int idx = y * depthWidth + x;
                    ushort depth = depthData[idx];

                    // Use a tighter threshold for plane fitting
                    if (depth > 0 && Math.Abs(depth - tableDepth) <= depthThreshold / 2)
                    {
                        tablePoints.Add(new Point3D(x, y, depth));
                    }
                }
            }

            // Need enough points for robust fitting
            if (tablePoints.Count < 20) return;

            // Calculate centroid (mean point)
            double sumX = 0, sumY = 0, sumZ = 0;
            foreach (var point in tablePoints)
            {
                sumX += point.X;
                sumY += point.Y;
                sumZ += point.Z;
            }

            origin = new Point3D(
                sumX / tablePoints.Count,
                sumY / tablePoints.Count,
                sumZ / tablePoints.Count
            );

            // Use Principal Component Analysis for plane fitting
            // Build covariance matrix
            double[,] covariance = new double[3, 3];

            foreach (var point in tablePoints)
            {
                double dx = point.X - origin.X;
                double dy = point.Y - origin.Y;
                double dz = point.Z - origin.Z;

                covariance[0, 0] += dx * dx;
                covariance[0, 1] += dx * dy;
                covariance[0, 2] += dx * dz;
                covariance[1, 0] += dy * dx;
                covariance[1, 1] += dy * dy;
                covariance[1, 2] += dy * dz;
                covariance[2, 0] += dz * dx;
                covariance[2, 1] += dz * dy;
                covariance[2, 2] += dz * dz;
            }

            // Normalize
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    covariance[i, j] /= tablePoints.Count;
                }
            }

            // For a table, we expect the normal to be roughly in Z direction
            // so we can compute it from the other elements
            double nx = -covariance[0, 2] / Math.Max(0.001, covariance[2, 2]);
            double ny = -covariance[1, 2] / Math.Max(0.001, covariance[2, 2]);
            double nz = 1.0;

            // Normalize the normal vector
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            normal = new Vector3D(nx / length, ny / length, nz / length);

            // Adjust origin Z to make sure it's at table depth
            origin.Z = tableDepth;
        }

        /// <summary>
        /// Calculate the average surface normal from a set of points
        /// </summary>
        private Vector3D CalculateAverageSurfaceNormal(List<Point3D> points)
        {
            // Calculate centroid
            Point3D centroid = new Point3D(
                points.Average(p => p.X),
                points.Average(p => p.Y),
                points.Average(p => p.Z)
            );

            // Calculate covariance matrix
            double[,] covariance = new double[3, 3];
            foreach (var point in points)
            {
                double dx = point.X - centroid.X;
                double dy = point.Y - centroid.Y;
                double dz = point.Z - centroid.Z;

                covariance[0, 0] += dx * dx;
                covariance[0, 1] += dx * dy;
                covariance[0, 2] += dx * dz;
                covariance[1, 0] += dy * dx;
                covariance[1, 1] += dy * dy;
                covariance[1, 2] += dy * dz;
                covariance[2, 0] += dz * dx;
                covariance[2, 1] += dz * dy;
                covariance[2, 2] += dz * dz;
            }

            // Normalize
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    covariance[i, j] /= points.Count;
                }
            }

            // In a full implementation, you would find the eigenvector with the smallest eigenvalue
            // As a simplification, we'll estimate the normal based on the covariance

            // Simple approach: assume the normal is roughly in the z direction with x,y components
            double nx = -covariance[0, 2] / covariance[2, 2];
            double ny = -covariance[1, 2] / covariance[2, 2];
            double nz = 1.0;

            // Normalize
            double magnitude = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return new Vector3D(nx / magnitude, ny / magnitude, nz / magnitude);
        }

        /// <summary>
        /// Visualize the height grid on the DepthROICanvas
        /// </summary>
        private void VisualizeHeightGrid()
        {
            if (heightGrid == null || !hasValidROI || !showHeightGrid) return;

            // Create the canvas if it doesn't exist
            if (HeightGridCanvas == null)
            {
                HeightGridCanvas = new Canvas();
                DepthROICanvas.Children.Add(HeightGridCanvas);
            }
            else
            {
                HeightGridCanvas.Children.Clear();
            }

            // Scale factors to fit in the depth image display
            double scaleX = DepthImage.ActualWidth / depthWidth;
            double scaleY = DepthImage.ActualHeight / depthHeight;
            double cellSizeDisplayX = gridCellSize * scaleX;
            double cellSizeDisplayY = gridCellSize * scaleY;

            // Draw each cell
            for (int cellY = 0; cellY < gridHeight; cellY++)
            {
                for (int cellX = 0; cellX < gridWidth; cellX++)
                {
                    int height = heightGrid[cellX, cellY];

                    if (height > 0)
                    {
                        // Create a rectangle for the cell
                        Rectangle cellRect = new Rectangle
                        {
                            Width = cellSizeDisplayX,
                            Height = cellSizeDisplayY,
                            Stroke = new SolidColorBrush(Colors.Yellow),
                            StrokeThickness = 1
                        };

                        // Color based on height (0-100mm range)
                        byte intensity = (byte)Math.Min(255, height * 255 / 100);
                        cellRect.Fill = new SolidColorBrush(Color.FromArgb(100, (byte)(255 - intensity), intensity, 0));

                        // Position
                        double left = (detectedTableROI.X + cellX * gridCellSize) * scaleX;
                        double top = (detectedTableROI.Y + cellY * gridCellSize) * scaleY;
                        Canvas.SetLeft(cellRect, left);
                        Canvas.SetTop(cellRect, top);

                        HeightGridCanvas.Children.Add(cellRect);

                        // Add height text for cells with significant height
                        if (height > 10 && cellSizeDisplayX >= 15 && cellSizeDisplayY >= 15)
                        {
                            TextBlock heightText = new TextBlock
                            {
                                Text = height.ToString(),
                                Foreground = new SolidColorBrush(Colors.White),
                                FontSize = 8,
                                FontWeight = FontWeights.Bold
                            };

                            Canvas.SetLeft(heightText, left + cellSizeDisplayX / 2 - 5);
                            Canvas.SetTop(heightText, top + cellSizeDisplayY / 2 - 5);
                            HeightGridCanvas.Children.Add(heightText);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Toggle the height grid visualization
        /// </summary>
        private void ToggleHeightGrid_Click(object sender, RoutedEventArgs e)
        {
            showHeightGrid = !showHeightGrid;

            if (showHeightGrid)
            {
                // Initialize if needed
                if (heightGrid == null && hasValidROI)
                {
                    InitializeHeightGrid();
                }
                StatusText = "Height grid visualization enabled";
                ShowHeightGridMenuItem.IsChecked = true;
            }
            else
            {
                // Clear visualization
                if (HeightGridCanvas != null)
                {
                    HeightGridCanvas.Children.Clear();
                }
                StatusText = "Height grid visualization disabled";
                ShowHeightGridMenuItem.IsChecked = false;
            }
        }

        /// <summary>
        /// Handle changes to the grid cell size slider
        /// </summary>
        private void GridCellSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            gridCellSize = (int)e.NewValue;

            // Reinitialize with new size if we have a valid ROI
            if (hasValidROI && showHeightGrid)
            {
                InitializeHeightGrid();
            }

            StatusText = $"Grid cell size set to {gridCellSize} pixels";
        }

        /// <summary>
        /// Toggle calibration mode for manual playfield definition
        /// </summary>
        private void ToggleCalibrationMode()
        {
            isCalibrationMode = !isCalibrationMode;

            if (isCalibrationMode)
            {
                // Start calibration mode
                StartCalibrationMode();
            }
            else
            {
                // End calibration mode
                EndCalibrationMode();
            }
        }

        /// <summary>
        /// Start calibration mode for the playfield
        /// </summary>
        private void StartCalibrationMode()
        {
            // Clear existing calibration points
            calibrationPoints.Clear();

            // Create calibration overlay canvas if it doesn't exist
            if (calibrationOverlayCanvas == null)
            {
                calibrationOverlayCanvas = new Canvas();
                DepthROICanvas.Children.Add(calibrationOverlayCanvas);
            }
            else
            {
                calibrationOverlayCanvas.Children.Clear();
            }

            // Show instructions
            this.Dispatcher.Invoke(() => {
                StatusText = "Calibration Mode: Click on the 4 corners of your table surface (clockwise from top-left)";

                // Add instructional overlay
                TextBlock instructionsText = new TextBlock
                {
                    Text = "Click on the 4 corners of your table surface (clockwise from top-left)",
                    Foreground = new SolidColorBrush(Colors.Yellow),
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    FontSize = 18,
                    Padding = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 20, 0, 0)
                };

                calibrationOverlayCanvas.Children.Add(instructionsText);
                Canvas.SetLeft(instructionsText, (DepthImage.ActualWidth / 2) - 200);
                Canvas.SetTop(instructionsText, 10);
            });

            // Enable mouse events for calibration
            DepthImage.MouseDown += CalibrationMouseDown;
        }

        /// <summary>
        /// End calibration mode and apply the calibrated playfield
        /// </summary>
        private void EndCalibrationMode()
        {
            // Disable mouse events for calibration
            DepthImage.MouseDown -= CalibrationMouseDown;

            // Apply calibration if we have all required points
            if (calibrationPoints.Count == CALIBRATION_POINTS_REQUIRED)
            {
                ApplyPlayfieldCalibration();
            }

            // Clean up
            this.Dispatcher.Invoke(() => {
                if (calibrationPoints.Count < CALIBRATION_POINTS_REQUIRED)
                {
                    StatusText = "Calibration cancelled - not enough points selected";
                }

                // Keep the overlay but mark as completed
                if (calibrationOverlayCanvas != null)
                {
                    TextBlock completeText = new TextBlock
                    {
                        Text = calibrationPoints.Count == CALIBRATION_POINTS_REQUIRED ?
                            "Calibration Complete" : "Calibration Cancelled",
                        Foreground = new SolidColorBrush(
                            calibrationPoints.Count == CALIBRATION_POINTS_REQUIRED ? Colors.LightGreen : Colors.Orange),
                        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                        FontSize = 18,
                        Padding = new Thickness(10),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 0)
                    };

                    calibrationOverlayCanvas.Children.Add(completeText);
                    Canvas.SetLeft(completeText, (DepthImage.ActualWidth / 2) - 75);
                    Canvas.SetTop(completeText, 10);

                    // Fade out after 3 seconds
                    System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                    {
                        this.Dispatcher.Invoke(() => {
                            calibrationOverlayCanvas.Children.Clear();
                        });
                    });
                }
            });
        }

        /// <summary>
        /// Handle mouse clicks during calibration mode
        /// </summary>
        private void CalibrationMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isCalibrationMode) return;

            // Get click position
            Point clickPoint = e.GetPosition(DepthImage);

            // Scale to actual depth image coordinates
            double scaleX = depthWidth / DepthImage.ActualWidth;
            double scaleY = depthHeight / DepthImage.ActualHeight;

            Point scaledPoint = new Point(
                clickPoint.X * scaleX,
                clickPoint.Y * scaleY
            );

            // Add to calibration points
            calibrationPoints.Add(scaledPoint);

            // Update the UI
            this.Dispatcher.Invoke(() => {
                // Draw a marker at the clicked point
                Ellipse marker = new Ellipse
                {
                    Width = 14,
                    Height = 14,
                    Fill = new SolidColorBrush(Colors.Yellow),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 2
                };

                calibrationOverlayCanvas.Children.Add(marker);
                Canvas.SetLeft(marker, clickPoint.X - 7);
                Canvas.SetTop(marker, clickPoint.Y - 7);

                // Add point number
                TextBlock pointNumber = new TextBlock
                {
                    Text = calibrationPoints.Count.ToString(),
                    Foreground = new SolidColorBrush(Colors.Black),
                    FontWeight = FontWeights.Bold,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                calibrationOverlayCanvas.Children.Add(pointNumber);
                Canvas.SetLeft(pointNumber, clickPoint.X - 4);
                Canvas.SetTop(pointNumber, clickPoint.Y - 7);

                // Update status message
                if (calibrationPoints.Count < CALIBRATION_POINTS_REQUIRED)
                {
                    StatusText = $"Calibration Point {calibrationPoints.Count} set. Click on point {calibrationPoints.Count + 1}";
                }
                else
                {
                    // Draw polygon connecting all points
                    Polygon polygon = new Polygon
                    {
                        Points = new PointCollection(calibrationPoints.Select(p =>
                            new Point(p.X / scaleX, p.Y / scaleY))),
                        Stroke = new SolidColorBrush(Colors.Yellow),
                        StrokeThickness = 2,
                        Fill = new SolidColorBrush(Color.FromArgb(50, 255, 255, 0))
                    };

                    // Insert at beginning to avoid covering points
                    calibrationOverlayCanvas.Children.Insert(0, polygon);

                    StatusText = "All calibration points set. Calculating playfield...";

                    // Automatically end calibration mode
                    isCalibrationMode = false;
                    EndCalibrationMode();
                }
            });
        }

        /// <summary>
        /// Apply the playfield calibration from the selected points
        /// </summary>
        private void ApplyPlayfieldCalibration()
        {
            if (calibrationPoints.Count != CALIBRATION_POINTS_REQUIRED)
                return;

            try
            {
                // Calculate bounding box of the points
                double minX = calibrationPoints.Min(p => p.X);
                double maxX = calibrationPoints.Max(p => p.X);
                double minY = calibrationPoints.Min(p => p.Y);
                double maxY = calibrationPoints.Max(p => p.Y);

                // Create ROI rect from bounding box
                manualPlayfieldRect = new Rect(minX, minY, maxX - minX, maxY - minY);

                // Set as the active ROI
                detectedTableROI = manualPlayfieldRect;
                hasValidROI = true;

                // Calculate depth for the table surface from the points
                List<ushort> pointDepths = new List<ushort>();
                foreach (var point in calibrationPoints)
                {
                    int x = (int)point.X;
                    int y = (int)point.Y;

                    // Ensure within bounds
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        if (depth > 0)
                        {
                            pointDepths.Add(depth);
                        }
                    }
                }

                // If we have valid depths, use median as table depth
                if (pointDepths.Count > 0)
                {
                    pointDepths.Sort();
                    ushort medianDepth = pointDepths[pointDepths.Count / 2];

                    // Set as table depth
                    tableDepth = medianDepth;
                    hasValidTableDepth = true;
                    tableDepthLocked = true; // Lock to prevent auto-detection from changing it

                    this.Dispatcher.Invoke(() => {
                        TableDepthText = $"{tableDepth} mm (calibrated)";
                    });
                }

                // Initialize height grid if enabled
                if (showHeightGrid)
                {
                    InitializeHeightGrid();
                }

                this.Dispatcher.Invoke(() => {
                    StatusText = $"Playfield calibrated: {detectedTableROI.Width:F0}x{detectedTableROI.Height:F0}, depth: {tableDepth} mm";
                });

                // Save settings for next time
                AutoSaveSettings("Calibrated ROI");
            }
            catch (Exception ex)
            {
                this.Dispatcher.Invoke(() => {
                    StatusText = $"Error applying calibration: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Sample depth values in a neighborhood to get a more stable table depth
        /// </summary>
        private ushort SampleTableDepthAtPoint(Point point, int radius = 3)
        {
            List<ushort> depths = new List<ushort>();

            int centerX = (int)point.X;
            int centerY = (int)point.Y;

            // Sample points in a square neighborhood
            for (int y = centerY - radius; y <= centerY + radius; y++)
            {
                for (int x = centerX - radius; x <= centerX + radius; x++)
                {
                    // Ensure within bounds
                    if (x >= 0 && x < depthWidth && y >= 0 && y < depthHeight)
                    {
                        int idx = y * depthWidth + x;
                        ushort depth = depthData[idx];

                        if (depth > 0)
                        {
                            depths.Add(depth);
                        }
                    }
                }
            }

            // Return median depth if we have samples
            if (depths.Count > 0)
            {
                depths.Sort();
                return depths[depths.Count / 2];
            }

            // Fallback: return center point depth or 0 if invalid
            int centerIdx = centerY * depthWidth + centerX;
            return (centerX >= 0 && centerX < depthWidth && centerY >= 0 && centerY < depthHeight) ?
                depthData[centerIdx] : (ushort)0;
        }

        /// <summary>
        /// Creates a perspective transform matrix from the calibration points
        /// for mapping between physical and virtual spaces
        /// </summary>
        private Matrix GetPerspectiveTransformFromCalibration(Point[] sourcePoints, Point[] destinationPoints)
        {
            if (sourcePoints.Length != 4 || destinationPoints.Length != 4)
                throw new ArgumentException("Both source and destination must have exactly 4 points");

            // Perspective transform can be computed by solving a system of linear equations
            // This is a simplified implementation - for production use, consider using a more robust library

            // For proof-of-concept, we'll use an affine transform which is simpler
            // but doesn't account for perspective distortion
            return ComputeAffineTransform(sourcePoints, destinationPoints);
        }

        /// <summary>
        /// Compute an affine transform matrix from three or more points
        /// </summary>
        private Matrix ComputeAffineTransform(Point[] sourcePoints, Point[] destinationPoints)
        {
            // We need at least 3 points for an affine transform
            if (sourcePoints.Length < 3 || destinationPoints.Length < 3)
                throw new ArgumentException("At least 3 points are needed for affine transform");

            // For simplicity, we'll use the first 3 points
            Point s1 = sourcePoints[0];
            Point s2 = sourcePoints[1];
            Point s3 = sourcePoints[2];

            Point d1 = destinationPoints[0];
            Point d2 = destinationPoints[1];
            Point d3 = destinationPoints[2];

            // Solve for transform coefficients
            // [ x' ]   [ a  b  c ] [ x ]
            // [ y' ] = [ d  e  f ] [ y ]
            // [ 1  ]   [ 0  0  1 ] [ 1 ]

            // Set up equations for x coordinates
            var m1 = Matrix.Identity;
            m1.M11 = (d2.X - d1.X) / (s2.X - s1.X);
            m1.M12 = (d3.X - d1.X) / (s3.Y - s1.Y);
            m1.OffsetX = d1.X - (m1.M11 * s1.X + m1.M12 * s1.Y);

            // Set up equations for y coordinates
            m1.M21 = (d2.Y - d1.Y) / (s2.X - s1.X);
            m1.M22 = (d3.Y - d1.Y) / (s3.Y - s1.Y);
            m1.OffsetY = d1.Y - (m1.M21 * s1.X + m1.M22 * s1.Y);

            return m1;
        }

        /// <summary>
        /// Add a "Set ROI" button to the UI for manual calibration
        /// </summary>
        private void AddPlayfieldCalibrationButton()
        {
            this.Dispatcher.Invoke(() => {
                var panel = FindName("AdvancedFeaturesPanel") as StackPanel;
                if (panel != null)
                {
                    var calibrationButton = new Button
                    {
                        Content = "Calibrate Playfield",
                        Padding = new Thickness(5, 2, 5, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        ToolTip = "Manually set the table region by clicking the 4 corners"
                    };

                    calibrationButton.Click += (s, e) => ToggleCalibrationMode();
                    panel.Children.Add(calibrationButton);
                }
            });
        }

        /// <summary>
        /// Handles the camera angle mode change (overhead vs. angled)
        /// </summary>
        private void HandleCameraAngleChange()
        {
            // Update parameters based on camera angle
            if (isAngledView)
            {
                // For angled view, use more tolerant parameters
                depthThreshold = ANGLED_DEG_MAX;
                miniatureBaseThreshold = 0.8; // More tolerant base-to-height ratio
            }
            else
            {
                // For overhead view, use stricter parameters
                depthThreshold = ANGLED_DEG_MIN;
                miniatureBaseThreshold = 0.5; // Stricter base-to-height ratio
            }

            // Clear depth history to rebuild with new parameters
            depthHistory.Clear();

            this.Dispatcher.Invoke(() => {
                StatusText = isAngledView ?
                    "Angled view mode - using relaxed detection parameters" :
                    "Overhead view mode - using strict detection parameters";
            });
        }
    }

    /// <summary>
    /// Extensions to improve the MainWindow class organization
    /// </summary>
    public static class MainWindowExtensions
    {
        /// <summary>
        /// Find child control by name in visual tree
        /// </summary>
        public static T FindVisualChild<T>(this DependencyObject parent, string childName) where T : DependencyObject
        {
            // Check for null reference
            if (parent == null) return null;

            T foundChild = null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                // If the child is the type we're looking for and has the specified name
                T childType = child as T;
                if (childType != null)
                {
                    if (child is FrameworkElement frameworkElement && frameworkElement.Name == childName)
                    {
                        foundChild = childType;
                        break;
                    }
                }

                // Recurse into children
                foundChild = FindVisualChild<T>(child, childName);

                if (foundChild != null)
                    break;
            }

            return foundChild;
        }
    }

    /// <summary>
    /// Enhanced version of the original mIniatureCandidate class with additional metrics
    /// </summary>
    public class EnhancedMiniatureCandidate
    {
        public Point BaseCenter { get; set; }
        public double BaseDiameter { get; set; }
        public List<Point> BasePoints { get; set; } = new List<Point>();
        public List<Point> AllPoints { get; set; } = new List<Point>();
        public Dictionary<int, List<Point>> HeightLayers { get; set; } = new Dictionary<int, List<Point>>();
        public int MaxHeight { get; set; }
        public double BaseToHeightRatio { get; set; }
        public double Circularity { get; set; } // 1.0 = perfect circle
        public double Stability { get; set; } // Higher = more stable detection between frames
        public ushort Depth { get; set; }
        public TokenType PredictedType { get; set; } = TokenType.Unknown;

        /// <summary>
        /// Calculate circularity metric for the base
        /// </summary>
        public void CalculateCircularity()
        {
            if (BasePoints.Count < 5)
            {
                // Not enough points for meaningful calculation
                Circularity = 0.5; // Default
                return;
            }

            // Find perimeter
            double perimeter = CalculatePerimeter(BasePoints);

            // Calculate area
            double area = BasePoints.Count; // Simple approximation using point count

            // Circularity formula: 4π*area/perimeter²
            double circularityValue = (4 * Math.PI * area) / (perimeter * perimeter);

            // Clamp to range [0,1] where 1 is perfect circle
            Circularity = Math.Min(1.0, Math.Max(0.0, circularityValue));
        }

        /// <summary>
        /// Approximate perimeter calculation
        /// </summary>
        private double CalculatePerimeter(List<Point> points)
        {
            // Sort points by polar angle from center for simple convex hull
            List<Point> sortedPoints = new List<Point>(points);

            // Calculate centroid as reference point
            double sumX = 0, sumY = 0;
            foreach (var pt in points)
            {
                sumX += pt.X;
                sumY += pt.Y;
            }
            double centerX = sumX / points.Count;
            double centerY = sumY / points.Count;

            // Sort by angle
            sortedPoints.Sort((a, b) => {
                double angleA = Math.Atan2(a.Y - centerY, a.X - centerX);
                double angleB = Math.Atan2(b.Y - centerY, b.X - centerX);
                return angleA.CompareTo(angleB);
            });

            // Calculate perimeter by summing distances between adjacent points
            double perimeter = 0;
            for (int i = 0; i < sortedPoints.Count; i++)
            {
                int nextIdx = (i + 1) % sortedPoints.Count;
                perimeter += Distance(sortedPoints[i], sortedPoints[nextIdx]);
            }

            return perimeter;
        }

        /// <summary>
        /// Calculate distance between two points
        /// </summary>
        private double Distance(Point p1, Point p2)
        {
            return Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
        }
    }
}
