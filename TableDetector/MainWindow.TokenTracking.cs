﻿using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Text.Json;

namespace TableDetector
{
    // This file contains the token tracking methods for the MainWindow class
    public partial class MainWindow
    {
        /// <summary>
        /// Event handler for the Track Tokens checkbox
        /// </summary>
        private void TrackTokens_Changed(object sender, RoutedEventArgs e)
        {
            trackTokens = TrackTokensCheckBox.IsChecked ?? false;
            StatusText = trackTokens ? "Token tracking enabled" : "Token tracking disabled";

            if (!trackTokens)
            {
                // Clear the token overlay
                TokenOverlayCanvas.Children.Clear();
            }

            // Auto-save this setting
            AutoSaveSettings("Token Tracking");
        }

        /// <summary>
        /// Event handler for the Show Token Labels checkbox
        /// </summary>
        private void ShowTokenLabels_Changed(object sender, RoutedEventArgs e)
        {
            showTokenLabels = ShowTokenLabelsCheckBox.IsChecked ?? false;
            StatusText = showTokenLabels ? "Token labels enabled" : "Token labels disabled";

            // Update the token overlay
            UpdateTokenOverlay();

            // Auto-save this setting
            AutoSaveSettings("Show Token Labels");
        }

        /// <summary>
        /// Event handler for the Token Size Threshold slider
        /// </summary>
        private void TokenSizeThreshold_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            tokenDetectionThreshold = (int)e.NewValue;
            StatusText = $"Token size threshold set to {tokenDetectionThreshold} pixels";

            // Auto-save this setting but only after a short delay
            // Use a simple timer approach to avoid saving on every small change during dragging
            System.Threading.Tasks.Task.Delay(500).ContinueWith(_ =>
            {
                this.Dispatcher.Invoke(() => AutoSaveSettings("Token Size Threshold"));
            });
        }

