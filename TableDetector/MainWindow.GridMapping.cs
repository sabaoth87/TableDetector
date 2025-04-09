using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Text.Json;
using System.Linq;

namespace TableDetector
{
    public partial class MainWindow
    {
        // Grid mapping configuration
        private bool isGridMappingActive = false;
        private GridMapping currentGridMapping = new GridMapping();
        private Window gridMappingWindow = null;

        /// <summary>
        /// Class to store grid mapping configuration between physical table and virtual grid
        /// </summary>
        public class GridMapping
        {
            // Physical points (in mm) from table calibration
            public Point PhysicalOrigin { get; set; } = new Point(0, 0);
            public double PhysicalGridSize { get; set; } = 25.4; // 1 inch = 25.4mm
            public double PhysicalRotation { get; set; } = 0; // Rotation in degrees

            // Virtual grid settings for Foundry VTT
            public Point VirtualOrigin { get; set; } = new Point(0, 0);
            public double VirtualGridSize { get; set; } = 100; // Foundry default grid size in pixels
            public double VirtualScale { get; set; } = 1.0; // Scale factor between physical and virtual

            // Boundary of the play area
            public double PlayAreaWidth { get; set; } = 800; // Width in mm
            public double PlayAreaHeight { get; set; } = 600; // Height in mm

            // Calibration points for mapping
            public List<CalibrationPoint> CalibrationPoints { get; set; } = new List<CalibrationPoint>();

            /// <summary>
            /// Converts a physical position to a Foundry grid position
            /// </summary>
            public Point PhysicalToVirtual(Point physicalPoint)
            {
                // Translate to origin
                double translatedX = physicalPoint.X - PhysicalOrigin.X;
                double translatedY = physicalPoint.Y - PhysicalOrigin.Y;

                // Apply rotation
                double radians = PhysicalRotation * Math.PI / 180;
                double rotatedX = translatedX * Math.Cos(radians) - translatedY * Math.Sin(radians);
                double rotatedY = translatedX * Math.Sin(radians) + translatedY * Math.Cos(radians);

                // Scale to virtual grid
                double scaledX = rotatedX * (VirtualGridSize / PhysicalGridSize) * VirtualScale;
                double scaledY = rotatedY * (VirtualGridSize / PhysicalGridSize) * VirtualScale;

                // Translate to virtual origin
                return new Point(
                    VirtualOrigin.X + scaledX,
                    VirtualOrigin.Y + scaledY
                );
            }

            /// <summary>
            /// Converts a Foundry grid position to a physical position
            /// </summary>
            public Point VirtualToPhysical(Point virtualPoint)
            {
                // Translate to origin
                double translatedX = virtualPoint.X - VirtualOrigin.X;
                double translatedY = virtualPoint.Y - VirtualOrigin.Y;

                // Scale to physical grid
                double scaledX = translatedX / ((VirtualGridSize / PhysicalGridSize) * VirtualScale);
                double scaledY = translatedY / ((VirtualGridSize / PhysicalGridSize) * VirtualScale);

                // Apply reverse rotation
                double radians = -PhysicalRotation * Math.PI / 180;
                double rotatedX = scaledX * Math.Cos(radians) - scaledY * Math.Sin(radians);
                double rotatedY = scaledX * Math.Sin(radians) + scaledY * Math.Cos(radians);

                // Translate back from origin
                return new Point(
                    PhysicalOrigin.X + rotatedX,
                    PhysicalOrigin.Y + rotatedY
                );
            }

