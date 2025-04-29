using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TableDetector
{
    public partial class MainWindow
    {
        #region MENU INTERACTIONS

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
                "© 2025 Sabaoth/Army of Robot",
                "About Table Detector",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        
        #endregion

        #region BUTTON CLICK EVENTS
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
        /// Event handler for the Exit menu item
        /// </summary>
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            // Close the application
            this.Close();
        }

        // User Interacts with the 'Lock Table Depth' button
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

        // User Interacts with the 'Unlock Table Depth' button
        private void UnlockTable_Click(object sender, RoutedEventArgs e)
        {
            tableDepthLocked = false;
            depthHistory.Clear(); // Reset history
            StatusText = "Table depth detection switched to automatic mode";

            // Auto-save this setting
            AutoSaveSettings("Table Depth Lock");
        }

        // User Interacts with the 'Toggle Contours' button
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
            depthThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;  // Increase threshold for angled views

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

        /// <summary>
        /// Click the diagnose button?
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        #endregion

        private void GridCellSize_ChangedOld(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            gridCellSize = (int)e.NewValue;
            InitializeHeightGrid(); // Reinitialize with new size
            StatusText = $"Grid cell size set to {gridCellSize} pixels";
        }

        private void ShowTuningInterface_Click(object sender, RoutedEventArgs e)
        {
            var tuningWindow = new Window
            {
                Title = "Depth & Token Tuning",
                Width = 500,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var panel = new StackPanel { Margin = new Thickness(10) };

            // Add depth threshold slider
            panel.Children.Add(new TextBlock { Text = "Depth Threshold (mm):" });
            var depthThresholdSlider = new Slider
            {
                Minimum = 5,
                Maximum = 50,
                Value = depthThreshold,
                TickFrequency = 5,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true
            };

            depthThresholdSlider.ValueChanged += (s, args) =>
            {
                depthThreshold = (int)args.NewValue;
                // Update status
            };

            panel.Children.Add(depthThresholdSlider);

            // Add more sliders for other parameters...

            // Add a preview section

            tuningWindow.Content = panel;
            tuningWindow.ShowDialog();
        }

        private void ManualROISelect_Click(object sender, RoutedEventArgs e)
        {
            StatusText = "Click and drag on the depth image to manually select table area";
            // Enable mouse event handlers for selection
        }

        private void CalibrateTokenHeight_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Place a token with known height (e.g., 25mm) on the table and click OK");
            // Then detect the token and use it to calibrate height measurements
        }

        #region ROI INTERACTIONS

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
                currentCanvas = DepthROICanvas;

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

        private void ShowROI_Changed(object sender, RoutedEventArgs e)
        {
            showROIOverlay = ShowROICheckBox.IsChecked ?? true;
            StatusText = showROIOverlay ?
                "ROI overlay enabled" :
                "ROI overlay disabled";

            // Auto-save this setting
            AutoSaveSettings("ROI Overlay");
        }


        #endregion

        #region COMPONENT INIT

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
                    Margin = new Thickness(25, 0, 0, 0),
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
            // Initialize height grid UI references
            ShowHeightGridCheckBox = this.FindName("ShowHeightGridCheckBox") as CheckBox;
            ShowHeightGridMenuItem = this.FindName("ShowHeightGridMenuItem") as MenuItem;
            GridCellSizeSlider = this.FindName("GridCellSizeSlider") as Slider;

            if (ShowHeightGridCheckBox != null)
                ShowHeightGridCheckBox.IsChecked = showHeightGrid;

            if (ShowHeightGridMenuItem != null)
                ShowHeightGridMenuItem.IsChecked = showHeightGrid;

            if (GridCellSizeSlider != null)
                GridCellSizeSlider.Value = gridCellSize;

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

        #endregion

        /// <summary>
        /// Event handler for toggling height profile debugging
        /// </summary>
        private void ToggleHeightProfileDebugging_Click(object sender, RoutedEventArgs e)
        {
            ToggleHeightProfileDebugging();

            // Update menu item checked state
            if (HeightProfileMenuItem != null)
            {
                HeightProfileMenuItem.IsChecked = showHeightProfileDebugging;
            }
        }

        /// <summary>
        /// Event handler for showing color mapping dialog
        /// </summary>
        private void ShowColorMappingDialog_Click(object sender, RoutedEventArgs e)
        {
            ShowColorMappingDialog();
        }

        /// <summary>
        /// Event handler for showing grid mapping window
        /// </summary>
        private void ShowGridMappingWindow_Click(object sender, RoutedEventArgs e)
        {
            ShowGridMappingWindow();
        }

        /// <summary>
        /// Event handler for toggling color detection
        /// </summary>
        private void ColorDetection_Changed(object sender, RoutedEventArgs e)
        {
            enableColorDetection = ColorDetectionCheckBox.IsChecked ?? false;

            if (enableColorDetection && colorToActorMappings.Count == 0)
            {
                InitializeColorDetection();
            }

            StatusText = enableColorDetection ?
                "Color detection enabled - tokens will be categorized by color" :
                "Color detection disabled";

            // Auto-save this setting
            AutoSaveSettings("Color Detection");
        }

        /// <summary>
        /// Event handler for toggling grid mapping
        /// </summary>
        private void GridMapping_Changed(object sender, RoutedEventArgs e)
        {
            isGridMappingActive = GridMappingCheckBox.IsChecked ?? false;

            StatusText = isGridMappingActive ?
                "Grid mapping enabled - positions will be transformed" :
                "Grid mapping disabled";

            // Auto-save this setting
            AutoSaveSettings("Grid Mapping");
        }

        /// <summary>
        /// Event handler for configuring advanced features
        /// </summary>
        private void ConfigureAdvancedFeatures_Click(object sender, RoutedEventArgs e)
        {
            // Create a configuration dialog
            var dialog = new Window
            {
                Title = "Advanced Features Configuration",
                Width = 450,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var panel = new StackPanel { Margin = new Thickness(15) };

            panel.Children.Add(new TextBlock
            {
                Text = "Advanced Detection and Integration Features",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Color detection section
            panel.Children.Add(new TextBlock
            {
                Text = "Color Detection",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            });

            var colorDetectionCheckBox = new CheckBox
            {
                Content = "Enable Color Detection",
                IsChecked = enableColorDetection,
                Margin = new Thickness(0, 0, 0, 5)
            };

            colorDetectionCheckBox.Checked += (s, args) => enableColorDetection = true;
            colorDetectionCheckBox.Unchecked += (s, args) => enableColorDetection = false;
            panel.Children.Add(colorDetectionCheckBox);

            var colorMappingButton = new Button
            {
                Content = "Configure Color Mappings",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 0, 15),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            colorMappingButton.Click += (s, args) =>
            {
                dialog.Close();
                ShowColorMappingDialog();
            };
            panel.Children.Add(colorMappingButton);

            // Grid mapping section
            panel.Children.Add(new TextBlock
            {
                Text = "Grid Mapping",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            });

            var gridMappingCheckBox = new CheckBox
            {
                Content = "Enable Grid Mapping",
                IsChecked = isGridMappingActive,
                Margin = new Thickness(0, 0, 0, 5)
            };

            gridMappingCheckBox.Checked += (s, args) => isGridMappingActive = true;
            gridMappingCheckBox.Unchecked += (s, args) => isGridMappingActive = false;
            panel.Children.Add(gridMappingCheckBox);

            var gridMappingButton = new Button
            {
                Content = "Configure Grid Mapping",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 0, 15),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            gridMappingButton.Click += (s, args) =>
            {
                dialog.Close();
                ShowGridMappingWindow();
            };
            panel.Children.Add(gridMappingButton);

            // Debugging section
            panel.Children.Add(new TextBlock
            {
                Text = "Debugging",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            });

            var debuggingButton = new Button
            {
                Content = "Height Profile Debugging",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 0, 15),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            debuggingButton.Click += (s, args) =>
            {
                dialog.Close();
                ToggleHeightProfileDebugging();
            };
            panel.Children.Add(debuggingButton);

            // Close button
            var closeButton = new Button
            {
                Content = "Close",
                Padding = new Thickness(15, 5, 15, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };
            closeButton.Click += (s, args) => dialog.Close();
            panel.Children.Add(closeButton);

            dialog.Content = panel;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows the height grid configuration dialog
        /// </summary>
        private void ConfigureHeightGrid_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Window
            {
                Title = "Height Grid Configuration",
                Width = 500,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create main layout
            var mainPanel = new StackPanel { Margin = new Thickness(15) };

            // Title
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Height Grid Configuration",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Description
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Configure the visualization of height data across the table surface.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Grid cell size slider
            var cellSizePanel = new StackPanel { Margin = new Thickness(0, 5, 0, 15) };
            cellSizePanel.Children.Add(new TextBlock
            {
                Text = "Grid Cell Size (pixels):",
                FontWeight = FontWeights.Bold
            });

            var cellSizeSlider = new Slider
            {
                Minimum = 5,
                Maximum = 50,
                Value = gridCellSize,
                TickFrequency = 5,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Width = 300,
                Margin = new Thickness(0, 5, 0, 0)
            };

            var cellSizeValue = new TextBlock
            {
                Text = $"Current size: {gridCellSize} pixels",
                Margin = new Thickness(0, 5, 0, 0)
            };

            cellSizeSlider.ValueChanged += (s, args) =>
            {
                gridCellSize = (int)args.NewValue;
                cellSizeValue.Text = $"Current size: {gridCellSize} pixels";
            };

            cellSizePanel.Children.Add(cellSizeSlider);
            cellSizePanel.Children.Add(cellSizeValue);
            mainPanel.Children.Add(cellSizePanel);

            // Color scheme
            var colorSchemePanel = new StackPanel { Margin = new Thickness(0, 5, 0, 15) };
            colorSchemePanel.Children.Add(new TextBlock
            {
                Text = "Color Scheme:",
                FontWeight = FontWeights.Bold
            });

            var colorSchemeCombo = new ComboBox
            {
                Width = 200,
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            colorSchemeCombo.Items.Add("Green-Red (Height)");
            colorSchemeCombo.Items.Add("Blue-Red (Heat Map)");
            colorSchemeCombo.Items.Add("Rainbow Spectrum");
            colorSchemeCombo.Items.Add("Grayscale");
            colorSchemeCombo.SelectedIndex = 0;

            colorSchemePanel.Children.Add(colorSchemeCombo);
            mainPanel.Children.Add(colorSchemePanel);

            // Height range
            var heightRangePanel = new StackPanel { Margin = new Thickness(0, 5, 0, 15) };
            heightRangePanel.Children.Add(new TextBlock
            {
                Text = "Height Range (mm):",
                FontWeight = FontWeights.Bold
            });

            var heightRangeGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            heightRangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heightRangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heightRangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heightRangeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Min height
            heightRangeGrid.Children.Add(new TextBlock
            {
                Text = "Min:",
                VerticalAlignment = VerticalAlignment.Center
            });

            var minHeightInput = new TextBox
            {
                Text = "0",
                Width = 50,
                Margin = new Thickness(5, 0, 20, 0)
            };
            Grid.SetColumn(minHeightInput, 1);
            heightRangeGrid.Children.Add(minHeightInput);

            // Max height
            heightRangeGrid.Children.Add(new TextBlock
            {
                Text = "Max:",
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(heightRangeGrid.Children[2], 2);

            var maxHeightInput = new TextBox
            {
                Text = "100",
                Width = 50,
                Margin = new Thickness(5, 0, 0, 0)
            };
            Grid.SetColumn(maxHeightInput, 3);
            heightRangeGrid.Children.Add(maxHeightInput);

            heightRangePanel.Children.Add(heightRangeGrid);
            mainPanel.Children.Add(heightRangePanel);

            // Show text option
            var showValuesCheckBox = new CheckBox
            {
                Content = "Show height values in cells",
                IsChecked = true,
                Margin = new Thickness(0, 5, 0, 15)
            };
            mainPanel.Children.Add(showValuesCheckBox);

            // Apply to ROI only
            var roiOnlyCheckBox = new CheckBox
            {
                Content = "Apply to ROI only (uncheck to visualize entire depth field)",
                IsChecked = true,
                Margin = new Thickness(0, 5, 0, 15)
            };
            mainPanel.Children.Add(roiOnlyCheckBox);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var applyButton = new Button
            {
                Content = "Apply",
                Padding = new Thickness(20, 5, 20, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            applyButton.Click += (s, args) =>
            {
                // Apply settings
                gridCellSize = (int)cellSizeSlider.Value;

                // Parse min/max height if valid
                if (int.TryParse(minHeightInput.Text, out int minHeight) &&
                    int.TryParse(maxHeightInput.Text, out int maxHeight))
                {
                    // Store min/max height values (would need to add these as class fields)
                    // minGridHeight = minHeight;
                    // maxGridHeight = maxHeight;
                }

                // Apply color scheme based on selection
                // Apply showValues setting

                // Update the grid with new settings
                if (hasValidROI && showHeightGrid)
                {
                    InitializeHeightGrid();
                    UpdateHeightGrid();
                }

                // Update UI
                GridCellSizeSlider.Value = gridCellSize;

                StatusText = "Height grid settings applied";
                dialog.Close();
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 5, 20, 5)
            };

            cancelButton.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(applyButton);
            buttonPanel.Children.Add(cancelButton);
            mainPanel.Children.Add(buttonPanel);

            // Set content and show dialog
            dialog.Content = mainPanel;
            dialog.ShowDialog();
        }

        /// <summary>
        /// Update height grid on button click
        /// </summary>
        private void UpdateHeightGrid_Click(object sender, RoutedEventArgs e)
        {
            if (!hasValidROI || !hasValidTableDepth)
            {
                StatusText = "Cannot update height grid: No valid ROI or table depth";
                return;
            }

            if (!showHeightGrid)
            {
                showHeightGrid = true;
                ShowHeightGridCheckBox.IsChecked = true;
                ShowHeightGridMenuItem.IsChecked = true;
            }

            InitializeHeightGrid();
            UpdateHeightGrid();
            StatusText = "Height grid updated";
        }

    }
}
