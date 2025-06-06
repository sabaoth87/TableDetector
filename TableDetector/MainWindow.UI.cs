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
        public bool hasValidROI { get; private set; }
        public bool hasValidTableDepth { get; private set; }
        #region MENU INTERACTIONS

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
        
        #endregion

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
        
        /// <summary>
        /// Event handler for showing grid mapping window
        /// </summary>
        private void ShowGridMappingWindow_Click(object sender, RoutedEventArgs e)
        {
            ShowGridMappingWindow();
        }
        
        /// <summary>
        /// Event handler for toggling grid mapping
        /// </summary>
        private void GridMapping_Changed(object sender, RoutedEventArgs e)
        {
            StatusText = isGridMappingActive ?
                "Grid mapping enabled - positions will be transformed" :
                "Grid mapping disabled";

            // Auto-save this setting
            AutoSaveSettings("Grid Mapping");
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
            
            StatusText = "Height grid updated";
        }

    }
}