            /// <summary>
            /// Calculates optimal mapping based on calibration points
            /// </summary>
            public void CalculateOptimalMapping()
            {
                if (CalibrationPoints.Count < 3)
                    return; // Need at least 3 points for proper mapping

                // Get physical and virtual positions from calibration points
                List<Point> physicalPoints = new List<Point>();
                List<Point> virtualPoints = new List<Point>();

                foreach (var cp in CalibrationPoints)
                {
                    if (cp.IsCalibrated)
                    {
                        physicalPoints.Add(cp.PhysicalPosition);
                        virtualPoints.Add(cp.VirtualPosition);
                    }
                }

                if (physicalPoints.Count < 3)
                    return;

                // Calculate center points
                Point physicalCenter = new Point(0, 0);
                Point virtualCenter = new Point(0, 0);

                foreach (var p in physicalPoints)
                {
                    physicalCenter.X += p.X;
                    physicalCenter.Y += p.Y;
                }

                foreach (var p in virtualPoints)
                {
                    virtualCenter.X += p.X;
                    virtualCenter.Y += p.Y;
                }

                physicalCenter.X /= physicalPoints.Count;
                physicalCenter.Y /= physicalPoints.Count;
                virtualCenter.X /= virtualPoints.Count;
                virtualCenter.Y /= virtualPoints.Count;

                // Set as origins
                PhysicalOrigin = physicalCenter;
                VirtualOrigin = virtualCenter;

                // Calculate approximate rotation
                double totalAngleDiff = 0;
                int angleSamples = 0;

                for (int i = 0; i < physicalPoints.Count; i++)
                {
                    Point pPhys = physicalPoints[i];
                    Point pVirt = virtualPoints[i];

                    // Get angle from center to point
                    double physAngle = Math.Atan2(pPhys.Y - physicalCenter.Y, pPhys.X - physicalCenter.X);
                    double virtAngle = Math.Atan2(pVirt.Y - virtualCenter.Y, pVirt.X - virtualCenter.X);

                    // Calculate angle difference
                    double angleDiff = virtAngle - physAngle;

                    // Normalize to -PI to PI
                    while (angleDiff > Math.PI) angleDiff -= 2 * Math.PI;
                    while (angleDiff < -Math.PI) angleDiff += 2 * Math.PI;

                    totalAngleDiff += angleDiff;
                    angleSamples++;
                }

                // Set rotation in degrees
                PhysicalRotation = (totalAngleDiff / angleSamples) * 180 / Math.PI;

                // Calculate scale by comparing distances
                double totalPhysicalDistance = 0;
                double totalVirtualDistance = 0;

                for (int i = 0; i < physicalPoints.Count; i++)
                {
                    Point pPhys = physicalPoints[i];
                    Point pVirt = virtualPoints[i];

                    // Calculate distance from center
                    double physDist = Math.Sqrt(
                        Math.Pow(pPhys.X - physicalCenter.X, 2) +
                        Math.Pow(pPhys.Y - physicalCenter.Y, 2));

                    double virtDist = Math.Sqrt(
                        Math.Pow(pVirt.X - virtualCenter.X, 2) +
                        Math.Pow(pVirt.Y - virtualCenter.Y, 2));

                    totalPhysicalDistance += physDist;
                    totalVirtualDistance += virtDist;
                }

                // Set scale factor
                if (totalPhysicalDistance > 0)
                {
                    VirtualScale = totalVirtualDistance / totalPhysicalDistance;
                }
            }
        }

        /// <summary>
        /// Represents a calibration point for grid mapping
        /// </summary>
        public class CalibrationPoint
        {
            // Physical position on the table (in mm)
            public Point PhysicalPosition { get; set; }

            // Corresponding position in Foundry VTT
            public Point VirtualPosition { get; set; }

            // Name/label for this calibration point
            public string Label { get; set; }

            // Whether this point has been calibrated
            public bool IsCalibrated { get; set; } = false;

            // Optional token ID associated with this point
            public string TokenId { get; set; }
        }