        /// <summary>
        /// Calibrates token detection thresholds
        /// </summary>
        private void CalibrateTokens_ClickOld(object sender, RoutedEventArgs e)
        {
            // Create a calibration dialog
            var dialog = new Window
            {
                Title = "Token & Miniature Calibration",
                Width = 450,
                Height = 450,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create the calibration UI with scrolling
            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var panel = new StackPanel { Margin = new Thickness(10) };
            scrollViewer.Content = panel;

            panel.Children.Add(new TextBlock
            {
                Text = "Configure detection settings for both flat tokens and 3D miniatures.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Token height range sliders
            panel.Children.Add(new TextBlock
            {
                Text = "Flat Token Settings:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 5, 0, 5)
            });

            panel.Children.Add(new TextBlock { Text = "Token Height Range (mm):" });

            var minHeightSlider = new Slider
            {
                Minimum = 2,
                Maximum = 20,
                Value = MIN_TOKEN_HEIGHT,
                TickFrequency = 2,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var minHeightLabel = new TextBlock { Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm" };
            minHeightSlider.ValueChanged += (s, args) =>
            {
                MIN_TOKEN_HEIGHT = (int)args.NewValue;
                minHeightLabel.Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm";
            };

            panel.Children.Add(minHeightSlider);
            panel.Children.Add(minHeightLabel);

            // Add Miniature-specific settings
            panel.Children.Add(new TextBlock
            {
                Text = "Miniature Settings:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 15, 0, 5)
            });

            panel.Children.Add(new TextBlock { Text = "Miniature Max Height (mm):" });

            var maxHeightSlider = new Slider
            {
                Minimum = 30,
                Maximum = 150,
                Value = MAX_TOKEN_HEIGHT,
                TickFrequency = 10,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var maxHeightLabel = new TextBlock { Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm" };
            maxHeightSlider.ValueChanged += (s, args) =>
            {
                MAX_TOKEN_HEIGHT = (int)args.NewValue;
                maxHeightLabel.Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm";
            };

            panel.Children.Add(maxHeightSlider);
            panel.Children.Add(maxHeightLabel);

            // Miniature detection sensitivity
            panel.Children.Add(new TextBlock { Text = "Miniature Detection Sensitivity:" });

            var miniatureThresholdSlider = new Slider
            {
                Minimum = 3,
                Maximum = 20,
                Value = Math.Max(3, tokenDetectionThreshold / 2), // Default: half of normal threshold
                TickFrequency = 1,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var miniatureThresholdLabel = new TextBlock
            {
                Text = $"Minimum Pixel Count: {Math.Max(3, tokenDetectionThreshold / 2)}"
            };

            miniatureThresholdSlider.ValueChanged += (s, args) =>
            {
                // Store this value in a new app setting
                int miniatureDetectionThreshold = (int)args.NewValue;
                miniatureThresholdLabel.Text = $"Minimum Pixel Count: {miniatureDetectionThreshold}";

                // Use App.Current.Properties to store this setting or add a dedicated field
                // For now we'll adjust the main threshold
                tokenDetectionThreshold = Math.Max(miniatureDetectionThreshold * 2, 10);
            };

            panel.Children.Add(miniatureThresholdSlider);
            panel.Children.Add(miniatureThresholdLabel);

            // Add test button to run a detection cycle
            var testButton = new Button
            {
                Content = "Test Current Settings",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 10, 0, 0)
            };

            testButton.Click += (s, args) =>
            {
                // Force a detection cycle
                //DetectTokens();
                StatusText = $"Detected {detectedTokens.Count} objects with current settings";
            };

            panel.Children.Add(testButton);

            // Add buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var saveButton = new Button
            {
                Content = "Save & Close",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            saveButton.Click += (s, args) =>
            {
                // Save the calibration settings
                SaveSettings();
                dialog.Close();
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(10, 5, 10, 5)
            };

            cancelButton.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = scrollViewer;
            dialog.ShowDialog();

            StatusText = "Token and miniature calibration updated";
        }

        private void CalibrateTokens_ClickOlder(object sender, RoutedEventArgs e)
        {
            // Create a calibration dialog
            var dialog = new Window
            {
                Title = "Token Calibration",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            // Create the calibration UI
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock
            {
                Text = "Place a standard token or miniature on the table and adjust settings.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Token height range sliders
            panel.Children.Add(new TextBlock { Text = "Token Height Range (mm):" });

            var minHeightSlider = new Slider
            {
                Minimum = 5,
                Maximum = 30,
                Value = MIN_TOKEN_HEIGHT,
                TickFrequency = 5,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var minHeightLabel = new TextBlock { Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm" };
            minHeightSlider.ValueChanged += (s, args) =>
            {
                MIN_TOKEN_HEIGHT = (int)args.NewValue;
                minHeightLabel.Text = $"Min Height: {MIN_TOKEN_HEIGHT}mm";
            };

            panel.Children.Add(minHeightSlider);
            panel.Children.Add(minHeightLabel);

            var maxHeightSlider = new Slider
            {
                Minimum = 20,
                Maximum = 100,
                Value = MAX_TOKEN_HEIGHT,
                TickFrequency = 10,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var maxHeightLabel = new TextBlock { Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm" };
            maxHeightSlider.ValueChanged += (s, args) =>
            {
                MAX_TOKEN_HEIGHT = (int)args.NewValue;
                maxHeightLabel.Text = $"Max Height: {MAX_TOKEN_HEIGHT}mm";
            };

            panel.Children.Add(maxHeightSlider);
            panel.Children.Add(maxHeightLabel);

            // Size threshold slider
            panel.Children.Add(new TextBlock
            {
                Text = "Token Size Threshold (pixels):",
                Margin = new Thickness(0, 10, 0, 0)
            });

            var sizeThresholdSlider = new Slider
            {
                Minimum = 5,
                Maximum = 50,
                Value = tokenDetectionThreshold,
                TickFrequency = 5,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 5, 0, 5)
            };

            var sizeThresholdLabel = new TextBlock { Text = $"Size Threshold: {tokenDetectionThreshold} pixels" };
            sizeThresholdSlider.ValueChanged += (s, args) =>
            {
                tokenDetectionThreshold = (int)args.NewValue;
                sizeThresholdLabel.Text = $"Size Threshold: {tokenDetectionThreshold} pixels";
                TokenSizeThresholdSlider.Value = tokenDetectionThreshold;
            };

            panel.Children.Add(sizeThresholdSlider);
            panel.Children.Add(sizeThresholdLabel);

            // Add buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var saveButton = new Button
            {
                Content = "Save & Close",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 10, 0)
            };

            saveButton.Click += (s, args) =>
            {
                // Save the calibration settings
                SaveSettings();
                dialog.Close();
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(10, 5, 10, 5)
            };

            cancelButton.Click += (s, args) => dialog.Close();

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            dialog.Content = panel;
            dialog.ShowDialog();

            StatusText = "Token calibration complete";
        }


        /// <summary>
        /// Clears all detected tokens
        /// </summary>
        private void ClearTokens_Click(object sender, RoutedEventArgs e)
        {
            detectedTokens.Clear();
            TokenOverlayCanvas.Children.Clear();
            TokenCountText = "0 tokens";
            StatusText = "All tokens cleared";
        }

        /// <summary>
        /// Saves the current token map to an image file
        /// </summary>
        private void SaveTokenMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Create a save file dialog
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "PNG files (*.png)|*.png|All files (*.*)|*.*",
                    DefaultExt = ".png",
                    Title = "Save Token Map"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    // Capture the depth image with token overlays
                    RenderTargetBitmap renderBitmap = new RenderTargetBitmap(
                        depthWidth, depthHeight, 96, 96, PixelFormats.Pbgra32);

                    // Create a visual to hold both the depth image and token overlays
                    DrawingVisual visual = new DrawingVisual();
                    using (DrawingContext dc = visual.RenderOpen())
                    {
                        // Draw the depth image
                        dc.DrawImage(depthBitmap, new Rect(0, 0, depthWidth, depthHeight));

                        // Draw the token overlays
                        foreach (var token in detectedTokens)
                        {
                            // Draw circle outline for each token
                            Pen pen = new Pen(new SolidColorBrush(Colors.Yellow), 2);
                            dc.DrawEllipse(null, pen,
                                new Point(token.Position.X, token.Position.Y),
                                token.DiameterPixels / 2, token.DiameterPixels / 2);

                            // Draw token label
                            FormattedText text = new FormattedText(
                                $"{token.HeightMm}mm",
                                System.Globalization.CultureInfo.CurrentCulture,
                                FlowDirection.LeftToRight,
                                new Typeface("Segoe UI"),
                                12,
                                Brushes.Yellow,
                                1.0);

                            dc.DrawText(text,
                                new Point(token.Position.X - text.Width / 2,
                                          token.Position.Y - token.DiameterPixels / 2 - 20));
                        }
                    }

                    renderBitmap.Render(visual);

                    // Save to file
                    using (FileStream stream = new FileStream(saveDialog.FileName, FileMode.Create))
                    {
                        PngBitmapEncoder encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(renderBitmap));
                        encoder.Save(stream);
                    }

                    StatusText = $"Token map saved to {saveDialog.FileName}";

                    // Open the file
                    Process.Start(new ProcessStartInfo(saveDialog.FileName) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving token map: {ex.Message}";
            }
        }

        /// <summary>
        /// Assigns the selected token type to all selected tokens
        /// </summary>
        private void AssignTokenType_Click(object sender, RoutedEventArgs e)
        {
            if (detectedTokens.Count == 0)
            {
                MessageBox.Show("No tokens detected to assign type.", "No Tokens", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Get the selected token type
            TokenType selectedType = TokenType.Unknown;

            switch (TokenTypeComboBox.SelectedIndex)
            {
                case 0:
                    selectedType = TokenType.SmallToken;
                    break;
                case 1:
                    selectedType = TokenType.MediumToken;
                    break;
                case 2:
                    selectedType = TokenType.LargeToken;
                    break;
                case 3:
                    selectedType = TokenType.Miniature;
                    break;
                case 4:
                    selectedType = TokenType.Dice;
                    break;
                case 5:
                    selectedType = TokenType.Custom;
                    break;
                default:
                    selectedType = TokenType.Unknown;
                    break;
            }

            // Apply the selected type to all tokens
            // In a real application, you might want to select tokens first
            foreach (var token in detectedTokens)
            {
                token.Type = selectedType;
            }

            // Update the token display
            UpdateTokenOverlay();

            StatusText = $"Assigned type '{selectedType}' to {detectedTokens.Count} tokens";
        }

        /// <summary>
        /// Exports token data to a JSON file for use with VTT software
        /// </summary>
        private void ExportTokenData_Click(object sender, RoutedEventArgs e)
        {
            if (detectedTokens.Count == 0)
            {
                MessageBox.Show("No tokens detected to export.", "No Tokens", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                // Create a save file dialog
                SaveFileDialog saveDialog = new SaveFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    DefaultExt = ".json",
                    Title = "Export Token Data"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    // Create token data for export
                    var exportData = new
                    {
                        Timestamp = DateTime.Now,
                        TableDepth = tableDepth,
                        TokenCount = detectedTokens.Count,
                        Tokens = detectedTokens.Select(t => new
                        {
                            Id = t.Id.ToString(),
                            Type = t.Type.ToString(),
                            PositionX = t.Position.X,
                            PositionY = t.Position.Y,
                            HeightMm = t.HeightMm,
                            DiameterPixels = t.DiameterPixels,
                            DiameterMeters = t.DiameterMeters,
                            RealWorldX = t.RealWorldPosition.X,
                            RealWorldY = t.RealWorldPosition.Y,
                            RealWorldZ = t.RealWorldPosition.Z
                        }).ToArray()
                    };

                    // Serialize and save
                    string jsonData = JsonSerializer.Serialize(exportData,
                        new JsonSerializerOptions { WriteIndented = true });

                    File.WriteAllText(saveDialog.FileName, jsonData);

                    StatusText = $"Token data exported to {saveDialog.FileName}";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error exporting token data: {ex.Message}";
            }
        }

        /// <summary>
        /// Updates the token overlay on the color image
        /// </summary>
        private void UpdateTokenOverlay()
        {
            // Clear existing overlays
            TokenOverlayCanvas.Children.Clear();

            if (!trackTokens || detectedTokens.Count == 0)
                return;

            // Scale factors for mapping between depth and color spaces
            double scaleX = TokenOverlayCanvas.ActualWidth / depthWidth;
            double scaleY = TokenOverlayCanvas.ActualHeight / depthHeight;

            // Debug the scaling calculation
            Console.WriteLine($"Canvas Size: {TokenOverlayCanvas.ActualWidth}x{TokenOverlayCanvas.ActualHeight}, Depth Size: {depthWidth}x{depthHeight}");
            Console.WriteLine($"Scale factors: {scaleX}x{scaleY}");

            // Add overlays for each token
            foreach (var token in detectedTokens)
            {
                try
                {
                    // Create an ellipse for the token
                    Ellipse tokenEllipse = new Ellipse
                    {
                        Width = token.DiameterPixels * scaleX,
                        Height = token.DiameterPixels * scaleY,
                        Stroke = new SolidColorBrush(GetTokenTypeColor(token.Type)),
                        StrokeThickness = 2
                    };

                    // Position the ellipse
                    double left = (token.Position.X - token.DiameterPixels / 2) * scaleX;
                    double top = (token.Position.Y - token.DiameterPixels / 2) * scaleY;

                    Canvas.SetLeft(tokenEllipse, left);
                    Canvas.SetTop(tokenEllipse, top);

                    // Add to canvas
                    TokenOverlayCanvas.Children.Add(tokenEllipse);

                    // Add label if enabled
                    if (showTokenLabels)
                    {
                        // Create a label with additional info
                        string labelText = GetTokenLabel(token);
                        TextBlock label = new TextBlock
                        {
                            Text = labelText,
                            Foreground = new SolidColorBrush(Colors.White),
                            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                            Padding = new Thickness(4),
                            FontSize = 12,
                            FontWeight = FontWeights.Bold
                        };

                        // Let the textblock measure itself with the content before positioning
                        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                        // Position the label
                        double labelLeft = token.Position.X * scaleX - label.DesiredSize.Width / 2;
                        double labelTop = (token.Position.Y - token.DiameterPixels / 2 - 25) * scaleY;

                        Canvas.SetLeft(label, labelLeft);
                        Canvas.SetTop(label, labelTop);

                        // Add to canvas
                        TokenOverlayCanvas.Children.Add(label);
                    }

                    // Add a center point for the token (helps with debugging)
                    Ellipse centerPoint = new Ellipse
                    {
                        Width = 6,
                        Height = 6,
                        Fill = new SolidColorBrush(Colors.Yellow)
                    };

                    Canvas.SetLeft(centerPoint, token.Position.X * scaleX - 3);
                    Canvas.SetTop(centerPoint, token.Position.Y * scaleY - 3);

                    TokenOverlayCanvas.Children.Add(centerPoint);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error drawing token overlay: {ex.Message}");
                }
            }

            // Debug display
            StatusText = $"Updated overlay for {detectedTokens.Count} tokens";
        }

        /// <summary>
        /// Gets a label for the specified token
        /// </summary>
        private string GetTokenLabel(TTRPGToken token)
        {
            string typeLabel = token.Type != TokenType.Unknown ?
                $"{token.Type}" : "Token";

            return $"{typeLabel}: {token.HeightMm}mm × {token.DiameterPixels:F0}px";
        }

        /// <summary>
        /// Gets a color for the specified token type
        /// </summary>
        private Color GetTokenTypeColor(TokenType type)
        {
            switch (type)
            {
                case TokenType.SmallToken:
                    return Colors.Yellow;
                case TokenType.MediumToken:
                    return Colors.Orange;
                case TokenType.LargeToken:
                    return Colors.Red;
                case TokenType.Miniature:
                    return Colors.LimeGreen;
                case TokenType.Dice:
                    return Colors.Cyan;
                case TokenType.Custom:
                    return Colors.Magenta;
                default:
                    return Colors.White;
            }
        }

        /// <summary>
        /// Gets a label for the specified token
        /// </summary>
        private string GetTokenLabelOld(TTRPGToken token)
        {
            string typeLabel = token.Type != TokenType.Unknown ?
                $"{token.Type}" : "Token";

            return $"{typeLabel}: {token.HeightMm}mm";
        }
    }
}