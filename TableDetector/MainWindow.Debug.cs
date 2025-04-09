using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TableDetector
{
    public partial class MainWindow
    {
        private bool showHeightProfileDebugging = false;
        private Window debugWindow = null;
        private Canvas heightProfileCanvas = null;
        private ScrollViewer debugScrollViewer = null;

        /// <summary>
        /// Shows or hides the height profile debugging window
        /// </summary>
        private void ToggleHeightProfileDebugging()
        {
            showHeightProfileDebugging = !showHeightProfileDebugging;

            if (showHeightProfileDebugging)
            {
                ShowHeightProfileDebugWindow();
            }
            else if (debugWindow != null)
            {
                debugWindow.Close();
                debugWindow = null;
            }

            StatusText = showHeightProfileDebugging ?
                "Height profile debugging enabled" :
                "Height profile debugging disabled";
        }

        /// <summary>
        /// Creates and shows the height profile debug window
        /// </summary>
        private void ShowHeightProfileDebugWindow()
        {
            if (debugWindow != null)
                return;

            // Create debug window
            debugWindow = new Window
            {
                Title = "Token Height Profile Debugging",
                Width = 800,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create main layout
            var mainPanel = new DockPanel();

            // Create control panel at top
            var controlPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10),
                Height = 40
            };

            var refreshButton = new Button
            {
                Content = "Refresh",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };
            refreshButton.Click += (s, e) => UpdateHeightProfileVisualizations();
            controlPanel.Children.Add(refreshButton);

            var showXYButton = new Button
            {
                Content = "Show Top View",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };
            showXYButton.Click += (s, e) => SwitchDebugViewMode("TopView");
            controlPanel.Children.Add(showXYButton);

            var showXZButton = new Button
            {
                Content = "Show Side View",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };
            showXZButton.Click += (s, e) => SwitchDebugViewMode("SideView");
            controlPanel.Children.Add(showXZButton);

            var show3DButton = new Button
            {
                Content = "Show 3D View",
                Padding = new Thickness(10, 5, 10, 5)
            };
            show3DButton.Click += (s, e) => SwitchDebugViewMode("3DView");
            controlPanel.Children.Add(show3DButton);

            DockPanel.SetDock(controlPanel, Dock.Top);
            mainPanel.Children.Add(controlPanel);

            // Create debug info panel
            var infoPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(10, 0, 10, 10),
                Width = 200,
                Background = new SolidColorBrush(Color.FromRgb(240, 240, 240))
            };

            infoPanel.Children.Add(new TextBlock
            {
                Text = "Debug Information",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            });

            var tokenInfoText = new TextBlock
            {
                Text = "No token selected",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(5)
            };
            infoPanel.Children.Add(tokenInfoText);

            DockPanel.SetDock(infoPanel, Dock.Right);
            mainPanel.Children.Add(infoPanel);

            // Create canvas for visualizations
            debugScrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            heightProfileCanvas = new Canvas
            {
                Background = new SolidColorBrush(Colors.Black),
                Width = 1000,
                Height = 800
            };

            debugScrollViewer.Content = heightProfileCanvas;
            mainPanel.Children.Add(debugScrollViewer);

            // Set content and show
            debugWindow.Content = mainPanel;
            debugWindow.Closed += (s, e) =>
            {
                debugWindow = null;
                showHeightProfileDebugging = false;
            };

            debugWindow.Show();

            // Update with current data
            UpdateHeightProfileVisualizations();
        }

        /// <summary>
        /// Changes the debug visualization mode
        /// </summary>
        private void SwitchDebugViewMode(string mode)
        {
            if (heightProfileCanvas == null)
                return;

            // Clear canvas
            heightProfileCanvas.Children.Clear();

            // Update visualizations based on selected mode
            switch (mode)
            {
                case "TopView":
                    DrawTopViewDebug();
                    break;

                case "SideView":
                    DrawSideViewDebug();
                    break;

                case "3DView":
                    Draw3DViewDebug();
                    break;

                default:
                    DrawTopViewDebug();
                    break;
            }
        }

        /// <summary>
        /// Updates the height profile visualizations with current token data
        /// </summary>
        private void UpdateHeightProfileVisualizations()
        {
            if (heightProfileCanvas == null)
                return;

            // Default to top view
            DrawTopViewDebug();
        }

        /// <summary>
        /// Draws top-down (XY) view of tokens with height-based coloring
        /// </summary>
        private void DrawTopViewDebug()
        {
            if (heightProfileCanvas == null || detectedTokens.Count == 0)
                return;

            // Clear canvas
            heightProfileCanvas.Children.Clear();

            // Add title
            var titleText = new TextBlock
            {
                Text = "Top View (XY) - Height Visualization",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10)
            };

            Canvas.SetLeft(titleText, 10);
            Canvas.SetTop(titleText, 10);
            heightProfileCanvas.Children.Add(titleText);

            // Calculate scaling factor for visualization
            double scale = 1.0;
            if (detectedTokens.Count > 0)
            {
                double maxX = detectedTokens.Max(t => t.Position.X);
                double maxY = detectedTokens.Max(t => t.Position.Y);

                // Scale to fit within 80% of canvas
                double scaleX = (heightProfileCanvas.Width * 0.8) / Math.Max(1, maxX);
                double scaleY = (heightProfileCanvas.Height * 0.8) / Math.Max(1, maxY);

                scale = Math.Min(scaleX, scaleY);
            }

            // Draw each token with height visualization
            int tokenIndex = 0;
            foreach (var token in detectedTokens)
            {
                // Calculate position in visualization
                double x = 50 + (token.Position.X * scale);
                double y = 50 + (token.Position.Y * scale);
                double radius = Math.Max(10, token.DiameterPixels * scale / 2);

                // Find max height for color scale
                int maxHeight = Math.Min((ushort)255, token.HeightMm);

                // Draw main token circle with height-based color
                Ellipse tokenCircle = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = new SolidColorBrush(Color.FromRgb(
                        (byte)(255 - Math.Min(255, maxHeight)),
                        (byte)Math.Min(255, maxHeight),
                        50))
                };

                Canvas.SetLeft(tokenCircle, x - radius);
                Canvas.SetTop(tokenCircle, y - radius);
                heightProfileCanvas.Children.Add(tokenCircle);

                // Add height label
                TextBlock heightText = new TextBlock
                {
                    Text = $"{token.HeightMm}mm",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0))
                };

                heightText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(heightText, x - heightText.DesiredSize.Width / 2);
                Canvas.SetTop(heightText, y - heightText.DesiredSize.Height / 2);
                heightProfileCanvas.Children.Add(heightText);

                // Add index number for selection
                TextBlock indexText = new TextBlock
                {
                    Text = tokenIndex.ToString(),
                    Foreground = new SolidColorBrush(Colors.Black),
                    Background = new SolidColorBrush(Colors.Yellow),
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 10
                };

                Canvas.SetLeft(indexText, x + radius - 10);
                Canvas.SetTop(indexText, y - radius - 15);
                heightProfileCanvas.Children.Add(indexText);

                tokenIndex++;
            }

            // Draw legend
            DrawHeightLegend();
        }

        /// <summary>
        /// Draws a side view (XZ) profile of tokens
        /// </summary>
        private void DrawSideViewDebug()
        {
            if (heightProfileCanvas == null || detectedTokens.Count == 0)
                return;

            // Clear canvas
            heightProfileCanvas.Children.Clear();

            // Add title
            var titleText = new TextBlock
            {
                Text = "Side View (XZ) - Height Profile",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10)
            };

            Canvas.SetLeft(titleText, 10);
            Canvas.SetTop(titleText, 10);
            heightProfileCanvas.Children.Add(titleText);

            // Calculate scaling and positioning
            double maxX = detectedTokens.Max(t => t.Position.X);
            double maxHeight = detectedTokens.Max(t => t.HeightMm);

            // Scale to fit within canvas
            double scaleX = (heightProfileCanvas.Width * 0.8) / Math.Max(1, maxX);
            double scaleHeight = (heightProfileCanvas.Height * 0.6) / Math.Max(1, maxHeight);

            // Draw table surface line
            Line tableLine = new Line
            {
                X1 = 50,
                Y1 = 500,
                X2 = 50 + (maxX * scaleX),
                Y2 = 500,
                Stroke = new SolidColorBrush(Colors.Gray),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 }
            };

            heightProfileCanvas.Children.Add(tableLine);

            // Add table surface label
            TextBlock tableText = new TextBlock
            {
                Text = "Table Surface",
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 12
            };

            Canvas.SetLeft(tableText, 50);
            Canvas.SetTop(tableText, 505);
            heightProfileCanvas.Children.Add(tableText);

            // Draw each token as a rectangle from table up to its height
            int tokenIndex = 0;
            foreach (var token in detectedTokens)
            {
                // Calculate position in visualization
                double x = 50 + (token.Position.X * scaleX);
                double y = 500; // Table surface baseline
                double width = Math.Max(20, token.DiameterPixels * scaleX / 2);
                double height = token.HeightMm * scaleHeight;

                // Draw token rectangle
                Rectangle tokenRect = new Rectangle
                {
                    Width = width,
                    Height = height,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = GetTokenTypeColorBrush(token.Type)
                };

                Canvas.SetLeft(tokenRect, x - (width / 2));
                Canvas.SetTop(tokenRect, y - height);
                heightProfileCanvas.Children.Add(tokenRect);

                // Add height label
                TextBlock heightText = new TextBlock
                {
                    Text = $"{token.HeightMm}mm",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    Padding = new Thickness(3)
                };

                Canvas.SetLeft(heightText, x - (width / 2));
                Canvas.SetTop(heightText, y - height - 20);
                heightProfileCanvas.Children.Add(heightText);

                // Add index number for selection
                TextBlock indexText = new TextBlock
                {
                    Text = tokenIndex.ToString(),
                    Foreground = new SolidColorBrush(Colors.Black),
                    Background = new SolidColorBrush(Colors.Yellow),
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 10
                };

                Canvas.SetLeft(indexText, x - (width / 2) - 10);
                Canvas.SetTop(indexText, y - height);
                heightProfileCanvas.Children.Add(indexText);

                tokenIndex++;
            }
        }

        /// <summary>
        /// Draws a simplified 3D view of tokens (isometric projection)
        /// </summary>
        private void Draw3DViewDebug()
        {
            if (heightProfileCanvas == null || detectedTokens.Count == 0)
                return;

            // Clear canvas
            heightProfileCanvas.Children.Clear();

            // Add title
            var titleText = new TextBlock
            {
                Text = "3D View (Isometric Projection)",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(10)
            };

            Canvas.SetLeft(titleText, 10);
            Canvas.SetTop(titleText, 10);
            heightProfileCanvas.Children.Add(titleText);

            // Calculate scaling and positioning
            double maxX = detectedTokens.Max(t => t.Position.X);
            double maxY = detectedTokens.Max(t => t.Position.Y);
            double maxHeight = detectedTokens.Max(t => t.HeightMm);

            // Scale to fit within canvas
            double scaleXY = (heightProfileCanvas.Width * 0.6) / Math.Max(maxX, maxY);
            double scaleHeight = (heightProfileCanvas.Height * 0.5) / Math.Max(1, maxHeight);

            // Define isometric projection angles
            double isoAngleX = Math.PI / 6; // 30 degrees
            double isoAngleY = Math.PI / 6; // 30 degrees

            // Draw isometric grid for reference
            DrawIsometricGrid(400, 400, scaleXY, isoAngleX, isoAngleY);

            // Center point for projection
            double centerX = 400;
            double centerY = 300;

            // Draw each token as a 3D shape
            int tokenIndex = 0;
            foreach (var token in detectedTokens)
            {
                // Calculate base position in 2D before projection
                double x = token.Position.X * scaleXY;
                double y = token.Position.Y * scaleXY;
                double radius = Math.Max(10, token.DiameterPixels * scaleXY / 4);
                double height = token.HeightMm * scaleHeight;

                // Project to isometric view
                double isoX = centerX + (x - y) * Math.Cos(isoAngleX);
                double isoY = centerY + (x + y) * Math.Sin(isoAngleY) - height;

                // Draw token as a cylinder approximation (ellipse + lines)

                // Draw top ellipse
                Ellipse topEllipse = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2,
                    Fill = GetTokenTypeColorBrush(token.Type)
                };

                Canvas.SetLeft(topEllipse, isoX - radius);
                Canvas.SetTop(topEllipse, isoY - radius / 2);
                heightProfileCanvas.Children.Add(topEllipse);

                // Draw bottom ellipse (at table level)
                Ellipse bottomEllipse = new Ellipse
                {
                    Width = radius * 2,
                    Height = radius,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(100,
                            GetTokenTypeColor(token.Type).R,
                            GetTokenTypeColor(token.Type).G,
                            GetTokenTypeColor(token.Type).B))
                };

                Canvas.SetLeft(bottomEllipse, isoX - radius);
                Canvas.SetTop(bottomEllipse, isoY + height - radius / 2);
                heightProfileCanvas.Children.Add(bottomEllipse);

                // Draw side lines
                Line leftLine = new Line
                {
                    X1 = isoX - radius,
                    Y1 = isoY,
                    X2 = isoX - radius,
                    Y2 = isoY + height,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2
                };

                Line rightLine = new Line
                {
                    X1 = isoX + radius,
                    Y1 = isoY,
                    X2 = isoX + radius,
                    Y2 = isoY + height,
                    Stroke = new SolidColorBrush(Colors.White),
                    StrokeThickness = 2
                };

                heightProfileCanvas.Children.Add(leftLine);
                heightProfileCanvas.Children.Add(rightLine);

                // Add height label
                TextBlock heightText = new TextBlock
                {
                    Text = $"{token.HeightMm}mm",
                    Foreground = new SolidColorBrush(Colors.White),
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    Padding = new Thickness(3)
                };

                Canvas.SetLeft(heightText, isoX - heightText.ActualWidth / 2);
                Canvas.SetTop(heightText, isoY - 20);
                heightProfileCanvas.Children.Add(heightText);

                // Add index number for selection
                TextBlock indexText = new TextBlock
                {
                    Text = tokenIndex.ToString(),
                    Foreground = new SolidColorBrush(Colors.Black),
                    Background = new SolidColorBrush(Colors.Yellow),
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 10
                };

                Canvas.SetLeft(indexText, isoX + radius + 5);
                Canvas.SetTop(indexText, isoY - radius / 2);
                heightProfileCanvas.Children.Add(indexText);

                tokenIndex++;
            }
        }

        /// <summary>
        /// Draws an isometric grid for the 3D view
        /// </summary>
        private void DrawIsometricGrid(double centerX, double centerY, double scale, double angleX, double angleY)
        {
            // Draw simplified reference grid
            const int gridSize = 10;
            const int gridSpacing = 50;

            // Add background grid panel
            Rectangle gridPanel = new Rectangle
            {
                Width = gridSize * gridSpacing * Math.Cos(angleX) * 1.5,
                Height = gridSize * gridSpacing * Math.Sin(angleY) * 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(30, 100, 100, 100))
            };

            Canvas.SetLeft(gridPanel, centerX - (gridPanel.Width / 2));
            Canvas.SetTop(gridPanel, centerY - (gridPanel.Height / 5));
            heightProfileCanvas.Children.Add(gridPanel);

            // X gridlines
            for (int i = 0; i <= gridSize; i++)
            {
                double y = i * gridSpacing;

                // Project to isometric
                double startX = centerX + (-gridSize * gridSpacing / 2 - y) * Math.Cos(angleX);
                double startY = centerY + (-gridSize * gridSpacing / 2 + y) * Math.Sin(angleY);

                double endX = centerX + (gridSize * gridSpacing / 2 - y) * Math.Cos(angleX);
                double endY = centerY + (gridSize * gridSpacing / 2 + y) * Math.Sin(angleY);

                Line gridLine = new Line
                {
                    X1 = startX,
                    Y1 = startY,
                    X2 = endX,
                    Y2 = endY,
                    Stroke = new SolidColorBrush(Colors.Gray),
                    StrokeThickness = i == gridSize / 2 ? 2 : 0.5,
                    Opacity = 0.5,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };

                heightProfileCanvas.Children.Add(gridLine);
            }

            // Y gridlines
            for (int i = 0; i <= gridSize; i++)
            {
                double x = i * gridSpacing - (gridSize * gridSpacing / 2);

                // Project to isometric
                double startX = centerX + (x - (-gridSize * gridSpacing / 2)) * Math.Cos(angleX);
                double startY = centerY + (x + (-gridSize * gridSpacing / 2)) * Math.Sin(angleY);

                double endX = centerX + (x - (gridSize * gridSpacing / 2)) * Math.Cos(angleX);
                double endY = centerY + (x + (gridSize * gridSpacing / 2)) * Math.Sin(angleY);

                Line gridLine = new Line
                {
                    X1 = startX,
                    Y1 = startY,
                    X2 = endX,
                    Y2 = endY,
                    Stroke = new SolidColorBrush(Colors.Gray),
                    StrokeThickness = i == gridSize / 2 ? 2 : 0.5,
                    Opacity = 0.5,
                    StrokeDashArray = new DoubleCollection { 4, 4 }
                };

                heightProfileCanvas.Children.Add(gridLine);
            }

            // Add axis labels
            TextBlock xLabel = new TextBlock
            {
                Text = "X",
                Foreground = new SolidColorBrush(Colors.Red),
                FontWeight = FontWeights.Bold
            };

            Canvas.SetLeft(xLabel, centerX + 100);
            Canvas.SetTop(xLabel, centerY + 20);
            heightProfileCanvas.Children.Add(xLabel);

            TextBlock yLabel = new TextBlock
            {
                Text = "Y",
                Foreground = new SolidColorBrush(Colors.Green),
                FontWeight = FontWeights.Bold
            };

            Canvas.SetLeft(yLabel, centerX - 120);
            Canvas.SetTop(yLabel, centerY + 20);
            heightProfileCanvas.Children.Add(yLabel);

            TextBlock zLabel = new TextBlock
            {
                Text = "Z",
                Foreground = new SolidColorBrush(Colors.Blue),
                FontWeight = FontWeights.Bold
            };

            Canvas.SetLeft(zLabel, centerX);
            Canvas.SetTop(zLabel, centerY - 120);
            heightProfileCanvas.Children.Add(zLabel);
        }

        /// <summary>
        /// Draws a color legend for height visualization
        /// </summary>
        private void DrawHeightLegend()
        {
            // Create legend panel
            Rectangle legendPanel = new Rectangle
            {
                Width = 150,
                Height = 200,
                Fill = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                RadiusX = 5,
                RadiusY = 5
            };

            Canvas.SetRight(legendPanel, 20);
            Canvas.SetBottom(legendPanel, 20);
            heightProfileCanvas.Children.Add(legendPanel);

            // Add legend title
            TextBlock titleText = new TextBlock
            {
                Text = "Height Legend",
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };

            Canvas.SetRight(titleText, 75);
            Canvas.SetBottom(titleText, 200);
            heightProfileCanvas.Children.Add(titleText);

            // Create gradient bar
            const int barHeight = 150;

            for (int i = 0; i < barHeight; i++)
            {
                // Calculate color based on height
                byte green = (byte)(i * 255 / barHeight);
                byte red = (byte)(255 - green);

                Line colorLine = new Line
                {
                    X1 = heightProfileCanvas.Width - 125,
                    Y1 = heightProfileCanvas.Height - 40 - i,
                    X2 = heightProfileCanvas.Width - 75,
                    Y2 = heightProfileCanvas.Height - 40 - i,
                    Stroke = new SolidColorBrush(Color.FromRgb(red, green, 50)),
                    StrokeThickness = 1
                };

                heightProfileCanvas.Children.Add(colorLine);
            }

            // Add labels
            TextBlock maxLabel = new TextBlock
            {
                Text = "100+ mm",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 10
            };

            Canvas.SetRight(maxLabel, 50);
            Canvas.SetBottom(maxLabel, 185);
            heightProfileCanvas.Children.Add(maxLabel);

            TextBlock midLabel = new TextBlock
            {
                Text = "50 mm",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 10
            };

            Canvas.SetRight(midLabel, 50);
            Canvas.SetBottom(midLabel, 115);
            heightProfileCanvas.Children.Add(midLabel);

            TextBlock minLabel = new TextBlock
            {
                Text = "0 mm",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 10
            };

            Canvas.SetRight(minLabel, 50);
            Canvas.SetBottom(minLabel, 45);
            heightProfileCanvas.Children.Add(minLabel);
        }

        /// <summary>
        /// Gets a brush for token type colors
        /// </summary>
        private SolidColorBrush GetTokenTypeColorBrush(TokenType type)
        {
            return new SolidColorBrush(GetTokenTypeColor(type));
        }
    }
}