        /// <summary>
        /// Opens the grid mapping configuration window
        /// </summary>
        private void ShowGridMappingWindow()
        {
            if (gridMappingWindow != null)
            {
                gridMappingWindow.Activate();
                return;
            }

            // Create the window
            gridMappingWindow = new Window
            {
                Title = "Table-to-Grid Mapping",
                Width = 800,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create main layout
            var mainPanel = new DockPanel();

            // Create header
            var headerPanel = new StackPanel
            {
                Margin = new Thickness(10)
            };

            headerPanel.Children.Add(new TextBlock
            {
                Text = "Physical Table to Foundry Grid Mapping",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            headerPanel.Children.Add(new TextBlock
            {
                Text = "This tool helps you align your physical tabletop with the virtual grid in Foundry VTT. " +
                      "Place tokens on your table at known grid positions, then map them to the corresponding " +
                      "positions in Foundry.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            });

            DockPanel.SetDock(headerPanel, Dock.Top);
            mainPanel.Children.Add(headerPanel);

            // Create a tabbed interface
            var tabControl = new TabControl();

            // Tab 1: Calibration Points
            var calibrationTab = new TabItem { Header = "Calibration Points" };
            calibrationTab.Content = CreateCalibrationPointsUI();
            tabControl.Items.Add(calibrationTab);

            // Tab 2: Grid Settings
            var gridSettingsTab = new TabItem { Header = "Grid Settings" };
            gridSettingsTab.Content = CreateGridSettingsUI();
            tabControl.Items.Add(gridSettingsTab);

            // Tab 3: Visualization
            var visualizationTab = new TabItem { Header = "Visualization" };
            visualizationTab.Content = CreateVisualizationUI();
            tabControl.Items.Add(visualizationTab);

            mainPanel.Children.Add(tabControl);

            // Create footer with action buttons
            var footerPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(10)
            };

            var activateButton = new Button
            {
                Content = isGridMappingActive ? "Deactivate Mapping" : "Activate Mapping",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            activateButton.Click += (s, e) =>
            {
                isGridMappingActive = !isGridMappingActive;
                activateButton.Content = isGridMappingActive ? "Deactivate Mapping" : "Activate Mapping";
                StatusText = isGridMappingActive ?
                    "Grid mapping active - positions will be transformed" :
                    "Grid mapping disabled";
            };

            var calculateButton = new Button
            {
                Content = "Calculate Mapping",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            calculateButton.Click += (s, e) =>
            {
                currentGridMapping.CalculateOptimalMapping();
                UpdateMappingDisplays();
                StatusText = "Grid mapping calculated";
            };

            var saveButton = new Button
            {
                Content = "Save",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            saveButton.Click += (s, e) =>
            {
                SaveGridMapping();
                gridMappingWindow.Close();
                gridMappingWindow = null;
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(15, 5, 15, 5)
            };

            cancelButton.Click += (s, e) =>
            {
                gridMappingWindow.Close();
                gridMappingWindow = null;
            };

            footerPanel.Children.Add(activateButton);
            footerPanel.Children.Add(calculateButton);
            footerPanel.Children.Add(saveButton);
            footerPanel.Children.Add(cancelButton);

            DockPanel.SetDock(footerPanel, Dock.Bottom);
            mainPanel.Children.Add(footerPanel);

            // Set content and show dialog
            gridMappingWindow.Content = mainPanel;
            gridMappingWindow.Closed += (s, e) => { gridMappingWindow = null; };
            gridMappingWindow.Show();

            // Initialize the displays
            UpdateMappingDisplays();
        }

        private Grid calibrationPointsGrid;
        private TextBox physicalGridSizeTextBox;
        private TextBox virtualGridSizeTextBox;
        private TextBox rotationTextBox;
        private TextBox scaleTextBox;
        private Canvas mappingVisualizationCanvas;

        /// <summary>
        /// Creates the UI for managing calibration points
        /// </summary>
        private UIElement CreateCalibrationPointsUI()
        {
            var panel = new DockPanel();

            // Create top toolbar
            var toolbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10)
            };

            var addButton = new Button
            {
                Content = "Add Point",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 10, 0)
            };

            addButton.Click += (s, e) => AddCalibrationPoint();
            toolbarPanel.Children.Add(addButton);

            var removeButton = new Button
            {
                Content = "Remove Selected",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 10, 0)
            };

            removeButton.Click += (s, e) => RemoveSelectedCalibrationPoint();
            toolbarPanel.Children.Add(removeButton);

            var clearButton = new Button
            {
                Content = "Clear All",
                Padding = new Thickness(10, 3, 10, 3)
            };

            clearButton.Click += (s, e) => ClearAllCalibrationPoints();
            toolbarPanel.Children.Add(clearButton);

            DockPanel.SetDock(toolbarPanel, Dock.Top);
            panel.Children.Add(toolbarPanel);

            // Create the calibration points grid
            calibrationPointsGrid = new Grid
            {
                Margin = new Thickness(10)
            };

            // Define columns
            calibrationPointsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Label
            calibrationPointsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Physical X
            calibrationPointsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Physical Y
            calibrationPointsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Virtual X
            calibrationPointsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // Virtual Y

            // Add header row
            calibrationPointsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var labelHeader = new TextBlock
            {
                Text = "Label",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };
            Grid.SetRow(labelHeader, 0);
            Grid.SetColumn(labelHeader, 0);
            calibrationPointsGrid.Children.Add(labelHeader);

            var physXHeader = new TextBlock
            {
                Text = "Physical X (mm)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };
            Grid.SetRow(physXHeader, 0);
            Grid.SetColumn(physXHeader, 1);
            calibrationPointsGrid.Children.Add(physXHeader);

            var physYHeader = new TextBlock
            {
                Text = "Physical Y (mm)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };
            Grid.SetRow(physYHeader, 0);
            Grid.SetColumn(physYHeader, 2);
            calibrationPointsGrid.Children.Add(physYHeader);

            var virtXHeader = new TextBlock
            {
                Text = "Virtual X (px)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };
            Grid.SetRow(virtXHeader, 0);
            Grid.SetColumn(virtXHeader, 3);
            calibrationPointsGrid.Children.Add(virtXHeader);

            var virtYHeader = new TextBlock
            {
                Text = "Virtual Y (px)",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(5)
            };
            Grid.SetRow(virtYHeader, 0);
            Grid.SetColumn(virtYHeader, 4);
            calibrationPointsGrid.Children.Add(virtYHeader);

            // Create scrollable container for the grid
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = calibrationPointsGrid
            };

            panel.Children.Add(scrollViewer);

            // Populate with existing calibration points
            PopulateCalibrationPointsGrid();

            return panel;
        }

        /// <summary>
        /// Creates the UI for adjusting grid settings
        /// </summary>
        private UIElement CreateGridSettingsUI()
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(15)
            };

            // Physical grid settings
            panel.Children.Add(new TextBlock
            {
                Text = "Physical Table Settings",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var physicalGridPanel = new Grid
            {
                Margin = new Thickness(0, 0, 0, 20)
            };

            physicalGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            physicalGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            physicalGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            physicalGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            physicalGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            physicalGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Grid size
            physicalGridPanel.Children.Add(new TextBlock
            {
                Text = "Grid Size (mm):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(physicalGridPanel.Children[0], 0);
            Grid.SetColumn(physicalGridPanel.Children[0], 0);

            physicalGridSizeTextBox = new TextBox
            {
                Text = currentGridMapping.PhysicalGridSize.ToString("F2"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            Grid.SetRow(physicalGridSizeTextBox, 0);
            Grid.SetColumn(physicalGridSizeTextBox, 1);
            physicalGridPanel.Children.Add(physicalGridSizeTextBox);

            physicalGridPanel.Children.Add(new TextBlock
            {
                Text = "(25.4mm = 1 inch)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = new SolidColorBrush(Colors.Gray)
            });

            Grid.SetRow(physicalGridPanel.Children[2], 0);
            Grid.SetColumn(physicalGridPanel.Children[2], 2);

            // Play area width
            physicalGridPanel.Children.Add(new TextBlock
            {
                Text = "Play Area Width (mm):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(physicalGridPanel.Children[3], 1);
            Grid.SetColumn(physicalGridPanel.Children[3], 0);

            var playAreaWidthTextBox = new TextBox
            {
                Text = currentGridMapping.PlayAreaWidth.ToString("F0"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            playAreaWidthTextBox.TextChanged += (s, e) =>
            {
                if (double.TryParse(playAreaWidthTextBox.Text, out double width))
                {
                    currentGridMapping.PlayAreaWidth = width;
                }
            };

            Grid.SetRow(playAreaWidthTextBox, 1);
            Grid.SetColumn(playAreaWidthTextBox, 1);
            physicalGridPanel.Children.Add(playAreaWidthTextBox);

            // Play area height
            physicalGridPanel.Children.Add(new TextBlock
            {
                Text = "Play Area Height (mm):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(physicalGridPanel.Children[5], 2);
            Grid.SetColumn(physicalGridPanel.Children[5], 0);

            var playAreaHeightTextBox = new TextBox
            {
                Text = currentGridMapping.PlayAreaHeight.ToString("F0"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            playAreaHeightTextBox.TextChanged += (s, e) =>
            {
                if (double.TryParse(playAreaHeightTextBox.Text, out double height))
                {
                    currentGridMapping.PlayAreaHeight = height;
                }
            };

            Grid.SetRow(playAreaHeightTextBox, 2);
            Grid.SetColumn(playAreaHeightTextBox, 1);
            physicalGridPanel.Children.Add(playAreaHeightTextBox);

            panel.Children.Add(physicalGridPanel);

            // Virtual grid settings
            panel.Children.Add(new TextBlock
            {
                Text = "Foundry VTT Grid Settings",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 10, 0, 10)
            });

            var virtualGridPanel = new Grid
            {
                Margin = new Thickness(0, 0, 0, 20)
            };

            virtualGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            virtualGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            virtualGridPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            virtualGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            virtualGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            virtualGridPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Grid size
            virtualGridPanel.Children.Add(new TextBlock
            {
                Text = "Grid Size (px):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(virtualGridPanel.Children[0], 0);
            Grid.SetColumn(virtualGridPanel.Children[0], 0);

            virtualGridSizeTextBox = new TextBox
            {
                Text = currentGridMapping.VirtualGridSize.ToString("F0"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            Grid.SetRow(virtualGridSizeTextBox, 0);
            Grid.SetColumn(virtualGridSizeTextBox, 1);
            virtualGridPanel.Children.Add(virtualGridSizeTextBox);

            virtualGridPanel.Children.Add(new TextBlock
            {
                Text = "(Default in Foundry is 100px)",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = new SolidColorBrush(Colors.Gray)
            });

            Grid.SetRow(virtualGridPanel.Children[2], 0);
            Grid.SetColumn(virtualGridPanel.Children[2], 2);

            // Rotation
            virtualGridPanel.Children.Add(new TextBlock
            {
                Text = "Rotation (degrees):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(virtualGridPanel.Children[3], 1);
            Grid.SetColumn(virtualGridPanel.Children[3], 0);

            rotationTextBox = new TextBox
            {
                Text = currentGridMapping.PhysicalRotation.ToString("F1"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsReadOnly = true
            };

            Grid.SetRow(rotationTextBox, 1);
            Grid.SetColumn(rotationTextBox, 1);
            virtualGridPanel.Children.Add(rotationTextBox);

            // Scale
            virtualGridPanel.Children.Add(new TextBlock
            {
                Text = "Scale Factor:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            });

            Grid.SetRow(virtualGridPanel.Children[5], 2);
            Grid.SetColumn(virtualGridPanel.Children[5], 0);

            scaleTextBox = new TextBox
            {
                Text = currentGridMapping.VirtualScale.ToString("F3"),
                Margin = new Thickness(0, 5, 10, 5),
                Width = 100,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsReadOnly = true
            };

            Grid.SetRow(scaleTextBox, 2);
            Grid.SetColumn(scaleTextBox, 1);
            virtualGridPanel.Children.Add(scaleTextBox);

            panel.Children.Add(virtualGridPanel);

            // Apply changes button
            var applyButton = new Button
            {
                Content = "Apply Changes",
                Padding = new Thickness(15, 5, 15, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };

            applyButton.Click += (s, e) => ApplyGridSettingsChanges();
            panel.Children.Add(applyButton);

            return panel;
        }

        /// <summary>
        /// Creates the UI for visualizing the grid mapping
        /// </summary>
        private UIElement CreateVisualizationUI()
        {
            var panel = new DockPanel();

            // Create info panel
            var infoPanel = new StackPanel
            {
                Margin = new Thickness(10)
            };

            infoPanel.Children.Add(new TextBlock
            {
                Text = "Grid Mapping Visualization",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            });

            infoPanel.Children.Add(new TextBlock
            {
                Text = "This visualization shows how your physical table (blue) maps to the Foundry VTT grid (orange).",
                TextWrapping = TextWrapping.Wrap
            });

            DockPanel.SetDock(infoPanel, Dock.Top);
            panel.Children.Add(infoPanel);

            // Create canvas for visualization
            mappingVisualizationCanvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                ClipToBounds = true
            };

            panel.Children.Add(mappingVisualizationCanvas);

            return panel;
        }

        /// <summary>
        /// Updates all mapping display elements
        /// </summary>
        private void UpdateMappingDisplays()
        {
            // Update text displays
            if (rotationTextBox != null)
                rotationTextBox.Text = currentGridMapping.PhysicalRotation.ToString("F1");

            if (scaleTextBox != null)
                scaleTextBox.Text = currentGridMapping.VirtualScale.ToString("F3");

            if (physicalGridSizeTextBox != null)
                physicalGridSizeTextBox.Text = currentGridMapping.PhysicalGridSize.ToString("F2");

            if (virtualGridSizeTextBox != null)
                virtualGridSizeTextBox.Text = currentGridMapping.VirtualGridSize.ToString("F0");

            // Update visualization
            UpdateVisualizationCanvas();
        }

        /// <summary>
        /// Updates the visualization canvas
        /// </summary>
        private void UpdateVisualizationCanvas()
        {
            if (mappingVisualizationCanvas == null)
                return;

            // Clear the canvas
            mappingVisualizationCanvas.Children.Clear();

            // Get canvas dimensions
            double canvasWidth = mappingVisualizationCanvas.ActualWidth;
            double canvasHeight = mappingVisualizationCanvas.ActualHeight;

            // If canvas not properly sized yet, wait
            if (canvasWidth < 10 || canvasHeight < 10)
                return;

            // Calculate scale factor for visualization
            double physicalWidth = currentGridMapping.PlayAreaWidth;
            double physicalHeight = currentGridMapping.PlayAreaHeight;

            double scaleX = (canvasWidth * 0.8) / physicalWidth;
            double scaleY = (canvasHeight * 0.8) / physicalHeight;
            double scale = Math.Min(scaleX, scaleY);

            // Calculate offsets to center in canvas
            double offsetX = (canvasWidth - (physicalWidth * scale)) / 2;
            double offsetY = (canvasHeight - (physicalHeight * scale)) / 2;

            // Draw physical table outline
            Rectangle tableRect = new Rectangle
            {
                Width = physicalWidth * scale,
                Height = physicalHeight * scale,
                Stroke = new SolidColorBrush(Colors.LightBlue),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(30, 100, 150, 255))
            };

            Canvas.SetLeft(tableRect, offsetX);
            Canvas.SetTop(tableRect, offsetY);
            mappingVisualizationCanvas.Children.Add(tableRect);

            // Draw physical grid
            double gridSize = currentGridMapping.PhysicalGridSize * scale;

            // Vertical grid lines
            for (double x = 0; x <= physicalWidth; x += currentGridMapping.PhysicalGridSize)
            {
                Line gridLine = new Line
                {
                    X1 = offsetX + (x * scale),
                    Y1 = offsetY,
                    X2 = offsetX + (x * scale),
                    Y2 = offsetY + (physicalHeight * scale),
                    Stroke = new SolidColorBrush(Colors.LightBlue),
                    StrokeThickness = 1,
                    Opacity = 0.5
                };

                mappingVisualizationCanvas.Children.Add(gridLine);
            }

            // Horizontal grid lines
            for (double y = 0; y <= physicalHeight; y += currentGridMapping.PhysicalGridSize)
            {
                Line gridLine = new Line
                {
                    X1 = offsetX,
                    Y1 = offsetY + (y * scale),
                    X2 = offsetX + (physicalWidth * scale),
                    Y2 = offsetY + (y * scale),
                    Stroke = new SolidColorBrush(Colors.LightBlue),
                    StrokeThickness = 1,
                    Opacity = 0.5
                };

                mappingVisualizationCanvas.Children.Add(gridLine);
            }

            // Draw virtual grid (transformed)
            int virtualGridCount = 20; // Number of grid cells to visualize

            for (int i = -virtualGridCount / 2; i <= virtualGridCount / 2; i++)
            {
                for (int j = -virtualGridCount / 2; j <= virtualGridCount / 2; j++)
                {
                    // Calculate virtual grid positions
                    Point virtPos = new Point(
                        currentGridMapping.VirtualOrigin.X + (i * currentGridMapping.VirtualGridSize),
                        currentGridMapping.VirtualOrigin.Y + (j * currentGridMapping.VirtualGridSize)
                    );

                    // Convert to physical position
                    Point physPos = currentGridMapping.VirtualToPhysical(virtPos);

                    // Scale and offset for canvas
                    double canvasX = offsetX + (physPos.X * scale);
                    double canvasY = offsetY + (physPos.Y * scale);

                    // Only draw if within canvas bounds
                    if (canvasX >= 0 && canvasX <= canvasWidth &&
                        canvasY >= 0 && canvasY <= canvasHeight)
                    {
                        // Draw a small marker for the grid point
                        Ellipse gridPoint = new Ellipse
                        {
                            Width = 5,
                            Height = 5,
                            Fill = new SolidColorBrush(Colors.Orange)
                        };

                        Canvas.SetLeft(gridPoint, canvasX - 2.5);
                        Canvas.SetTop(gridPoint, canvasY - 2.5);
                        mappingVisualizationCanvas.Children.Add(gridPoint);
                    }
                }
            }

            // Draw calibration points
            foreach (var cp in currentGridMapping.CalibrationPoints)
            {
                if (cp.IsCalibrated)
                {
                    // Physical position (blue)
                    Ellipse physPoint = new Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new SolidColorBrush(Colors.Blue),
                        Stroke = new SolidColorBrush(Colors.White),
                        StrokeThickness = 1
                    };

                    Canvas.SetLeft(physPoint, offsetX + (cp.PhysicalPosition.X * scale) - 5);
                    Canvas.SetTop(physPoint, offsetY + (cp.PhysicalPosition.Y * scale) - 5);
                    mappingVisualizationCanvas.Children.Add(physPoint);

                    // Virtual position (orange)
                    Point virtPhysPos = currentGridMapping.VirtualToPhysical(cp.VirtualPosition);

                    Ellipse virtPoint = new Ellipse
                    {
                        Width = 10,
                        Height = 10,
                        Fill = new SolidColorBrush(Colors.Orange),
                        Stroke = new SolidColorBrush(Colors.White),
                        StrokeThickness = 1
                    };

                    Canvas.SetLeft(virtPoint, offsetX + (virtPhysPos.X * scale) - 5);
                    Canvas.SetTop(virtPoint, offsetY + (virtPhysPos.Y * scale) - 5);
                    mappingVisualizationCanvas.Children.Add(virtPoint);

                    // Line connecting them
                    Line connectionLine = new Line
                    {
                        X1 = offsetX + (cp.PhysicalPosition.X * scale),
                        Y1 = offsetY + (cp.PhysicalPosition.Y * scale),
                        X2 = offsetX + (virtPhysPos.X * scale),
                        Y2 = offsetY + (virtPhysPos.Y * scale),
                        Stroke = new SolidColorBrush(Colors.White),
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 2, 2 }
                    };

                    mappingVisualizationCanvas.Children.Add(connectionLine);

                    // Point label
                    TextBlock label = new TextBlock
                    {
                        Text = cp.Label,
                        Foreground = new SolidColorBrush(Colors.White),
                        Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                        Padding = new Thickness(3),
                        FontSize = 10
                    };

                    Canvas.SetLeft(label, offsetX + (cp.PhysicalPosition.X * scale) + 10);
                    Canvas.SetTop(label, offsetY + (cp.PhysicalPosition.Y * scale) - 10);
                    mappingVisualizationCanvas.Children.Add(label);
                }
            }

            // Add legend
            DrawMappingLegend();
        }

        /// <summary>
        /// Draws a legend for the mapping visualization
        /// </summary>
        private void DrawMappingLegend()
        {
            // Create legend panel
            Border legendBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Colors.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };

            StackPanel legendPanel = new StackPanel();

            // Title
            legendPanel.Children.Add(new TextBlock
            {
                Text = "Legend",
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 5)
            });

            // Physical grid
            StackPanel physicalPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            physicalPanel.Children.Add(new Rectangle
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Color.FromArgb(30, 100, 150, 255)),
                Stroke = new SolidColorBrush(Colors.LightBlue),
                StrokeThickness = 1,
                Margin = new Thickness(0, 0, 5, 0)
            });
            physicalPanel.Children.Add(new TextBlock
            {
                Text = "Physical Table",
                Foreground = new SolidColorBrush(Colors.White)
            });
            legendPanel.Children.Add(physicalPanel);

            // Physical points
            StackPanel physPointPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            physPointPanel.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Colors.Blue),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                Margin = new Thickness(1, 0, 5, 0)
            });
            physPointPanel.Children.Add(new TextBlock
            {
                Text = "Physical Point",
                Foreground = new SolidColorBrush(Colors.White)
            });
            legendPanel.Children.Add(physPointPanel);

            // Virtual points
            StackPanel virtPointPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            virtPointPanel.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Colors.Orange),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1,
                Margin = new Thickness(1, 0, 5, 0)
            });
            virtPointPanel.Children.Add(new TextBlock
            {
                Text = "Virtual Point",
                Foreground = new SolidColorBrush(Colors.White)
            });
            legendPanel.Children.Add(virtPointPanel);

            // Virtual grid
            StackPanel gridPointPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            gridPointPanel.Children.Add(new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = new SolidColorBrush(Colors.Orange),
                Margin = new Thickness(3, 0, 7, 0)
            });
            gridPointPanel.Children.Add(new TextBlock
            {
                Text = "Virtual Grid Point",
                Foreground = new SolidColorBrush(Colors.White)
            });
            legendPanel.Children.Add(gridPointPanel);

            legendBorder.Child = legendPanel;

            // Add to canvas
            Canvas.SetLeft(legendBorder, 10);
            Canvas.SetTop(legendBorder, 10);
            mappingVisualizationCanvas.Children.Add(legendBorder);
        }

        /// <summary>
        /// Adds a new calibration point
        /// </summary>
        private void AddCalibrationPoint()
        {
            // Create a new calibration point
            var newPoint = new CalibrationPoint
            {
                Label = $"Point {currentGridMapping.CalibrationPoints.Count + 1}",
                PhysicalPosition = new Point(0, 0),
                VirtualPosition = new Point(0, 0),
                IsCalibrated = false
            };

            // Add to the list
            currentGridMapping.CalibrationPoints.Add(newPoint);

            // Update the grid
            PopulateCalibrationPointsGrid();
        }

        /// <summary>
        /// Removes the selected calibration point
        /// </summary>
        private void RemoveSelectedCalibrationPoint()
        {
            // Get selected point (if any)
            // For simplicity, let's just remove the last one
            if (currentGridMapping.CalibrationPoints.Count > 0)
            {
                currentGridMapping.CalibrationPoints.RemoveAt(
                    currentGridMapping.CalibrationPoints.Count - 1);

                // Update the grid
                PopulateCalibrationPointsGrid();
            }
        }

        /// <summary>
        /// Clears all calibration points
        /// </summary>
        private void ClearAllCalibrationPoints()
        {
            // Confirm with user
            var result = MessageBox.Show(
                "Are you sure you want to clear all calibration points?",
                "Clear Calibration Points",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                currentGridMapping.CalibrationPoints.Clear();
                PopulateCalibrationPointsGrid();
            }
        }

        /// <summary>
        /// Populates the calibration points grid with current data
        /// </summary>
        private void PopulateCalibrationPointsGrid()
        {
            // Clear existing rows (except header)
            while (calibrationPointsGrid.RowDefinitions.Count > 1)
            {
                calibrationPointsGrid.RowDefinitions.RemoveAt(1);
            }

            var children = calibrationPointsGrid.Children.Cast<UIElement>().ToList();
            foreach (var child in children)
            {
                if (Grid.GetRow(child) > 0)
                {
                    calibrationPointsGrid.Children.Remove(child);
                }
            }

            // Add each calibration point as a new row
            for (int i = 0; i < currentGridMapping.CalibrationPoints.Count; i++)
            {
                var point = currentGridMapping.CalibrationPoints[i];
                int rowIndex = i + 1; // +1 because of header row

                // Add row definition
                calibrationPointsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add label
                var labelTextBox = new TextBox
                {
                    Text = point.Label,
                    Margin = new Thickness(5, 2, 5, 2)
                };

                labelTextBox.TextChanged += (s, e) =>
                {
                    point.Label = labelTextBox.Text;
                };

                Grid.SetRow(labelTextBox, rowIndex);
                Grid.SetColumn(labelTextBox, 0);
                calibrationPointsGrid.Children.Add(labelTextBox);

                // Add physical X
                var physXTextBox = new TextBox
                {
                    Text = point.PhysicalPosition.X.ToString("F1"),
                    Margin = new Thickness(5, 2, 5, 2)
                };

                physXTextBox.TextChanged += (s, e) =>
                {
                    if (double.TryParse(physXTextBox.Text, out double xValue))
                    {
                        point.PhysicalPosition = new Point(xValue, point.PhysicalPosition.Y);
                        point.IsCalibrated = true;
                    }
                };

                Grid.SetRow(physXTextBox, rowIndex);
                Grid.SetColumn(physXTextBox, 1);
                calibrationPointsGrid.Children.Add(physXTextBox);

                // Add physical Y
                var physYTextBox = new TextBox
                {
                    Text = point.PhysicalPosition.Y.ToString("F1"),
                    Margin = new Thickness(5, 2, 5, 2)
                };

                physYTextBox.TextChanged += (s, e) =>
                {
                    if (double.TryParse(physYTextBox.Text, out double yValue))
                    {
                        point.PhysicalPosition = new Point(point.PhysicalPosition.X, yValue);
                        point.IsCalibrated = true;
                    }
                };

                Grid.SetRow(physYTextBox, rowIndex);
                Grid.SetColumn(physYTextBox, 2);
                calibrationPointsGrid.Children.Add(physYTextBox);

                // Add virtual X
                var virtXTextBox = new TextBox
                {
                    Text = point.VirtualPosition.X.ToString("F0"),
                    Margin = new Thickness(5, 2, 5, 2)
                };

                virtXTextBox.TextChanged += (s, e) =>
                {
                    if (double.TryParse(virtXTextBox.Text, out double xValue))
                    {
                        point.VirtualPosition = new Point(xValue, point.VirtualPosition.Y);
                        point.IsCalibrated = true;
                    }
                };

                Grid.SetRow(virtXTextBox, rowIndex);
                Grid.SetColumn(virtXTextBox, 3);
                calibrationPointsGrid.Children.Add(virtXTextBox);

                // Add virtual Y
                var virtYTextBox = new TextBox
                {
                    Text = point.VirtualPosition.Y.ToString("F0"),
                    Margin = new Thickness(5, 2, 5, 2)
                };

                virtYTextBox.TextChanged += (s, e) =>
                {
                    if (double.TryParse(virtYTextBox.Text, out double yValue))
                    {
                        point.VirtualPosition = new Point(point.VirtualPosition.X, yValue);
                        point.IsCalibrated = true;
                    }
                };

                Grid.SetRow(virtYTextBox, rowIndex);
                Grid.SetColumn(virtYTextBox, 4);
                calibrationPointsGrid.Children.Add(virtYTextBox);
            }

            // Update visualization
            UpdateMappingDisplays();
        }

        /// <summary>
        /// Applies changes from grid settings UI to current mapping
        /// </summary>
        private void ApplyGridSettingsChanges()
        {
            // Physical grid size
            if (double.TryParse(physicalGridSizeTextBox.Text, out double physGridSize))
            {
                currentGridMapping.PhysicalGridSize = physGridSize;
            }

            // Virtual grid size
            if (double.TryParse(virtualGridSizeTextBox.Text, out double virtGridSize))
            {
                currentGridMapping.VirtualGridSize = virtGridSize;
            }

            // Update displays
            UpdateMappingDisplays();

            StatusText = "Grid settings applied";
        }

        /// <summary>
        /// Saves the current grid mapping to settings
        /// </summary>
        private void SaveGridMapping()
        {
            try
            {
                // Serialize to JSON
                string json = JsonSerializer.Serialize(currentGridMapping,
                    new JsonSerializerOptions { WriteIndented = true });

                // Save to a file
                string mappingFilePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TableDetector",
                    "gridMapping.json");

                System.IO.File.WriteAllText(mappingFilePath, json);

                StatusText = "Grid mapping saved";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving grid mapping: {ex.Message}";
            }
        }

        /// <summary>
        /// Loads grid mapping from settings
        /// </summary>
        private void LoadGridMapping()
        {
            try
            {
                string mappingFilePath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TableDetector",
                    "gridMapping.json");

                if (System.IO.File.Exists(mappingFilePath))
                {
                    string json = System.IO.File.ReadAllText(mappingFilePath);
                    currentGridMapping = JsonSerializer.Deserialize<GridMapping>(json);

                    StatusText = "Grid mapping loaded";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading grid mapping: {ex.Message}";
            }
        }

        /// <summary>
        /// Applies grid mapping to transform a detected token's position
        /// </summary>
        private void ApplyGridMappingToTokens()
        {
            // Only apply if mapping is active
            if (!isGridMappingActive || detectedTokens.Count == 0)
                return;

            foreach (var token in detectedTokens)
            {
                // Create physical point from real world position
                Point physPoint = new Point(
                    token.RealWorldPosition.X * 1000, // Convert meters to mm
                    token.RealWorldPosition.Y * 1000  // Convert meters to mm
                );

                // Apply mapping
                Point mappedPoint = currentGridMapping.PhysicalToVirtual(physPoint);

                // Store the mapped position in token's FoundryPosition property
                token.FoundryPosition = mappedPoint;
            }
        }
    }

}