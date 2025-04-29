using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace TableDetector
{
    public partial class MainWindow
    {

        // Settings path for persistence
        private string settingsFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TableDetector",
            "settings.json");

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
                    depthThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;
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
                depthThreshold = isAngledView ? ANGLED_DEG_MAX : ANGLED_DEG_MIN;
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
                        MaxMiniatureHeight = maxMiniatureHeight,
                        TokenDetectionThreshold = tokenDetectionThreshold,
                        MiniDetectionSensitivity = miniDetectionSensitivity,
                        MiniatureBaseThreshold = miniatureBaseThreshold,
                        TrackTokens = trackTokens,
                        ShowTokenLabels = showTokenLabels,
                        TokenUpdateIntervalMs = tokenUpdateInterval.TotalMilliseconds
                    },
                    // Height Grid settings
                    HeightGrid = new
                    {
                        Enabled = showHeightGrid,
                        CellSize = gridCellSize,
                        MinHeight = 0, // Add class field if you implement min/max height ranges
                        MaxHeight = 100, // Add class field if you implement min/max height ranges
                        ColorScheme = "GreenRed", // Add class field if you implement color schemes
                        ShowValues = true // Add class field if you implement this setting
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
                    Version = "1.1",
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

                    // Height Grid settings
                    if (root.TryGetProperty("HeightGrid", out var heightGrid))
                    {
                        if (heightGrid.TryGetProperty("Enabled", out var val))
                            showHeightGrid = val.GetBoolean();

                        if (heightGrid.TryGetProperty("CellSize", out val))
                            gridCellSize = val.GetInt32();
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
                    
                    if (ShowHeightGridCheckBox != null)
                        ShowHeightGridCheckBox.IsChecked = showHeightGrid;

                    if (ShowHeightGridMenuItem != null)
                        ShowHeightGridMenuItem.IsChecked = showHeightGrid;

                    if (GridCellSizeSlider != null)
                        GridCellSizeSlider.Value = gridCellSize;
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

    }
}
