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
        // Kinect v2 sensor field of view (in degrees)
        private const double KINECT_VERTICAL_FOV = 60.0;
        private const double KINECT_HORIZONTAL_FOV = 70.6;

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
        // Color detection configurations
        private bool enableColorDetection = false;
        private Dictionary<TokenColorCategory, ActorTypeMapping> colorToActorMappings = new Dictionary<TokenColorCategory, ActorTypeMapping>();

        // Enumeration for detected token colors
        public enum TokenColorCategory
        {
            Red,
            Green,
            Blue,
            Yellow,
            White,
            Black,
            Purple,
            Orange,
            Brown,
            Gray,
            Cyan,
            Pink,
            Unknown
        }

        // Mapping class for token colors to actor types
        public class ActorTypeMapping
        {
            public string ActorType { get; set; } = "unknown";
            public string DisplayName { get; set; } = "Unknown";
            public Color DisplayColor { get; set; } = Colors.Gray;
            public string FoundryActorType { get; set; } = "npc"; // "npc", "character", "vehicle"
            public bool IsHostile { get; set; } = false;
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
            // Clean up height grid resources
            if (HeightGridCanvas != null)
            {
                HeightGridCanvas.Children.Clear();
            }
            // Clear the entire sensor
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

        /// <summary>
        /// Initializes color detection with default mappings
        /// </summary>
        private void InitializeColorDetection()
        {
            // Set up default color mappings
            colorToActorMappings[TokenColorCategory.Red] = new ActorTypeMapping
            {
                ActorType = "monster",
                DisplayName = "Monster/Enemy",
                DisplayColor = Colors.DarkRed,
                FoundryActorType = "npc",
                IsHostile = true
            };

            colorToActorMappings[TokenColorCategory.Green] = new ActorTypeMapping
            {
                ActorType = "ally",
                DisplayName = "Ally/Friendly NPC",
                DisplayColor = Colors.ForestGreen,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Blue] = new ActorTypeMapping
            {
                ActorType = "player",
                DisplayName = "Player Character",
                DisplayColor = Colors.RoyalBlue,
                FoundryActorType = "character",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Yellow] = new ActorTypeMapping
            {
                ActorType = "neutral",
                DisplayName = "Neutral NPC",
                DisplayColor = Colors.Gold,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Purple] = new ActorTypeMapping
            {
                ActorType = "boss",
                DisplayName = "Boss/Elite Enemy",
                DisplayColor = Colors.DarkMagenta,
                FoundryActorType = "npc",
                IsHostile = true
            };

            colorToActorMappings[TokenColorCategory.Orange] = new ActorTypeMapping
            {
                ActorType = "hazard",
                DisplayName = "Hazard/Trap",
                DisplayColor = Colors.OrangeRed,
                FoundryActorType = "npc",
                IsHostile = true
            };

            colorToActorMappings[TokenColorCategory.Brown] = new ActorTypeMapping
            {
                ActorType = "object",
                DisplayName = "Object/Prop",
                DisplayColor = Colors.SaddleBrown,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Black] = new ActorTypeMapping
            {
                ActorType = "shadow",
                DisplayName = "Shadow/Undead",
                DisplayColor = Colors.Black,
                FoundryActorType = "npc",
                IsHostile = true
            };

            colorToActorMappings[TokenColorCategory.White] = new ActorTypeMapping
            {
                ActorType = "divine",
                DisplayName = "Divine/Celestial",
                DisplayColor = Colors.White,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Cyan] = new ActorTypeMapping
            {
                ActorType = "magical",
                DisplayName = "Magical Object",
                DisplayColor = Colors.Cyan,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Gray] = new ActorTypeMapping
            {
                ActorType = "construct",
                DisplayName = "Construct/Golem",
                DisplayColor = Colors.Gray,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Pink] = new ActorTypeMapping
            {
                ActorType = "familiar",
                DisplayName = "Familiar/Pet",
                DisplayColor = Colors.HotPink,
                FoundryActorType = "npc",
                IsHostile = false
            };

            colorToActorMappings[TokenColorCategory.Unknown] = new ActorTypeMapping
            {
                ActorType = "unknown",
                DisplayName = "Unknown",
                DisplayColor = Colors.Gray,
                FoundryActorType = "npc",
                IsHostile = false
            };
        }

        /// <summary>
        /// Analyzes color from the color frame for a specific token
        /// </summary>
        private TokenColorCategory DetectTokenColor(TTRPGToken token)
        {
            // Ensure we have valid color data
            if (colorData == null || colorData.Length == 0 || token.Points.Count == 0)
                return TokenColorCategory.Unknown;

            // Collect RGB values from all points in the token
            int totalR = 0;
            int totalG = 0;
            int totalB = 0;
            int sampleCount = 0;

            foreach (Point point in token.Points)
            {
                int x = (int)point.X;
                int y = (int)point.Y;

                // Convert from depth space to color space (simplified)
                int colorX = (int)(x * (double)colorWidth / depthWidth);
                int colorY = (int)(y * (double)colorHeight / depthHeight);

                // Check bounds
                if (colorX < 0 || colorX >= colorWidth || colorY < 0 || colorY >= colorHeight)
                    continue;

                // Get color data (BGRA format)
                int colorIndex = (colorY * colorWidth + colorX) * 4;
                if (colorIndex + 2 < colorData.Length)
                {
                    byte b = colorData[colorIndex];
                    byte g = colorData[colorIndex + 1];
                    byte r = colorData[colorIndex + 2];

                    totalB += b;
                    totalG += g;
                    totalR += r;
                    sampleCount++;
                }
            }

            // Calculate average color
            if (sampleCount == 0)
                return TokenColorCategory.Unknown;

            int avgR = totalR / sampleCount;
            int avgG = totalG / sampleCount;
            int avgB = totalB / sampleCount;

            // Store the detected color with the token
            token.Color = Color.FromRgb((byte)avgR, (byte)avgG, (byte)avgB);

            // Determine dominant color category
            return CategorizeDominantColor(avgR, avgG, avgB);
        }

        /// <summary>
        /// Maps RGB values to a color category
        /// </summary>
        private TokenColorCategory CategorizeDominantColor(int r, int g, int b)
        {
            // Calculate brightness and saturation
            int max = Math.Max(Math.Max(r, g), b);
            int min = Math.Min(Math.Min(r, g), b);
            int delta = max - min;

            double brightness = max / 255.0;
            double saturation = max == 0 ? 0 : delta / (double)max;

            // Determine black, white, or gray based on brightness and saturation
            if (brightness < 0.2)
                return TokenColorCategory.Black;

            if (brightness > 0.9 && saturation < 0.1)
                return TokenColorCategory.White;

            if (saturation < 0.15)
                return TokenColorCategory.Gray;

            // Determine hue-based colors
            // Red dominance
            if (r > g + 50 && r > b + 50)
                return TokenColorCategory.Red;

            // Green dominance
            if (g > r + 50 && g > b + 50)
                return TokenColorCategory.Green;

            // Blue dominance
            if (b > r + 50 && b > g + 50)
                return TokenColorCategory.Blue;

            // Yellow (red + green)
            if (r > b + 50 && g > b + 50)
                return TokenColorCategory.Yellow;

            // Purple (red + blue)
            if (r > g + 30 && b > g + 30)
                return TokenColorCategory.Purple;

            // Cyan (green + blue)
            if (g > r + 30 && b > r + 30)
                return TokenColorCategory.Cyan;

            // Orange (high red, medium green)
            if (r > g + 50 && g > b + 20 && r > 200)
                return TokenColorCategory.Orange;

            // Brown (medium red, low green, very low blue)
            if (r > g && r < 200 && g < 150 && b < 100)
                return TokenColorCategory.Brown;

            // Pink (high red, medium blue)
            if (r > g + 50 && r > b && b > g)
                return TokenColorCategory.Pink;

            // Default
            return TokenColorCategory.Unknown;
        }

        /// <summary>
        /// Enhanced token detection that also identifies color
        /// </summary>
        private void DetectTokensWithColor()
        {
            // First detect tokens using the normal algorithm
            DetectTokensEnhanced();

            // If color detection is enabled, analyze and assign colors
            if (enableColorDetection && detectedTokens.Count > 0)
            {
                foreach (var token in detectedTokens)
                {
                    // Get token color
                    TokenColorCategory colorCategory = DetectTokenColor(token);

                    // Add the color category to token's actor mapping
                    token.ActorCategory = colorCategory.ToString();

                    // Get the actor type mapping
                    if (colorToActorMappings.TryGetValue(colorCategory, out var mapping))
                    {
                        token.ActorType = mapping.ActorType;
                        token.IsHostile = mapping.IsHostile;
                    }
                }

                // Update the WebSocket message format to include color and actor information
                // (This is handled in the CreateTokenUpdateData method)
            }
        }

        /// <summary>
        /// Shows the color mapping configuration dialog
        /// </summary>
        private void ShowColorMappingDialog()
        {
            // Initialize mappings if needed
            if (colorToActorMappings.Count == 0)
            {
                InitializeColorDetection();
            }

            // Create dialog window
            var window = new Window
            {
                Title = "Token Color Mapping",
                Width = 600,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create the main layout
            var mainPanel = new DockPanel();

            // Create header
            var headerPanel = new StackPanel
            {
                Margin = new Thickness(10)
            };

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Color to Actor Type Mapping",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Configure how token colors are mapped to actor types in Foundry VTT.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            });

            // Add toggle for color detection
            var enableColorCheckbox = new CheckBox
            {
                Content = "Enable Color Detection",
                IsChecked = enableColorDetection,
                Margin = new Thickness(0, 10, 0, 10)
            };

            enableColorCheckbox.Checked += (s, e) => enableColorDetection = true;
            enableColorCheckbox.Unchecked += (s, e) => enableColorDetection = false;

            headerPanel.Children.Add(enableColorCheckbox);

            DockPanel.SetDock(headerPanel, Dock.Top);
            mainPanel.Children.Add(headerPanel);

            // Create scrollable content
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(10, 0, 10, 10)
            };

            var mappingsPanel = new StackPanel();

            // Create a header row
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            // Add headers
            headerRow.Children.Add(CreateHeaderTextBlock("Color", 0));
            headerRow.Children.Add(CreateHeaderTextBlock("Display Name", 1));
            headerRow.Children.Add(CreateHeaderTextBlock("Actor Type", 2));
            headerRow.Children.Add(CreateHeaderTextBlock("Is Hostile", 3));

            mappingsPanel.Children.Add(headerRow);

            // Separator
            mappingsPanel.Children.Add(new Separator
            {
                Margin = new Thickness(0, 5, 0, 10)
            });

            // Create a row for each color mapping
            foreach (var colorMapping in colorToActorMappings)
            {
                var colorCategory = colorMapping.Key;
                var mapping = colorMapping.Value;

                var row = new Grid
                {
                    Margin = new Thickness(0, 5, 0, 5)
                };

                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

                // Color indicator
                var colorPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var colorRect = new System.Windows.Shapes.Rectangle
                {
                    Width = 20,
                    Height = 20,
                    Fill = new SolidColorBrush(mapping.DisplayColor),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Margin = new Thickness(0, 0, 5, 0)
                };

                colorPanel.Children.Add(colorRect);
                colorPanel.Children.Add(new TextBlock
                {
                    Text = colorCategory.ToString(),
                    VerticalAlignment = VerticalAlignment.Center
                });

                Grid.SetColumn(colorPanel, 0);
                row.Children.Add(colorPanel);

                // Display name textbox
                var displayNameTextBox = new TextBox
                {
                    Text = mapping.DisplayName,
                    Margin = new Thickness(5, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                displayNameTextBox.TextChanged += (s, e) =>
                {
                    mapping.DisplayName = displayNameTextBox.Text;
                };

                Grid.SetColumn(displayNameTextBox, 1);
                row.Children.Add(displayNameTextBox);

                // Actor type combobox
                var actorTypeComboBox = new ComboBox
                {
                    Margin = new Thickness(5, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 120
                };

                actorTypeComboBox.Items.Add("npc");
                actorTypeComboBox.Items.Add("character");
                actorTypeComboBox.Items.Add("vehicle");
                actorTypeComboBox.SelectedItem = mapping.FoundryActorType;

                actorTypeComboBox.SelectionChanged += (s, e) =>
                {
                    mapping.FoundryActorType = actorTypeComboBox.SelectedItem.ToString();
                };

                Grid.SetColumn(actorTypeComboBox, 2);
                row.Children.Add(actorTypeComboBox);

                // Is hostile checkbox
                var hostileCheckBox = new CheckBox
                {
                    IsChecked = mapping.IsHostile,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                hostileCheckBox.Checked += (s, e) => mapping.IsHostile = true;
                hostileCheckBox.Unchecked += (s, e) => mapping.IsHostile = false;

                Grid.SetColumn(hostileCheckBox, 3);
                row.Children.Add(hostileCheckBox);

                mappingsPanel.Children.Add(row);
            }

            scrollViewer.Content = mappingsPanel;
            mainPanel.Children.Add(scrollViewer);

            // Add buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10)
            };

            var saveButton = new Button
            {
                Content = "Save",
                Padding = new Thickness(20, 5, 20, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            saveButton.Click += (s, e) =>
            {
                // Save to settings
                SaveSettings();
                window.Close();
                StatusText = "Color mappings saved";
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 5, 20, 5)
            };

            cancelButton.Click += (s, e) => window.Close();

            var resetButton = new Button
            {
                Content = "Reset to Defaults",
                Padding = new Thickness(20, 5, 20, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            resetButton.Click += (s, e) =>
            {
                // Reset to defaults
                colorToActorMappings.Clear();
                InitializeColorDetection();
                window.Close();
                ShowColorMappingDialog(); // Reopen with defaults
            };

            buttonPanel.Children.Add(resetButton);
            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);

            DockPanel.SetDock(buttonPanel, Dock.Bottom);
            mainPanel.Children.Add(buttonPanel);

            // Set content and show
            window.Content = mainPanel;
            window.ShowDialog();
        }

        /// <summary>
        /// Helper to create header text blocks for the mapping table
        /// </summary>
        private TextBlock CreateHeaderTextBlock(string text, int column)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(textBlock, column);
            return textBlock;
        }

        /// <summary>
        /// Updates the CreateTokenUpdateData method to include color and actor information
        /// </summary>
        private string CreateTokenUpdateDataWithColor()
        {
            // Only send updates if we have a valid detection setup
            if (!IsReadyForTokenDetection() || detectedTokens.Count == 0)
            {
                // Create a status-only update when no tokens are available
                var statusUpdate = new
                {
                    type = "statusUpdate",
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    status = hasValidROI && hasValidTableDepth ? "ready" : "notReady",
                    message = !hasValidROI ? "Define ROI on depth image" :
                             !hasValidTableDepth ? "Table depth not detected" : "Ready",
                    tokenCount = 0
                };

                return System.Text.Json.JsonSerializer.Serialize(statusUpdate);
            }

            // Create token data in Foundry VTT compatible format with improved metadata
            var tokenUpdate = new
            {
                type = "tokenUpdate",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                tableDepth = tableDepth,
                status = "active",
                colorDetectionEnabled = enableColorDetection,
                tokens = detectedTokens.Select(t => new
                {
                    id = t.Id.ToString(),
                    name = !string.IsNullOrEmpty(t.Label) ? t.Label : GetTokenTypeString(t.Type),
                    // Convert to Foundry grid units - assuming 1 grid = 1 inch and using meters as our base unit
                    x = t.RealWorldPosition.X * 39.37, // Convert meters to inches
                    y = t.RealWorldPosition.Y * 39.37, // Convert meters to inches
                    elevation = 0,
                    height = GetSizeForFoundry(t),
                    width = GetSizeForFoundry(t),
                    type = t.Type.ToString(),
                    // Additional metadata
                    heightMm = t.HeightMm,
                    diameterMm = t.DiameterMeters * 1000, // Convert meters to mm
                    // Include properties for visualization in Foundry
                    isHumanoid = t.Type == TokenType.Miniature,
                    tokenColor = GetTokenHexColor(t),
                    // Include detected color and actor information
                    detectedColor = enableColorDetection ? t.ActorCategory : "Unknown",
                    actorType = enableColorDetection ? t.ActorType : "unknown",
                    foundryActorType = enableColorDetection && colorToActorMappings.TryGetValue(
                        Enum.TryParse<TokenColorCategory>(t.ActorCategory, out var category) ?
                            category : TokenColorCategory.Unknown,
                        out var mapping) ?
                            mapping.FoundryActorType : "npc",
                    isHostile = enableColorDetection ? t.IsHostile : false,
                    colorHex = enableColorDetection ?
                        $"#{t.Color.R:X2}{t.Color.G:X2}{t.Color.B:X2}" : "#CCCCCC"
                }).ToArray()
            };

            return System.Text.Json.JsonSerializer.Serialize(tokenUpdate);
        }
    }

}
    
