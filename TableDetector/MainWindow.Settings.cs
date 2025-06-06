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

        public object maxMiniatureHeight { get; private set; }
        public object miniDetectionSensitivity { get; private set; }
        public object miniatureBaseThreshold { get; private set; }
        private int gridCellSize = 20; // Size of each grid cell in pixels
        private bool showHeightGrid = false;
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

        private void OnPropertyChanged(string v)
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        /// Helper method to auto-save settings when important settings are changed
        /// </summary>
        private void AutoSaveSettings(string settingName = null)
        {
            try
            {
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
                
                // Create a new settings file
                SaveSettings();


                StatusText = "All settings reset to default values";
            }
            catch (Exception ex)
            {
                StatusText = $"Error resetting settings: {ex.Message}";
                MessageBox.Show($"Error resetting settings: {ex.Message}", "Reset Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new
                {
                    TokenTracking = new
                    {
                        MaxMiniatureHeight = maxMiniatureHeight,
                        MiniDetectionSensitivity = miniDetectionSensitivity,
                        MiniatureBaseThreshold = miniatureBaseThreshold
                    },
                    TableROI = new
                    {
                        X = roiRect.X,
                        Y = roiRect.Y,
                        Width = roiRect.Width,
                        Height = roiRect.Height,
                        IsValid = true
                    },
                    HeightGrid = new
                    {
                        Enabled = showHeightGrid,
                        CellSize = gridCellSize,
                        MinHeight = 0,
                        MaxHeight = 100,
                        ColorScheme = "GreenRed",
                        ShowValues = true
                    },
                    Version = "1.2",
                    LastSaved = DateTime.Now
                };

                string json = System.Text.Json.JsonSerializer.Serialize(settings,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(settingsFilePath, json);

                StatusText = $"Settings saved to {settingsFilePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving settings: {ex.Message}";
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFilePath))
                {
                    StatusText = "No saved settings found. Using defaults.";
                    return;
                }

                string json = File.ReadAllText(settingsFilePath);

                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("TableROI", out var tableROI))
                    {
                        if (tableROI.TryGetProperty("IsValid", out var isValid) && isValid.GetBoolean())
                        {
                            int x = 0, y = 0, width = 0, height = 0;

                            if (tableROI.TryGetProperty("X", out var val)) x = val.GetInt32();
                            if (tableROI.TryGetProperty("Y", out val)) y = val.GetInt32();
                            if (tableROI.TryGetProperty("Width", out val)) width = val.GetInt32();
                            if (tableROI.TryGetProperty("Height", out val)) height = val.GetInt32();

                            roiRect = new Int32Rect(x, y, width, height);
                        }
                    }

                    if (root.TryGetProperty("HeightGrid", out var heightGrid))
                    {
                        if (heightGrid.TryGetProperty("Enabled", out var val)) showHeightGrid = val.GetBoolean();
                        if (heightGrid.TryGetProperty("CellSize", out val)) gridCellSize = val.GetInt32();
                    }
                }

                StatusText = "Settings loaded successfully";
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading settings: {ex.Message}";
            }
        }
    }
}
