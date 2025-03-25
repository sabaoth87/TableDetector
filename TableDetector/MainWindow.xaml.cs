using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Kinect;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Linq;

namespace TableDetectionApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // Kinect sensor and readers
        private KinectSensor kinectSensor = null;
        private MultiSourceFrameReader multiFrameReader = null;

        // Image data 
        private WriteableBitmap depthBitmap = null;
        private WriteableBitmap colorBitmap = null;
        private ushort[] depthData = null;
        private byte[] depthPixels = null;
        private byte[] colorData = null;
        private int depthWidth = 0;
        private int depthHeight = 0;
        private int colorWidth = 0;
        private int colorHeight = 0;

        // Table detection properties
        private ushort tableDepth = 0;
        private bool tableDepthLocked = false;
        private Queue<ushort> depthHistory = new Queue<ushort>();
        private int maxHistorySize = 10;
        private bool showDepthContours = true;
        private bool isAngledView = true; // Default to assume angled view for robustness
        private int depthThreshold = 30;  // Default depth threshold for surface detection (in mm)

        // Property change notification
        public event PropertyChangedEventHandler PropertyChanged;

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

        public MainWindow()
        {
            InitializeComponent();

            // Set DataContext for binding
            this.DataContext = this;

            // Initialize Kinect sensor
            this.kinectSensor = KinectSensor.GetDefault();

            if (this.kinectSensor != null)
            {
                // Open the sensor
                this.kinectSensor.Open();

                // Get frame description for color and depth
                FrameDescription colorFrameDesc = this.kinectSensor.ColorFrameSource.FrameDescription;
                FrameDescription depthFrameDesc = this.kinectSensor.DepthFrameSource.FrameDescription;

                // Store dimensions
                colorWidth = colorFrameDesc.Width;
                colorHeight = colorFrameDesc.Height;
                depthWidth = depthFrameDesc.Width;
                depthHeight = depthFrameDesc.Height;

                // Allocate pixel arrays
                colorData = new byte[colorFrameDesc.Width * colorFrameDesc.Height * 4];
                depthData = new ushort[depthFrameDesc.Width * depthFrameDesc.Height];
                depthPixels = new byte[depthFrameDesc.Width * depthFrameDesc.Height * 4];

                // Create bitmap sources
                this.colorBitmap = new WriteableBitmap(
                    colorFrameDesc.Width,
                    colorFrameDesc.Height,
                    96.0, 96.0,
                    PixelFormats.Bgra32, null);

                this.depthBitmap = new WriteableBitmap(
                    depthFrameDesc.Width,
                    depthFrameDesc.Height,
                    96.0, 96.0,
                    PixelFormats.Bgra32, null);

                // Set image sources to the named elements from XAML
                this.ColorImage.Source = this.colorBitmap;
                this.DepthImage.Source = this.depthBitmap;

                // Create multi frame reader
                this.multiFrameReader = this.kinectSensor.OpenMultiSourceFrameReader(
                    FrameSourceTypes.Color | FrameSourceTypes.Depth);

                // Register for frame arrived events
                if (this.multiFrameReader != null)
                {
                    this.multiFrameReader.MultiSourceFrameArrived += Reader_MultiSourceFrameArrived;
                    StatusText = "Kinect initialized. Waiting for frames...";
                }
                else
                {
                    StatusText = "Failed to create MultiSourceFrameReader";
                }
            }
            else
            {
                StatusText = "No Kinect sensor detected";
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Clean up Kinect resources
            if (this.multiFrameReader != null)
            {
                this.multiFrameReader.MultiSourceFrameArrived -= Reader_MultiSourceFrameArrived;
                this.multiFrameReader.Dispose();
                this.multiFrameReader = null;
            }

            if (this.kinectSensor != null)
            {
                this.kinectSensor.Close();
                this.kinectSensor = null;
            }
        }

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

                        // Process depth into visualization
                        ProcessDepthData();

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

        private void DetermineTableSurfaceDepth()
        {
            if (depthData == null || depthData.Length == 0)
                return;

            // Find planar surfaces - we don't just want a histogram of depths,
            // we want to find continuous flat surfaces in the scene

            // Step 1: Scan the entire depth image to find potential surface regions
            Dictionary<ushort, List<Point>> depthRegions = new Dictionary<ushort, List<Point>>();
            // We'll use a broader quantization to group similar depths
            int depthQuantization = 20; // Group depths into 20mm ranges

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
                        ushort quantizedDepth = (ushort)(depth / depthQuantization * depthQuantization);

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

            // Update the UI to show surface detection stats
            this.Dispatcher.Invoke(() => {
                if (hasTableDepth && showDepthContours)
                {
                    double surfacePercentage = (100.0 * tableSurfaceCount) / (depthWidth * depthHeight);
                    TableDepthText = $"{tableDepth} mm ({surfacePercentage:F1}%)";
                }
            });
        }

        private void LockTable_Click(object sender, RoutedEventArgs e)
        {
            if (tableDepth > 0)
            {
                tableDepthLocked = true;
                StatusText = $"Table depth locked at {tableDepth} mm";
            }
            else
            {
                StatusText = "Cannot lock table depth: No valid depth detected";
            }
        }

        private void UnlockTable_Click(object sender, RoutedEventArgs e)
        {
            tableDepthLocked = false;
            depthHistory.Clear(); // Reset history
            StatusText = "Table depth detection switched to automatic mode";
        }

        private void ToggleContours_Click(object sender, RoutedEventArgs e)
        {
            showDepthContours = !showDepthContours;
            if (showDepthContours)
                StatusText = "Depth contours enabled";
            else
                StatusText = "Depth contours disabled";
        }

        private void FindLargestSurface_Click(object sender, RoutedEventArgs e)
        {
            // Force a new detection of the largest surface in the scene
            tableDepthLocked = false;
            depthHistory.Clear();  // Clear history to start fresh

            // Force more aggressive scanning
            depthThreshold = isAngledView ? 40 : 20;  // Increase threshold for angled views

            // Run the detection algorithm
            DetermineTableSurfaceDepthExhaustive();

            StatusText = $"Searching for largest flat surface...";

            // Update the display
            ProcessDepthData();

            this.Dispatcher.Invoke(() => {
                this.depthBitmap.Lock();
                Marshal.Copy(depthPixels, 0, this.depthBitmap.BackBuffer, depthPixels.Length);
                this.depthBitmap.AddDirtyRect(new Int32Rect(0, 0, depthWidth, depthHeight));
                this.depthBitmap.Unlock();

                StatusText = $"Found largest surface at depth: {tableDepth} mm";
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
            int planeThreshold = isAngledView ? 40 : 20; // Depth variation tolerance (mm)
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

        private void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            // Create a diagnostic message
            string message = $"=== TABLE DEPTH DIAGNOSTIC ===\n" +
                             $"Current table depth: {tableDepth} mm\n" +
                             $"Table depth locked: {tableDepthLocked}\n" +
                             $"Depth history buffer size: {depthHistory.Count}\n" +
                             $"Contours enabled: {showDepthContours}\n" +
                             $"Angled view mode: {isAngledView}\n" +
                             $"Depth threshold: {depthThreshold} mm\n";

            // Sample the current histogram
            Dictionary<ushort, int> quickHistogram = new Dictionary<ushort, int>();
            int sampleCount = 0;

            if (depthData != null && depthData.Length > 0)
            {
                for (int i = 0; i < depthData.Length; i += 20) // Sample sparsely
                {
                    ushort depth = depthData[i];
                    if (depth > 400 && depth < 4000) // Only consider reasonable depths
                    {
                        if (!quickHistogram.ContainsKey(depth))
                            quickHistogram[depth] = 0;
                        quickHistogram[depth]++;
                        sampleCount++;
                    }
                }

                message += $"\nDepth Frame: {sampleCount} samples analyzed\n";

                if (sampleCount > 0)
                {
                    // Find the most common depth
                    ushort mostCommonDepth = 0;
                    int mostCommonCount = 0;

                    // Convert to list for sorting
                    List<KeyValuePair<ushort, int>> pairs = new List<KeyValuePair<ushort, int>>(quickHistogram);
                    pairs.Sort((a, b) => b.Value.CompareTo(a.Value)); // Sort descending by count

                    if (pairs.Count > 0)
                    {
                        mostCommonDepth = pairs[0].Key;
                        mostCommonCount = pairs[0].Value;

                        double percentage = 100.0 * mostCommonCount / sampleCount;
                        message += $"Most common depth: {mostCommonDepth}mm ({percentage:F1}% of samples)\n";

                        // Show top 5 most common depths
                        message += "Top depth values:\n";
                        int rank = 1;
                        foreach (var pair in pairs.Take(5))
                        {
                            double pct = 100.0 * pair.Value / sampleCount;
                            message += $"  {rank}. {pair.Key}mm: {pair.Value} samples ({pct:F1}%)\n";
                            rank++;
                        }
                    }
                }
            }

            // Display in a message box
            MessageBox.Show(message, "Table Depth Diagnostic", MessageBoxButton.OK, MessageBoxImage.Information);
        }

            private void AngledView_Changed(object sender, RoutedEventArgs e)
        {
            isAngledView = AngledViewCheckBox.IsChecked ?? false;

            // Adjust thresholds based on view setting
            depthThreshold = isAngledView ? 30 : 15;

            StatusText = isAngledView ?
                "Angled view mode enabled - using adaptive surface detection" :
                "Direct overhead view mode - using tighter thresholds";
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

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}