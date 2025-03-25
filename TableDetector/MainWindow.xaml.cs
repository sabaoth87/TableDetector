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
using System.IO;
using System.Windows.Media.Media3D;

namespace TableDetector
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
        private int depthThreshold = 100;  // Default depth threshold for surface detection (in mm)

        // ROI visualization properties
        private Rect detectedTableROI = Rect.Empty;
        private bool showROIOverlay = true;
        private bool isDragging = false;
        private Point startPoint;
        private Rectangle selectionRectangle = null;
        private UIElement currentCanvas = null;

        // TTRPG token tracking properties
        private int MIN_TOKEN_HEIGHT = 10; // Minimum height of a token in mm
        private int MAX_TOKEN_HEIGHT = 50; // Maximum height of a token in mm
        private List<TTRPGToken> detectedTokens = new List<TTRPGToken>();
        private bool trackTokens = true;
        private int tokenDetectionThreshold = 15; // Minimum pixel count to consider as a token
        private bool showTokenLabels = true;
        private DateTime lastTokenUpdateTime = DateTime.MinValue;
        private TimeSpan tokenUpdateInterval = TimeSpan.FromMilliseconds(100); // Update tokens 10 times per second

        //
        private ComboBox TokenTypeComboBox;

        // Menu item references
        //private MenuItem ShowROIMenuItem;
        //private MenuItem AngledViewMenuItem;
        //private MenuItem ShowDepthContoursMenuItem;
        //private MenuItem TrackTokensMenuItem;
        //private MenuItem ShowTokenLabelsMenuItem;

        // Settings path for persistence
        private string settingsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TableDetector",
            "settings.json");

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

        public MainWindow()
        {
            InitializeComponent();
            InitializeSettings();
            // Set DataContext for binding
            this.DataContext = this;

            // Create directory for settings if it doesn't exist
            string directory = System.IO.Path.GetDirectoryName(settingsFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

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

            // Connect to UI controls after they're loaded
            this.Loaded += (s, e) => InitializeControlReferences();
        }

        /// <summary>
        /// Initializes application settings - loads existing settings or creates defaults
        /// </summary>
        private void InitializeSettings()
        {
            try
            {
                // Create settings directory if it doesn't exist
                string directory = System.IO.Path.GetDirectoryName(settingsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Try to load existing settings
                LoadSettings();

                // If this is the first run (no settings file), create default settings
                if (!File.Exists(settingsFilePath))
                {
                    // Set default values
                    MIN_TOKEN_HEIGHT = 10;
                    MAX_TOKEN_HEIGHT = 50;
                    tokenDetectionThreshold = 15;
                    depthThreshold = isAngledView ? 50 : 30;
                    maxHistorySize = 10;

                    // Save default settings
                    SaveSettings();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error initializing settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Creates the TokenTypeComboBox if it doesn't exist already
        /// </summary>
        private void InitializeTokenTypeComboBox()
        {
            // If the combo box doesn't exist in XAML, create it programmatically
            if (TokenTypeComboBox == null)
            {
                TokenTypeComboBox = new ComboBox
                {
                    Width = 120,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Add token type options
                TokenTypeComboBox.Items.Add("Small Token");
                TokenTypeComboBox.Items.Add("Medium Token");
                TokenTypeComboBox.Items.Add("Large Token");
                TokenTypeComboBox.Items.Add("Miniature");
                TokenTypeComboBox.Items.Add("Dice");
                TokenTypeComboBox.Items.Add("Custom");
                TokenTypeComboBox.SelectedIndex = 0;

                // Find a place to add it in the UI - this might need adjustment based on your XAML structure
                var tokenTrackingPanel = this.FindName("TokenTrackingPanel") as StackPanel;
                if (tokenTrackingPanel != null)
                {
                    var label = new TextBlock
                    {
                        Text = "Token Type:",
                        Foreground = new SolidColorBrush(Colors.White),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(10, 0, 0, 0)
                    };

                    tokenTrackingPanel.Children.Add(label);
                    tokenTrackingPanel.Children.Add(TokenTypeComboBox);
                }
                else
                {
                    // If we can't find the panel, create a contingency plan
                    // This assumes the last row in your Grid is for token tracking controls
                    var mainGrid = this.Content as Grid;
                    if (mainGrid != null && mainGrid.RowDefinitions.Count > 0)
                    {
                        var lastRow = mainGrid.RowDefinitions.Count - 1;

                        var container = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Margin = new Thickness(0, 5, 0, 0)
                        };

                        var label = new TextBlock
                        {
                            Text = "Token Type:",
                            Foreground = new SolidColorBrush(Colors.White),
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        container.Children.Add(label);
                        container.Children.Add(TokenTypeComboBox);

                        Grid.SetRow(container, lastRow);
                        mainGrid.Children.Add(container);
                    }
                }
            }
        }

        /// <summary>
        /// Called when the application is closing
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // Save current settings before exiting
            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

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

        /// <summary>
        /// Helper method to auto-save settings when important settings are changed
        /// </summary>
        private void AutoSaveSettings(string settingName = null)
        {
            try
            {
                // Optional delay/debounce for auto-saving (avoid saving too frequently)
                // You could implement a timer-based approach here if needed

                // For now, just save directly
                SaveSettings();

                if (!string.IsNullOrEmpty(settingName))
                {
                    StatusText = $"Setting '{settingName}' updated and saved";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error auto-saving settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Exports current settings to a user-selected file
        /// </summary>
        private void ExportSettings()
        {
            try
            {
                // Create a save file dialog
                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JSON Settings Files (*.json)|*.json|All Files (*.*)|*.*",
                    DefaultExt = ".json",
                    Title = "Export Settings"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    // First ensure settings are up to date
                    SaveSettings();

                    // Then copy the settings file to the user-selected location
                    File.Copy(settingsFilePath, saveDialog.FileName, true);

                    StatusText = $"Settings exported to {saveDialog.FileName}";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error exporting settings: {ex.Message}";
                MessageBox.Show($"Error exporting settings: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Imports settings from a user-selected file
        /// </summary>
        private void ImportSettings()
        {
            try
            {
                // Create an open file dialog
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "JSON Settings Files (*.json)|*.json|All Files (*.*)|*.*",
                    Title = "Import Settings"
                };

                if (openDialog.ShowDialog() == true)
                {
                    // Validate that this is a proper settings file
                    bool isValid = false;
                    try
                    {
                        string json = File.ReadAllText(openDialog.FileName);
                        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                        {
                            // Check for required top-level properties
                            isValid = doc.RootElement.TryGetProperty("TableDetection", out _) &&
                                      doc.RootElement.TryGetProperty("TokenTracking", out _);
                        }
                    }
                    catch
                    {
                        isValid = false;
                    }

                    if (!isValid)
                    {
                        MessageBox.Show("The selected file does not appear to be a valid TableDetector settings file.",
                            "Invalid Settings File", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Backup current settings
                    string backupFile = settingsFilePath + ".backup";
                    File.Copy(settingsFilePath, backupFile, true);

                    // Copy the selected file to the settings location
                    File.Copy(openDialog.FileName, settingsFilePath, true);

                    // Reload settings
                    LoadSettings();

                    StatusText = $"Settings imported from {openDialog.FileName}";

                    // Inform user that a restart might be needed for all settings to take effect
                    MessageBox.Show("Settings have been imported successfully. Some settings may require restarting the application to take full effect.",
                        "Settings Imported", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error importing settings: {ex.Message}";
                MessageBox.Show($"Error importing settings: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Resets all settings to default values
        /// </summary>
        private void ResetSettings()
        {
            try
            {
                // Ask for confirmation
                var result = MessageBox.Show("Are you sure you want to reset all settings to default values? This cannot be undone.",
                    "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                // Backup current settings
                if (File.Exists(settingsFilePath))
                {
                    string backupFile = settingsFilePath + ".backup";
                    File.Copy(settingsFilePath, backupFile, true);

                    // Delete current settings file
                    File.Delete(settingsFilePath);
                }

                // Set default values
                MIN_TOKEN_HEIGHT = 10;
                MAX_TOKEN_HEIGHT = 50;
                tokenDetectionThreshold = 15;
                depthThreshold = isAngledView ? 50 : 30;
                maxHistorySize = 10;
                trackTokens = true;
                showTokenLabels = true;
                showDepthContours = true;
                showROIOverlay = true;

                // Create a new settings file
                SaveSettings();

                // Update UI
                this.Dispatcher.Invoke(() => {
                    if (TrackTokensCheckBox != null)
                        TrackTokensCheckBox.IsChecked = trackTokens;

                    if (ShowTokenLabelsCheckBox != null)
                        ShowTokenLabelsCheckBox.IsChecked = showTokenLabels;

                    if (TokenSizeThresholdSlider != null)
                        TokenSizeThresholdSlider.Value = tokenDetectionThreshold;

                    if (AngledViewCheckBox != null)
                        AngledViewCheckBox.IsChecked = isAngledView;

                    if (ShowROICheckBox != null)
                        ShowROICheckBox.IsChecked = showROIOverlay;
                });

                StatusText = "All settings reset to default values";
            }
            catch (Exception ex)
            {
                StatusText = $"Error resetting settings: {ex.Message}";
                MessageBox.Show($"Error resetting settings: {ex.Message}", "Reset Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Event handler for the Exit menu item
        /// </summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            // Close the application
            this.Close();
        }

        /// <summary>
        /// Event handler for the Export Settings menu item
        /// </summary>
        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            ExportSettings();
        }

        /// <summary>
        /// Event handler for the Import Settings menu item
        /// </summary>
        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            ImportSettings();
        }

        /// <summary>
        /// Event handler for the Reset Settings menu item
        /// </summary>
        private void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            ResetSettings();
        }

        /// <summary>
        /// Event handler for the Show ROI menu item
        /// </summary>
        private void ShowROI_MenuClick(object sender, RoutedEventArgs e)
        {
            // Update the checkbox to match the menu item
            ShowROICheckBox.IsChecked = ShowROIMenuItem.IsChecked;

            // The checkbox's event handler will take care of the rest
            ShowROI_Changed(sender, e);
        }

        /// <summary>
        /// Event handler for the Angled View menu item
        /// </summary>
        private void AngledView_MenuClick(object sender, RoutedEventArgs e)
        {
            // Update the checkbox to match the menu item
            AngledViewCheckBox.IsChecked = AngledViewMenuItem.IsChecked;

            // The checkbox's event handler will take care of the rest
            AngledView_Changed(sender, e);
        }

        /// <summary>
        /// Event handler for the Track Tokens menu item
        /// </summary>
        private void TrackTokens_MenuClick(object sender, RoutedEventArgs e)
        {
            // Update the checkbox to match the menu item
            TrackTokensCheckBox.IsChecked = TrackTokensMenuItem.IsChecked;

            // The checkbox's event handler will take care of the rest
            TrackTokens_Changed(sender, e);
        }

        /// <summary>
        /// Event handler for the Show Token Labels menu item
        /// </summary>
        private void ShowTokenLabels_MenuClick(object sender, RoutedEventArgs e)
        {
            // Update the checkbox to match the menu item
            ShowTokenLabelsCheckBox.IsChecked = ShowTokenLabelsMenuItem.IsChecked;

            // The checkbox's event handler will take care of the rest
            ShowTokenLabels_Changed(sender, e);
        }

        /// <summary>
        /// Event handler for the About menu item
        /// </summary>
        private void About_Click(object sender, RoutedEventArgs e)
        {
            // Show a simple about dialog
            MessageBox.Show(
                "Table Detector for TTRPG\n" +
                "Version 1.0\n\n" +
                "Uses Kinect for Windows SDK to detect and track tokens on a tabletop surface.\n\n" +
                "© 2025 Your Name/Company",
                "About Table Detector",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                            DetectTokens();
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
            int depthTolerance = isAngledView ? 30 : 15;

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
                detectedTableROI = new Rect(minX, minY, maxX - minX, maxY - minY);

                this.Dispatcher.Invoke(() => {
                    StatusText = $"Updated table ROI: {detectedTableROI.Width}x{detectedTableROI.Height}";
                });
            }
        }

        /// <summary>
        /// Detects objects (tokens) on the table surface
        /// </summary>
        private void DetectTokens()
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

        /// <summary>
        /// Initializes UI control references after the window loads
        /// </summary>
        private void InitializeControlReferences()
        {
            // Find and store references to UI controls
            TokenTypeComboBox = this.FindName("TokenTypeComboBox") as ComboBox;
            TrackTokensCheckBox = this.FindName("TrackTokensCheckBox") as CheckBox;
            ShowTokenLabelsCheckBox = this.FindName("ShowTokenLabelsCheckBox") as CheckBox;
            TokenSizeThresholdSlider = this.FindName("TokenSizeThresholdSlider") as Slider;
            TokenOverlayCanvas = this.FindName("TokenOverlayCanvas") as Canvas;

            // Find menu items
            ShowROIMenuItem = this.FindName("ShowROIMenuItem") as MenuItem;
            AngledViewMenuItem = this.FindName("AngledViewMenuItem") as MenuItem;
            ShowDepthContoursMenuItem = this.FindName("ShowDepthContoursMenuItem") as MenuItem;
            TrackTokensMenuItem = this.FindName("TrackTokensMenuItem") as MenuItem;
            ShowTokenLabelsMenuItem = this.FindName("ShowTokenLabelsMenuItem") as MenuItem;

            // If TokenTypeComboBox isn't found in XAML, create it programmatically
            if (TokenTypeComboBox == null)
            {
                InitializeTokenTypeComboBox();
            }

            // Set initial values for checkboxes
            if (TrackTokensCheckBox != null)
                TrackTokensCheckBox.IsChecked = trackTokens;

            if (ShowTokenLabelsCheckBox != null)
                ShowTokenLabelsCheckBox.IsChecked = showTokenLabels;

            if (TokenSizeThresholdSlider != null)
                TokenSizeThresholdSlider.Value = tokenDetectionThreshold;

            // Set angled view checkbox
            if (AngledViewCheckBox != null)
                AngledViewCheckBox.IsChecked = isAngledView;

            // Set show ROI checkbox
            if (ShowROICheckBox != null)
                ShowROICheckBox.IsChecked = showROIOverlay;

            // Set menu item states to match current settings
            if (ShowROIMenuItem != null)
                ShowROIMenuItem.IsChecked = showROIOverlay;

            if (AngledViewMenuItem != null)
                AngledViewMenuItem.IsChecked = isAngledView;

            if (ShowDepthContoursMenuItem != null)
                ShowDepthContoursMenuItem.IsChecked = showDepthContours;

            if (TrackTokensMenuItem != null)
                TrackTokensMenuItem.IsChecked = trackTokens;

            if (ShowTokenLabelsMenuItem != null)
                ShowTokenLabelsMenuItem.IsChecked = showTokenLabels;

            // Update status displays
            TableDepthText = $"{tableDepth} mm" + (tableDepthLocked ? " (locked)" : "");
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

        // Helper method to draw a rectangle on the depth image
        private void DrawRectangle(byte[] pixels, int width, int height, int x, int y, int rectWidth, int rectHeight, byte r, byte g, byte b)
        {
            // Parameters for line thickness
            int thickness = 2;

            // Draw top horizontal line
            for (int i = 0; i < thickness; i++)
            {
                DrawLine(pixels, width, height, x, y + i, x + rectWidth, y + i, r, g, b);
            }

            // Draw bottom horizontal line
            for (int i = 0; i < thickness; i++)
            {
                DrawLine(pixels, width, height, x, y + rectHeight - i, x + rectWidth, y + rectHeight - i, r, g, b);
            }

            // Draw left vertical line
            for (int i = 0; i < thickness; i++)
            {
                DrawLine(pixels, width, height, x + i, y, x + i, y + rectHeight, r, g, b);
            }

            // Draw right vertical line
            for (int i = 0; i < thickness; i++)
            {
                DrawLine(pixels, width, height, x + rectWidth - i, y, x + rectWidth - i, y + rectHeight, r, g, b);
            }
        }

        // Helper method to draw a line on the depth image using Bresenham's line algorithm
        private void DrawLine(byte[] pixels, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            // Bresenham's line algorithm
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                // Check if point is in bounds
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                {
                    // Draw pixel
                    int index = (y0 * width + x0) * 4;
                    pixels[index] = b;     // B
                    pixels[index + 1] = g; // G
                    pixels[index + 2] = r; // R
                    // Don't change alpha
                }

                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private void LockTable_Click(object sender, RoutedEventArgs e)
        {
            if (tableDepth > 0)
            {
                tableDepthLocked = true;
                StatusText = $"Table depth locked at {tableDepth} mm";

                // Auto-save this setting
                AutoSaveSettings("Table Depth Lock");
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

            // Auto-save this setting
            AutoSaveSettings("Table Depth Lock");
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
            depthThreshold = isAngledView ? 50 : 30;  // Increase threshold for angled views

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
            depthThreshold = isAngledView ? 50 : 30;

            StatusText = isAngledView ?
                "Angled view mode enabled - using adaptive surface detection" :
                "Direct overhead view mode - using tighter thresholds";

            // Auto-save this setting
            AutoSaveSettings("View Mode");
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

        private void ShowROI_Changed(object sender, RoutedEventArgs e)
        {
            showROIOverlay = ShowROICheckBox.IsChecked ?? true;
            StatusText = showROIOverlay ?
                "ROI overlay enabled" :
                "ROI overlay disabled";

            // Auto-save this setting
            AutoSaveSettings("ROI Overlay");
        }

        private void ExtractROI_Click(object sender, RoutedEventArgs e)
        {
            // Extract ROI data in a format that can be used by the main application
            if (detectedTableROI.Width > 0 && detectedTableROI.Height > 0)
            {
                // Format the ROI data as a JSON-like string
                string roiData = $@"{{
    ""X"": {(int)detectedTableROI.X},
    ""Y"": {(int)detectedTableROI.Y},
    ""Width"": {(int)detectedTableROI.Width},
    ""Height"": {(int)detectedTableROI.Height},
    ""TableDepth"": {tableDepth},
    ""DepthThreshold"": {depthThreshold}
}}";

                // Show dialog with data
                MessageBox.Show(roiData, "ROI Data", MessageBoxButton.OK, MessageBoxImage.Information);

                // Optional: Copy to clipboard for easy pasting into the main application
                try
                {
                    Clipboard.SetText(roiData);
                    StatusText = "ROI data copied to clipboard";
                }
                catch (Exception ex)
                {
                    StatusText = $"Failed to copy to clipboard: {ex.Message}";
                }
            }
            else
            {
                MessageBox.Show("No valid ROI detected yet. Please detect a table surface first.",
                               "ROI Data", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Add these event handlers for mouse interactions
        private void Image_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Start ROI selection
            Image image = sender as Image;
            if (image != null)
            {
                // Get the position relative to the image
                startPoint = e.GetPosition(image);
                isDragging = true;

                /*
                // Determine which canvas to use based on source
                if (image == ColorImage)
                {
                    currentCanvas = ColorROICanvas;
                }
                else if (image == DepthImage)
                {
                    currentCanvas = DepthROICanvas;
                }
                */

                // Clear existing selection rectangle
                if (currentCanvas is Canvas canvas)
                {
                    canvas.Children.Clear();

                    // Create a new rectangle
                    selectionRectangle = new Rectangle
                    {
                        Stroke = new SolidColorBrush(Colors.Yellow),
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 4, 2 }
                    };

                    Canvas.SetLeft(selectionRectangle, startPoint.X);
                    Canvas.SetTop(selectionRectangle, startPoint.Y);
                    canvas.Children.Add(selectionRectangle);
                }

                // Capture mouse to ensure we get mouse move events
                image.CaptureMouse();

                StatusText = $"Creating ROI - Click and drag to define region";
            }
        }

        private void Image_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (isDragging && selectionRectangle != null && currentCanvas is Canvas canvas)
            {
                Image image = sender as Image;
                if (image != null)
                {
                    // Get current position
                    Point currentPoint = e.GetPosition(image);

                    // Calculate rectangle dimensions
                    double x = Math.Min(startPoint.X, currentPoint.X);
                    double y = Math.Min(startPoint.Y, currentPoint.Y);
                    double width = Math.Abs(currentPoint.X - startPoint.X);
                    double height = Math.Abs(currentPoint.Y - startPoint.Y);

                    // Update rectangle position and size
                    Canvas.SetLeft(selectionRectangle, x);
                    Canvas.SetTop(selectionRectangle, y);
                    selectionRectangle.Width = width;
                    selectionRectangle.Height = height;

                    // Update status
                    StatusText = $"ROI: ({x:F0},{y:F0}) to ({x + width:F0},{y + height:F0})";
                }
            }
        }

        private void Image_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (isDragging && selectionRectangle != null)
            {
                Image image = sender as Image;
                if (image != null)
                {
                    // Release mouse capture
                    image.ReleaseMouseCapture();

                    Point currentPoint = e.GetPosition(image);

                    // Calculate final rectangle dimensions
                    double x = Math.Min(startPoint.X, currentPoint.X);
                    double y = Math.Min(startPoint.Y, currentPoint.Y);
                    double width = Math.Abs(currentPoint.X - startPoint.X);
                    double height = Math.Abs(currentPoint.Y - startPoint.Y);

                    // Only proceed if we have a valid sized rectangle
                    if (width > 10 && height > 10)
                    {
                        // Convert from UI coordinates to image coordinates
                        double scaleX, scaleY;

                        if (image == ColorImage)
                        {
                            scaleX = colorWidth / image.ActualWidth;
                            scaleY = colorHeight / image.ActualHeight;
                        }
                        else // DepthImage
                        {
                            scaleX = depthWidth / image.ActualWidth;
                            scaleY = depthHeight / image.ActualHeight;
                        }

                        // Create the ROI in image coordinates
                        detectedTableROI = new Rect(
                            x * scaleX,
                            y * scaleY,
                            width * scaleX,
                            height * scaleY);

                        // If ROI is from depth image, analyze depth data within ROI
                        if (image == DepthImage && depthData != null)
                        {
                            AnalyzeROIDepth();
                        }

                        StatusText = $"ROI set: {detectedTableROI.X:F0},{detectedTableROI.Y:F0} " +
                                     $"({detectedTableROI.Width:F0}x{detectedTableROI.Height:F0})";
                    }
                    else
                    {
                        StatusText = "ROI too small - please try again with a larger selection";

                        // Clear the small selection
                        if (currentCanvas is Canvas canvas)
                        {
                            canvas.Children.Clear();
                        }
                    }

                    // Reset dragging state
                    isDragging = false;
                    selectionRectangle = null;
                    currentCanvas = null;
                }
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

        /// <summary>
        /// Saves current application settings to a JSON file
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                // Create settings object
                var settings = new
                {
                    // Table detection settings
                    TableDetection = new
                    {
                        DepthThreshold = depthThreshold,
                        IsAngledView = isAngledView,
                        ShowDepthContours = showDepthContours,
                        ShowROIOverlay = showROIOverlay,
                        MaxHistorySize = maxHistorySize
                    },

                    // Token tracking settings
                    TokenTracking = new
                    {
                        MinTokenHeight = MIN_TOKEN_HEIGHT,
                        MaxTokenHeight = MAX_TOKEN_HEIGHT,
                        TokenDetectionThreshold = tokenDetectionThreshold,
                        TrackTokens = trackTokens,
                        ShowTokenLabels = showTokenLabels,
                        TokenUpdateIntervalMs = tokenUpdateInterval.TotalMilliseconds
                    },

                    // Last detected ROI
                    TableROI = new
                    {
                        X = (int)detectedTableROI.X,
                        Y = (int)detectedTableROI.Y,
                        Width = (int)detectedTableROI.Width,
                        Height = (int)detectedTableROI.Height,
                        IsValid = detectedTableROI.Width > 0 && detectedTableROI.Height > 0
                    },

                    // Last known table depth
                    TableDepth = tableDepth,
                    TableDepthLocked = tableDepthLocked,

                    // Version info for backward compatibility
                    Version = "1.0",
                    LastSaved = DateTime.Now
                };

                // Serialize to JSON
                string json = System.Text.Json.JsonSerializer.Serialize(settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                // Write to file
                File.WriteAllText(settingsFilePath, json);

                StatusText = $"Settings saved to {settingsFilePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving settings: {ex.Message}";
            }
        }

        /// <summary>
        /// Loads application settings from a JSON file
        /// </summary>
        private void LoadSettings()
        {
            try
            {
                // Check if settings file exists
                if (!File.Exists(settingsFilePath))
                {
                    StatusText = "No saved settings found. Using defaults.";
                    return;
                }

                // Read settings file
                string json = File.ReadAllText(settingsFilePath);

                // Parse JSON
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    // Table detection settings
                    if (root.TryGetProperty("TableDetection", out var tableDetection))
                    {
                        if (tableDetection.TryGetProperty("DepthThreshold", out var val))
                            depthThreshold = val.GetInt32();

                        if (tableDetection.TryGetProperty("IsAngledView", out val))
                            isAngledView = val.GetBoolean();

                        if (tableDetection.TryGetProperty("ShowDepthContours", out val))
                            showDepthContours = val.GetBoolean();

                        if (tableDetection.TryGetProperty("ShowROIOverlay", out val))
                            showROIOverlay = val.GetBoolean();

                        if (tableDetection.TryGetProperty("MaxHistorySize", out val))
                            maxHistorySize = val.GetInt32();
                    }

                    // Token tracking settings
                    if (root.TryGetProperty("TokenTracking", out var tokenTracking))
                    {
                        if (tokenTracking.TryGetProperty("MinTokenHeight", out var val))
                            MIN_TOKEN_HEIGHT = val.GetInt32();

                        if (tokenTracking.TryGetProperty("MaxTokenHeight", out val))
                            MAX_TOKEN_HEIGHT = val.GetInt32();

                        if (tokenTracking.TryGetProperty("TokenDetectionThreshold", out val))
                            tokenDetectionThreshold = val.GetInt32();

                        if (tokenTracking.TryGetProperty("TrackTokens", out val))
                            trackTokens = val.GetBoolean();

                        if (tokenTracking.TryGetProperty("ShowTokenLabels", out val))
                            showTokenLabels = val.GetBoolean();

                        if (tokenTracking.TryGetProperty("TokenUpdateIntervalMs", out val))
                            tokenUpdateInterval = TimeSpan.FromMilliseconds(val.GetDouble());
                    }

                    // ROI settings
                    if (root.TryGetProperty("TableROI", out var tableROI))
                    {
                        if (tableROI.TryGetProperty("IsValid", out var isValid) && isValid.GetBoolean())
                        {
                            int x = 0, y = 0, width = 0, height = 0;

                            if (tableROI.TryGetProperty("X", out var val))
                                x = val.GetInt32();

                            if (tableROI.TryGetProperty("Y", out val))
                                y = val.GetInt32();

                            if (tableROI.TryGetProperty("Width", out val))
                                width = val.GetInt32();

                            if (tableROI.TryGetProperty("Height", out val))
                                height = val.GetInt32();

                            detectedTableROI = new Rect(x, y, width, height);
                        }
                    }

                    // Table depth settings
                    if (root.TryGetProperty("TableDepth", out var tableDepthProp))
                        tableDepth = (ushort)tableDepthProp.GetInt32();

                    if (root.TryGetProperty("TableDepthLocked", out var tableDepthLockedProp))
                        tableDepthLocked = tableDepthLockedProp.GetBoolean();
                }

                // Update UI controls with loaded settings
                this.Dispatcher.Invoke(() => {
                    if (TrackTokensCheckBox != null)
                        TrackTokensCheckBox.IsChecked = trackTokens;

                    if (ShowTokenLabelsCheckBox != null)
                        ShowTokenLabelsCheckBox.IsChecked = showTokenLabels;

                    if (TokenSizeThresholdSlider != null)
                        TokenSizeThresholdSlider.Value = tokenDetectionThreshold;

                    if (AngledViewCheckBox != null)
                        AngledViewCheckBox.IsChecked = isAngledView;

                    if (ShowROICheckBox != null)
                        ShowROICheckBox.IsChecked = showROIOverlay;

                    // Update status displays
                    TableDepthText = $"{tableDepth} mm" + (tableDepthLocked ? " (locked)" : "");
                });

                StatusText = "Settings loaded successfully";
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading settings: {ex.Message}";
            }
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}