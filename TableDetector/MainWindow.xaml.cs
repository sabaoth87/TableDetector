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
        private int MAX_TOKEN_HEIGHT = 250; // Maximum height of a token in mm
        private int MIN_TOKEN_SIZE = 4; // Minimum size of a token in px
        private int MAX_TOKEN_SIZE = 150; // Maximum size of a token in px
        private int MIN_BASE_SIZE = 4; // Minimum size of a token in px
        private int MAX_BASE_SIZE = 150; // Maximum size of a token in px
        private List<TTRPGToken> detectedTokens = new List<TTRPGToken>();
        private bool trackTokens = true;
        private int tokenDetectionThreshold = 15; // Minimum pixel count to consider as a token
        private bool showTokenLabels = true;
        private DateTime lastTokenUpdateTime = DateTime.MinValue;
        private TimeSpan tokenUpdateInterval = TimeSpan.FromMilliseconds(100); // Update tokens 10 times per second

        //
        private ComboBox TokenTypeComboBox;

        // Property change notification
        public event PropertyChangedEventHandler PropertyChanged;

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

        private void AngledView_Changed(object sender, RoutedEventArgs e)
        {
            isAngledView = AngledViewCheckBox.IsChecked ?? false;

            // Adjust thresholds based on view setting
            depthThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;

            StatusText = isAngledView ?
                "Angled view mode enabled - using adaptive surface detection" :
                "Direct overhead view mode - using tighter thresholds";

            // Auto-save this setting
            AutoSaveSettings("View Mode");
        }

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}