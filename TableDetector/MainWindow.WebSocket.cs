using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TableDetector
{
    public partial class MainWindow
    {
        // Fields for WebSocket server
        private HttpListener httpListener;
        private CancellationTokenSource webSocketCancellation;
        private List<WebSocket> activeConnections = new List<WebSocket>();
        private bool isWebSocketServerRunning = false;
        private int webSocketPort = 8080;
        private System.Threading.Timer tokenUpdateTimer;
        private bool autoSyncWithFoundry = false;
        private DateTime lastFoundryUpdate = DateTime.MinValue;
        private TimeSpan foundryUpdateInterval = TimeSpan.FromSeconds(0.5); // 500ms update rate

        /// <summary>
        /// Starts the WebSocket server for real-time Foundry VTT integration
        /// </summary>
        private async Task StartWebSocketServer()
        {
            try
            {
                if (isWebSocketServerRunning)
                    return;

                webSocketCancellation = new CancellationTokenSource();
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://localhost:{webSocketPort}/");
                httpListener.Prefixes.Add($"http://127.0.0.1:{webSocketPort}/");

                // Add local IP addresses to allow connections from other devices on network
                var hostName = Dns.GetHostName();
                var hostAddresses = Dns.GetHostAddresses(hostName);
                foreach (var address in hostAddresses)
                {
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        httpListener.Prefixes.Add($"http://{address}:{webSocketPort}/");
                    }
                }

                httpListener.Start();
                isWebSocketServerRunning = true;

                // Setup timer for regular token updates
                tokenUpdateTimer = new System.Threading.Timer(
                    async (state) => await SendTokenUpdatesToClients(),
                    null,
                    TimeSpan.FromSeconds(1),
                    foundryUpdateInterval);

                StatusText = $"WebSocket server started on port {webSocketPort}";

                // Main connection acceptance loop
                while (!webSocketCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        var context = await httpListener.GetContextAsync();

                        if (context.Request.IsWebSocketRequest)
                        {
                            ProcessWebSocketRequest(context);
                        }
                        else
                        {
                            // Handle regular HTTP requests - could serve a simple status page
                            string responseString = "<html><body><h1>TableDetector WebSocket Server</h1><p>Status: Running</p></body></html>";
                            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                            context.Response.ContentLength64 = buffer.Length;
                            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                            context.Response.Close();
                        }
                    }
                    catch (HttpListenerException)
                    {
                        // Listener was closed
                        break;
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText = $"WebSocket error: {ex.Message}";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText = $"Failed to start WebSocket server: {ex.Message}";
                    MessageBox.Show($"Failed to start WebSocket server: {ex.Message}",
                        "WebSocket Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        /// <summary>
        /// Processes incoming WebSocket connection requests
        /// </summary>
        private async void ProcessWebSocketRequest(HttpListenerContext context)
        {
            WebSocketContext webSocketContext = null;

            try
            {
                webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                var webSocket = webSocketContext.WebSocket;

                // Add to active connections
                lock (activeConnections)
                {
                    activeConnections.Add(webSocket);
                }

                Dispatcher.Invoke(() =>
                {
                    StatusText = $"Foundry VTT client connected. Total connections: {activeConnections.Count}";
                });

                // Send initial token data
                await SendTokenDataToClient(webSocket);

                // Keep connection alive and handle incoming messages
                var buffer = new byte[4096];
                var receiveResult = await webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), webSocketCancellation.Token);

                while (!receiveResult.CloseStatus.HasValue)
                {
                    // Process any commands from Foundry
                    string message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                    ProcessIncomingMessage(message);

                    // Continue receiving
                    receiveResult = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer), webSocketCancellation.Token);
                }

                // Close connection gracefully
                await webSocket.CloseAsync(
                    receiveResult.CloseStatus.Value,
                    receiveResult.CloseStatusDescription,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Handle error
                Dispatcher.Invoke(() =>
                {
                    StatusText = $"WebSocket connection error: {ex.Message}";
                });
            }
            finally
            {
                // Remove from active connections
                if (webSocketContext != null)
                {
                    var webSocket = webSocketContext.WebSocket;

                    lock (activeConnections)
                    {
                        activeConnections.Remove(webSocket);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        StatusText = $"Foundry VTT client disconnected. Remaining connections: {activeConnections.Count}";
                    });

                    // Dispose if still open
                    if (webSocket.State == WebSocketState.Open)
                    {
                        webSocket.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Sends token data to a specific client
        /// </summary>
        private async Task SendTokenDataToClient(WebSocket webSocket)
        {
            if (webSocket.State != WebSocketState.Open)
                return;

            try
            {
                var tokenData = CreateTokenUpdateData();
                byte[] data = Encoding.UTF8.GetBytes(tokenData);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText = $"Error sending token data: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Sends token updates to all connected clients
        /// </summary>
        private async Task SendTokenUpdatesToClients()
        {
            if (!isWebSocketServerRunning || !autoSyncWithFoundry)
                return;

            // Only send updates at the specified interval
            if (DateTime.Now - lastFoundryUpdate < foundryUpdateInterval)
                return;

            lastFoundryUpdate = DateTime.Now;

            var tokenData = CreateTokenUpdateData();
            byte[] data = Encoding.UTF8.GetBytes(tokenData);

            // Copy the list of connections to avoid modification during iteration
            WebSocket[] connections;
            lock (activeConnections)
            {
                connections = activeConnections.ToArray();
            }

            foreach (var webSocket in connections)
            {
                if (webSocket.State != WebSocketState.Open)
                    continue;

                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    // Handle connection errors - will be cleaned up on next receive
                }
            }
        }

        /// <summary>
        /// Convert token type color to hex for Foundry VTT
        /// </summary>
        private string GetTokenHexColor(TokenType type)
        {
            System.Windows.Media.Color color = GetTokenTypeColor(type);
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// Improved helper method to determine appropriate size for Foundry
        /// </summary>
        private double GetSizeForFoundry(TTRPGToken token)
        {
            switch (token.Type)
            {
                case TokenType.SmallToken:
                    return 1.0;
                case TokenType.MediumToken:
                    return 1.0;
                case TokenType.LargeToken:
                    return 2.0;
                case TokenType.Miniature:
                    // More sophisticated size calculation for miniatures based on actual dimensions
                    double diameter = token.DiameterMeters * 39.37; // Convert to inches

                    // Standard D&D sizes: Tiny (0.5), Small (1), Medium (1), Large (2), Huge (3), Gargantuan (4)
                    if (diameter < 0.75) return 0.5; // Tiny
                    if (diameter < 1.5) return 1.0; // Small/Medium
                    if (diameter < 2.5) return 2.0; // Large
                    if (diameter < 3.5) return 3.0; // Huge
                    return 4.0; // Gargantuan

                case TokenType.Dice:
                    return 0.5; // Dice are usually small tokens
                default:
                    return 1.0;
            }
        }

        /// <summary>
        /// Processes incoming messages from Foundry VTT clients with enhanced functionality
        /// </summary>
        private void ProcessIncomingMessage(string message)
        {
            try
            {
                // Parse JSON message
                using (JsonDocument doc = JsonDocument.Parse(message))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("command", out var command))
                    {
                        string cmd = command.GetString();

                        // Handle different commands
                        switch (cmd)
                        {
                            case "requestTokens":
                                // Client is requesting a fresh token update
                                SendTokenUpdatesToClients().Wait();
                                break;

                            case "updateSettings":
                                // Client is updating settings
                                if (root.TryGetProperty("autoSync", out var autoSync))
                                {
                                    autoSyncWithFoundry = autoSync.GetBoolean();
                                }

                                // Add ability to adjust detection settings from Foundry
                                if (root.TryGetProperty("detectionSensitivity", out var sensitivity))
                                {
                                    miniDetectionSensitivity = Math.Max(3, Math.Min(30, sensitivity.GetInt32()));
                                }

                                if (root.TryGetProperty("miniatureHeight", out var height))
                                {
                                    maxMiniatureHeight = Math.Max(30, Math.Min(200, height.GetInt32()));
                                }
                                break;

                            case "assignLabel":
                                // Client is assigning a label to a token
                                if (root.TryGetProperty("tokenId", out var tokenId) &&
                                    root.TryGetProperty("label", out var label))
                                {
                                    var tokenIdStr = tokenId.GetString();
                                    var labelStr = label.GetString();

                                    // Find and update token
                                    var token = detectedTokens.FirstOrDefault(t => t.Id.ToString() == tokenIdStr);
                                    if (token != null)
                                    {
                                        token.Label = labelStr;
                                        Dispatcher.Invoke(() =>
                                        {
                                            UpdateTokenOverlay();
                                            StatusText = $"Updated token label: {labelStr}";
                                        });
                                    }
                                }
                                break;

                            case "requestStatus":
                                // Client is requesting system status
                                SendSystemStatusToClients().Wait();
                                break;

                            case "setTokenType":
                                // Client is setting a token type
                                if (root.TryGetProperty("tokenId", out var typeTokenId) &&
                                    root.TryGetProperty("type", out var typeValue))
                                {
                                    var tokenIdStr = typeTokenId.GetString();
                                    var typeStr = typeValue.GetString();

                                    // Parse the token type
                                    if (Enum.TryParse<TokenType>(typeStr, true, out TokenType parsedType))
                                    {
                                        // Find and update token
                                        var token = detectedTokens.FirstOrDefault(t => t.Id.ToString() == tokenIdStr);
                                        if (token != null)
                                        {
                                            token.Type = parsedType;
                                            Dispatcher.Invoke(() =>
                                            {
                                                UpdateTokenOverlay();
                                                StatusText = $"Updated token type: {typeStr}";
                                            });
                                        }
                                    }
                                }
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText = $"Error processing message: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Sends system status information to all connected clients
        /// </summary>
        private async Task SendSystemStatusToClients()
        {
            var statusData = new
            {
                type = "systemStatus",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                isReady = IsReadyForTokenDetection(),
                hasValidROI = hasValidROI,
                hasValidTableDepth = hasValidTableDepth,
                tableDepth = tableDepth,
                tokenCount = detectedTokens.Count,
                roi = new
                {
                    x = (int)detectedTableROI.X,
                    y = (int)detectedTableROI.Y,
                    width = (int)detectedTableROI.Width,
                    height = (int)detectedTableROI.Height
                },
                detection = new
                {
                    minTokenHeight = MIN_TOKEN_HEIGHT,
                    maxMiniatureHeight = maxMiniatureHeight,
                    sensitivity = miniDetectionSensitivity,
                    updateInterval = tokenUpdateInterval.TotalMilliseconds
                }
            };

            string json = JsonSerializer.Serialize(statusData);
            byte[] data = Encoding.UTF8.GetBytes(json);

            // Send to all connected clients
            WebSocket[] connections;
            lock (activeConnections)
            {
                connections = activeConnections.ToArray();
            }

            foreach (var webSocket in connections)
            {
                if (webSocket.State != WebSocketState.Open)
                    continue;

                try
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
                catch (Exception)
                {
                    // Handle connection errors - will be cleaned up on next receive
                }
            }
        }

        /// <summary>
        /// Creates JSON token update data for Foundry VTT
        /// </summary>
        private string CreateTokenUpdateData()
        {
            // Use the color-enhanced version if color detection is enabled
            if (enableColorDetection)
            {
                return CreateTokenUpdateDataWithColor();
            }

            // If grid mapping is active, use the mapped positions
            if (isGridMappingActive)
            {
                return CreateTokenUpdateDataWithMapping();
            }

            // Otherwise, use the original format
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

                return JsonSerializer.Serialize(statusUpdate);
            }

            // Create token data in Foundry VTT compatible format with improved metadata
            var tokenUpdate = new
            {
                type = "tokenUpdate",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                tableDepth = tableDepth,
                status = "active",
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
                    tokenColor = GetTokenHexColor(t.Type)
                }).ToArray()
            };

            return JsonSerializer.Serialize(tokenUpdate);
        }

        // If Mapping is enabled
        private string CreateTokenUpdateDataWithMapping()
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
                    tokenCount = 0,
                    gridMappingActive = isGridMappingActive
                };

                return JsonSerializer.Serialize(statusUpdate);
            }

            // Create token data with grid mapping included
            var tokenUpdate = new
            {
                type = "tokenUpdate",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                tableDepth = tableDepth,
                status = "active",
                gridMappingActive = isGridMappingActive,
                gridSettings = new
                {
                    physicalGridSize = currentGridMapping.PhysicalGridSize,
                    virtualGridSize = currentGridMapping.VirtualGridSize,
                    rotation = currentGridMapping.PhysicalRotation,
                    scale = currentGridMapping.VirtualScale
                },
                tokens = detectedTokens.Select(t => new
                {
                    id = t.Id.ToString(),
                    name = !string.IsNullOrEmpty(t.Label) ? t.Label : GetTokenTypeString(t.Type),
                    // Use the mapped position if available, otherwise use the standard position
                    x = isGridMappingActive ? t.FoundryPosition.X : (t.RealWorldPosition.X * 39.37),
                    y = isGridMappingActive ? t.FoundryPosition.Y : (t.RealWorldPosition.Y * 39.37),
                    // Include the original position for reference
                    originalX = t.RealWorldPosition.X * 39.37,
                    originalY = t.RealWorldPosition.Y * 39.37,
                    elevation = 0,
                    height = GetSizeForFoundry(t),
                    width = GetSizeForFoundry(t),
                    type = t.Type.ToString(),
                    // Additional metadata
                    heightMm = t.HeightMm,
                    diameterMm = t.DiameterMeters * 1000, // Convert meters to mm
                                                          // Include properties for visualization in Foundry
                    isHumanoid = t.Type == TokenType.Miniature,
                    tokenColor = GetTokenHexColor(t.Type)
                }).ToArray()
            };

            return JsonSerializer.Serialize(tokenUpdate);
        }

        /// <summary>
        /// Stops the WebSocket server
        /// </summary>
        private void StopWebSocketServer()
        {
            if (!isWebSocketServerRunning)
                return;

            try
            {
                // Cancel and dispose timer
                tokenUpdateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                tokenUpdateTimer?.Dispose();
                tokenUpdateTimer = null;

                // Signal cancellation to all tasks
                webSocketCancellation?.Cancel();

                // Close all active WebSocket connections
                WebSocket[] connections;
                lock (activeConnections)
                {
                    connections = activeConnections.ToArray();
                    activeConnections.Clear();
                }

                foreach (var webSocket in connections)
                {
                    try
                    {
                        if (webSocket.State == WebSocketState.Open)
                        {
                            // Try to close gracefully
                            webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                                "Server shutting down", CancellationToken.None).Wait(1000);
                        }
                        webSocket.Dispose();
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }

                // Stop the HTTP listener
                httpListener?.Close();
                httpListener = null;
                isWebSocketServerRunning = false;

                StatusText = "WebSocket server stopped";
            }
            catch (Exception ex)
            {
                StatusText = $"Error stopping WebSocket server: {ex.Message}";
            }
        }

        /// <summary>
        /// Toggle automatic synchronization with Foundry VTT
        /// </summary>
        private void ToggleFoundrySync(bool enable)
        {
            autoSyncWithFoundry = enable;

            if (enable)
            {
                StatusText = "Auto-sync with Foundry VTT enabled";
                // Ensure server is running
                if (!isWebSocketServerRunning)
                {
                    Task.Run(() => StartWebSocketServer());
                }
            }
            else
            {
                StatusText = "Auto-sync with Foundry VTT disabled";
            }
        }

        /// <summary>
        /// Add Foundry VTT integration UI to the main window
        /// </summary>
        private void AddFoundryVTTIntegrationUI()
        {
            var window = new Window
            {
                Title = "Foundry VTT Integration",
                Width = 500,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this
            };

            var panel = new StackPanel { Margin = new Thickness(15) };

            // Title
            panel.Children.Add(new TextBlock
            {
                Text = "Foundry VTT Integration Setup",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 15)
            });

            // Connection settings
            panel.Children.Add(new TextBlock
            {
                Text = "WebSocket Server Settings",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 5)
            });

            // Port selection
            var portPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 15) };
            portPanel.Children.Add(new TextBlock { Text = "Server Port:", VerticalAlignment = VerticalAlignment.Center });
            var portInput = new TextBox
            {
                Text = webSocketPort.ToString(),
                Width = 80,
                Margin = new Thickness(10, 0, 0, 0)
            };
            portPanel.Children.Add(portInput);
            panel.Children.Add(portPanel);

            // Server status and controls
            var serverStatusPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 15) };
            var serverStatusText = new TextBlock
            {
                Text = isWebSocketServerRunning ? "Server Status: Running" : "Server Status: Stopped",
                VerticalAlignment = VerticalAlignment.Center
            };
            serverStatusPanel.Children.Add(serverStatusText);

            var startServerButton = new Button
            {
                Content = isWebSocketServerRunning ? "Restart Server" : "Start Server",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(15, 0, 0, 0)
            };
            startServerButton.Click += async (s, e) =>
            {
            // Update port if changed
            if (int.TryParse(portInput.Text, out int newPort) && newPort >= 1024 && newPort <= 65535)
                {
                    webSocketPort = newPort;
                }

            // Stop existing server if running
            if (isWebSocketServerRunning)
                {
                    StopWebSocketServer();
                }

            // Start new server
            await StartWebSocketServer();
                serverStatusText.Text = "Server Status: Running";
                startServerButton.Content = "Restart Server";
            };
            serverStatusPanel.Children.Add(startServerButton);

            var stopServerButton = new Button
            {
                Content = "Stop Server",
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(10, 0, 0, 0),
                IsEnabled = isWebSocketServerRunning
            };
            stopServerButton.Click += (s, e) =>
            {
                StopWebSocketServer();
                serverStatusText.Text = "Server Status: Stopped";
                startServerButton.Content = "Start Server";
                stopServerButton.IsEnabled = false;
            };
            serverStatusPanel.Children.Add(stopServerButton);

            panel.Children.Add(serverStatusPanel);

            // Synchronization settings
            panel.Children.Add(new TextBlock
            {
                Text = "Synchronization Settings",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 5)
            });

            var autoSyncCheckbox = new CheckBox
            {
                Content = "Automatically sync tokens with Foundry VTT",
                IsChecked = autoSyncWithFoundry,
                Margin = new Thickness(0, 5, 0, 5)
            };
            autoSyncCheckbox.Checked += (s, e) => ToggleFoundrySync(true);
            autoSyncCheckbox.Unchecked += (s, e) => ToggleFoundrySync(false);
            panel.Children.Add(autoSyncCheckbox);

            // Update interval
            var updateIntervalPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            updateIntervalPanel.Children.Add(new TextBlock
            {
                Text = "Update Interval (ms):",
                VerticalAlignment = VerticalAlignment.Center
            });

            var updateIntervalInput = new TextBox
            {
                Text = foundryUpdateInterval.TotalMilliseconds.ToString(),
                Width = 80,
                Margin = new Thickness(10, 0, 0, 0)
            };
            updateIntervalPanel.Children.Add(updateIntervalInput);

            var applyIntervalButton = new Button
            {
                Content = "Apply",
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(10, 0, 0, 0)
            };
            applyIntervalButton.Click += (s, e) =>
            {
                if (double.TryParse(updateIntervalInput.Text, out double ms) && ms >= 100)
                {
                    foundryUpdateInterval = TimeSpan.FromMilliseconds(ms);
                    StatusText = $"Update interval set to {ms}ms";

                // Update timer interval if running
                if (tokenUpdateTimer != null)
                    {
                        tokenUpdateTimer.Change(TimeSpan.FromSeconds(1), foundryUpdateInterval);
                    }
                }
            };
            updateIntervalPanel.Children.Add(applyIntervalButton);
            panel.Children.Add(updateIntervalPanel);

            // Foundry module installation instructions
            panel.Children.Add(new TextBlock
            {
                Text = "Foundry VTT Module",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 15, 0, 5)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "To use this integration, install the TableDetector module in Foundry VTT. The module connects to this application to synchronize physical miniatures with digital tokens.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 5)
            });

            var connectionInfoPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            connectionInfoPanel.Children.Add(new TextBlock
            {
                Text = "Connection Information",
                FontWeight = FontWeights.Bold
            });

            // Get local IP address for connection info
            string localIp = "localhost";
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        localIp = ip.ToString();
                        break;
                    }
                }
            }
            catch { /* Use localhost if there's an error */ }

            var connectionUrlText = new TextBox
            {
                Text = $"ws://{localIp}:{webSocketPort}",
                IsReadOnly = true,
                Margin = new Thickness(0, 5, 0, 5)
            };
            connectionInfoPanel.Children.Add(connectionUrlText);

            var copyUrlButton = new Button
            {
                Content = "Copy Connection URL",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 10, 5)
            };
            copyUrlButton.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(connectionUrlText.Text);
                    StatusText = "Connection URL copied to clipboard";
                }
                catch (Exception ex)
                {
                    StatusText = $"Failed to copy: {ex.Message}";
                }
            };
            connectionInfoPanel.Children.Add(copyUrlButton);
            panel.Children.Add(connectionInfoPanel);

            // Close button
            var closeButton = new Button
            {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(0, 20, 0, 0)
            };
            closeButton.Click += (s, e) => window.Close();
            panel.Children.Add(closeButton);

            // Set content and show window
            var scrollViewer = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            window.Content = scrollViewer;
            window.ShowDialog();
        }

        // Add a button to the main UI to open the Foundry integration setup
        private void AddFoundryIntegrationButton()
        {
            // Find the parent panel in the UI
            var panel = this.FindName("TokenTrackingPanel") as StackPanel;
            if (panel == null)
                return;

            // Create the button
            var integrationButton = new Button
            {
                Content = "Foundry VTT Setup",
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(10, 0, 0, 0)
            };

            // Add click handler
            integrationButton.Click += (s, e) => AddFoundryVTTIntegrationUI();

            // Add to panel
            panel.Children.Add(integrationButton);
        }
    }
}