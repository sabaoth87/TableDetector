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
using System.Windows.Media;

namespace TableDetector
{
    /// <summary>
    /// Extensions to the MainWindow class that handle WebSocket communication with FoundryVTT
    /// </summary>
    public partial class MainWindow
    {
        // WebSocket server fields
        private HttpListener httpListener;
        private List<WebSocket> activeConnections = new List<WebSocket>();
        private bool isWebSocketServerRunning = false;
        private int webSocketPort = 8080;
        private CancellationTokenSource webSocketCts;
        private System.Timers.Timer tokenUpdateTimer;
        private bool autoSyncToFoundry = true;
        private bool enableCompression = true;
        private int messageSendCount = 0;

        /// <summary>
        /// Start the WebSocket server
        /// </summary>
        private async Task StartWebSocketServer()
        {
            try
            {
                webSocketCts = new CancellationTokenSource();
                httpListener = new HttpListener();
                httpListener.Prefixes.Add($"http://*:{webSocketPort}/");

                httpListener.Start();
                isWebSocketServerRunning = true;

                // Setup token update timer
                tokenUpdateTimer = new System.Timers.Timer(100); // 10 updates per second
                tokenUpdateTimer.Elapsed += async (s, e) => await SendTokenUpdatesToClients();
                tokenUpdateTimer.Start();

                this.Dispatcher.Invoke(() => {
                    StatusText = $"WebSocket server started on port {webSocketPort}";
                });

                // Main connection acceptance loop
                while (!webSocketCts.Token.IsCancellationRequested)
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
                            // Handle HTTP requests with a status page
                            ServeStatusPage(context);
                        }
                    }
                    catch (HttpListenerException)
                    {
                        // Listener was closed
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // Operation was canceled
                        break;
                    }
                    catch (Exception ex)
                    {
                        this.Dispatcher.Invoke(() => {
                            StatusText = $"WebSocket error: {ex.Message}";
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                this.Dispatcher.Invoke(() => {
                    StatusText = $"Failed to start WebSocket server: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Stop the WebSocket server
        /// </summary>
        private void StopWebSocketServer()
        {
            try
            {
                // Stop timer
                tokenUpdateTimer?.Stop();
                tokenUpdateTimer?.Dispose();
                tokenUpdateTimer = null;

                // Cancel operations
                webSocketCts?.Cancel();
                webSocketCts?.Dispose();
                webSocketCts = null;

                // Close all connections
                List<WebSocket> connectionsToClose;
                lock (activeConnections)
                {
                    connectionsToClose = new List<WebSocket>(activeConnections);
                    activeConnections.Clear();
                }

                foreach (var socket in connectionsToClose)
                {
                    try
                    {
                        socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                            "Server shutting down", CancellationToken.None).Wait(1000);
                        socket.Dispose();
                    }
                    catch { /* Best effort cleanup */ }
                }

                // Stop listener
                httpListener?.Close();
                httpListener = null;

                isWebSocketServerRunning = false;

                this.Dispatcher.Invoke(() => {
                    StatusText = "WebSocket server stopped";
                });
            }
            catch (Exception ex)
            {
                this.Dispatcher.Invoke(() => {
                    StatusText = $"Error stopping WebSocket server: {ex.Message}";
                });
            }
        }

        /// <summary>
        /// Process an incoming WebSocket connection request
        /// </summary>
        private async void ProcessWebSocketRequest(HttpListenerContext context)
        {
            WebSocketContext webSocketContext = null;

            try
            {
                // Accept WebSocket connection
                webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
                var webSocket = webSocketContext.WebSocket;

                // Add to active connections
                lock (activeConnections)
                {
                    activeConnections.Add(webSocket);
                }

                // Update UI and log
                this.Dispatcher.Invoke(() => {
                    StatusText = $"FoundryVTT client connected. Total connections: {activeConnections.Count}";
                });

                // Send initial full token data
                await SendInitialDataToClient(webSocket);

                // Keep connection alive and handle incoming messages
                var buffer = new byte[4096];
                WebSocketReceiveResult receiveResult;

                // Receive messages until connection closes
                do
                {
                    try
                    {
                        receiveResult = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), webSocketCts.Token);

                        if (!receiveResult.CloseStatus.HasValue)
                        {
                            // Process message
                            string message = Encoding.UTF8.GetString(buffer, 0, receiveResult.Count);
                            ProcessIncomingMessage(message, webSocket);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Operation was canceled
                        break;
                    }
                    catch (WebSocketException)
                    {
                        // WebSocket error
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error receiving message: {ex.Message}");
                        break;
                    }
                }
                while (webSocket.State == WebSocketState.Open && !webSocketCts.Token.IsCancellationRequested);

                // Close connection gracefully
                try
                {
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Connection closed by server",
                            CancellationToken.None);
                    }
                }
                catch { /* Ignore errors during close */ }
            }
            catch (Exception ex)
            {
                this.Dispatcher.Invoke(() => {
                    StatusText = $"WebSocket connection error: {ex.Message}";
                });
            }
            finally
            {
                // Clean up connection
                if (webSocketContext != null)
                {
                    var webSocket = webSocketContext.WebSocket;

                    lock (activeConnections)
                    {
                        activeConnections.Remove(webSocket);
                    }

                    this.Dispatcher.Invoke(() => {
                        StatusText = $"FoundryVTT client disconnected. Remaining connections: {activeConnections.Count}";
                    });

                    webSocket.Dispose();
                }
            }
        }

        /// <summary>
        /// Send initial full token data to a new client
        /// </summary>
        private async Task SendInitialDataToClient(WebSocket webSocket)
        {
            try
            {
                if (webSocket.State != WebSocketState.Open)
                    return;

                // Prepare initial data packet with everything the client needs
                var initialData = new
                {
                    type = "initialData",
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    status = hasValidROI && hasValidTableDepth ? "ready" : "notReady",
                    tableDepth = tableDepth,
                    hasValidROI = hasValidROI,
                    hasValidTableDepth = hasValidTableDepth,
                    roi = new
                    {
                        x = (int)detectedTableROI.X,
                        y = (int)detectedTableROI.Y,
                        width = (int)detectedTableROI.Width,
                        height = (int)detectedTableROI.Height
                    },
                    settings = new
                    {
                        isAngledView = isAngledView,
                        tokenDetectionThreshold = tokenDetectionThreshold,
                        miniDetectionSensitivity = miniDetectionSensitivity,
                        maxMiniatureHeight = maxMiniatureHeight,
                        tokenUpdateInterval = tokenUpdateInterval.TotalMilliseconds
                    },
                    tokens = detectedTokens.Select(t => CreateTokenObjectForJson(t)).ToArray()
                };

                string json = JsonSerializer.Serialize(initialData);

                // Send data
                byte[] messageBytes = Encoding.UTF8.GetBytes(json);
                await webSocket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    webSocketCts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending initial data: {ex.Message}");
            }
        }

        /// <summary>
        /// Process incoming messages from clients
        /// </summary>
        private void ProcessIncomingMessage(string message, WebSocket senderSocket)
        {
            try
            {
                // Parse JSON message
                using (JsonDocument doc = JsonDocument.Parse(message))
                {
                    var root = doc.RootElement;

                    // Check for command property
                    if (root.TryGetProperty("command", out var cmdElement))
                    {
                        string command = cmdElement.GetString();

                        switch (command)
                        {
                            case "requestTokenUpdate":
                                // Client is requesting fresh token data
                                SendTokenUpdatesToClient(senderSocket).Wait();
                                break;

                            case "updateSettings":
                                HandleSettingsUpdateRequest(root);
                                break;

                            case "updateToken":
                                HandleTokenUpdateRequest(root);
                                break;

                            case "requestRoi":
                                // Client is requesting to enter ROI selection mode
                                this.Dispatcher.Invoke(() => {
                                    ToggleCalibrationMode();
                                });
                                break;

                            case "calibrate":
                                // Client is requesting system calibration
                                HandleCalibrationRequest(root);
                                break;

                            case "ping":
                                // Send pong response
                                SendPongResponse(senderSocket).Wait();
                                break;

                            default:
                                Console.WriteLine($"Unknown command: {command}");
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing message: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle settings update request from client
        /// </summary>
        private void HandleSettingsUpdateRequest(JsonElement requestData)
        {
            try
            {
                bool settingsChanged = false;

                // Check each setting that can be updated
                if (requestData.TryGetProperty("autoSync", out var autoSyncElement))
                {
                    autoSyncToFoundry = autoSyncElement.GetBoolean();
                    settingsChanged = true;
                }

                if (requestData.TryGetProperty("tokenThreshold", out var thresholdElement))
                {
                    tokenDetectionThreshold = Math.Max(3, Math.Min(50, thresholdElement.GetInt32()));
                    settingsChanged = true;
                }

                if (requestData.TryGetProperty("miniatureSensitivity", out var sensitivityElement))
                {
                    miniDetectionSensitivity = Math.Max(3, Math.Min(30, sensitivityElement.GetInt32()));
                    settingsChanged = true;
                }

                if (requestData.TryGetProperty("angledView", out var angledViewElement))
                {
                    isAngledView = angledViewElement.GetBoolean();
                    HandleCameraAngleChange();
                    settingsChanged = true;
                }

                if (requestData.TryGetProperty("updateInterval", out var intervalElement))
                {
                    double intervalMs = Math.Max(50, Math.Min(1000, intervalElement.GetDouble()));
                    tokenUpdateInterval = TimeSpan.FromMilliseconds(intervalMs);
                    settingsChanged = true;

                    // Update timer interval if running
                    if (tokenUpdateTimer != null)
                    {
                        tokenUpdateTimer.Interval = intervalMs;
                    }
                }

                // Save settings if changed
                if (settingsChanged)
                {
                    this.Dispatcher.Invoke(() => {
                        AutoSaveSettings("Remote Settings Update");
                        StatusText = "Settings updated from Foundry VTT";
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle token update request from client
        /// </summary>
        private void HandleTokenUpdateRequest(JsonElement requestData)
        {
            try
            {
                // Extract token ID and updated properties
                if (requestData.TryGetProperty("tokenId", out var tokenIdElement))
                {
                    string tokenId = tokenIdElement.GetString();

                    // Find matching token
                    var token = detectedTokens.FirstOrDefault(t => t.Id.ToString() == tokenId);
                    if (token != null)
                    {
                        bool tokenUpdated = false;

                        // Update token label
                        if (requestData.TryGetProperty("label", out var labelElement))
                        {
                            token.Label = labelElement.GetString();
                            tokenUpdated = true;
                        }

                        // Update token type
                        if (requestData.TryGetProperty("type", out var typeElement))
                        {
                            string typeString = typeElement.GetString();
                            if (Enum.TryParse<TokenType>(typeString, true, out TokenType type))
                            {
                                token.Type = type;
                                tokenUpdated = true;
                            }
                        }

                        // Update token color
                        if (requestData.TryGetProperty("color", out var colorElement))
                        {
                            string colorHex = colorElement.GetString();
                            if (colorHex.StartsWith("#") && colorHex.Length == 7)
                            {
                                try
                                {
                                    var color = System.Windows.Media.Color.FromRgb(
                                        Convert.ToByte(colorHex.Substring(1, 2), 16),
                                        Convert.ToByte(colorHex.Substring(3, 2), 16),
                                        Convert.ToByte(colorHex.Substring(5, 2), 16));

                                    token.Color = color;
                                    tokenUpdated = true;
                                }
                                catch { /* Invalid color format */ }
                            }
                        }

                        // Update UI if token was updated
                        if (tokenUpdated)
                        {
                            this.Dispatcher.Invoke(() => {
                                UpdateTokenOverlay();
                                StatusText = $"Token {token.Id} updated from Foundry VTT";
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating token: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle calibration request from client
        /// </summary>
        private void HandleCalibrationRequest(JsonElement requestData)
        {
            try
            {
                string calibrationType = "full";

                // Get calibration type
                if (requestData.TryGetProperty("type", out var typeElement))
                {
                    calibrationType = typeElement.GetString();
                }

                switch (calibrationType)
                {
                    case "tableDepth":
                        // Force table depth detection
                        tableDepthLocked = false;
                        depthHistory.Clear();

                        this.Dispatcher.Invoke(() => {
                            FindLargestSurface_Click(null, null);
                        });
                        break;

                    case "roi":
                        // Enter ROI selection mode
                        this.Dispatcher.Invoke(() => {
                            ToggleCalibrationMode();
                        });
                        break;

                    case "full":
                    default:
                        // Full system calibration
                        tableDepthLocked = false;
                        depthHistory.Clear();

                        this.Dispatcher.Invoke(() => {
                            FindLargestSurface_Click(null, null);

                            // Wait a moment then enter ROI selection
                            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => {
                                this.Dispatcher.Invoke(() => {
                                    ToggleCalibrationMode();
                                });
                            });
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling calibration request: {ex.Message}");
            }
        }

        /// <summary>
        /// Send ping response
        /// </summary>
        private async Task SendPongResponse(WebSocket socket)
        {
            try
            {
                if (socket.State != WebSocketState.Open)
                    return;

                var pongResponse = new
                {
                    type = "pong",
                    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                };

                string json = JsonSerializer.Serialize(pongResponse);
                byte[] messageBytes = Encoding.UTF8.GetBytes(json);

                await socket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending pong: {ex.Message}");
            }
        }

        /// <summary>
        /// Send token updates to all connected clients
        /// </summary>
        private async Task SendTokenUpdatesToClients()
        {
            if (!isWebSocketServerRunning || !autoSyncToFoundry || activeConnections.Count == 0)
                return;

            // Get snapshot of current connections
            WebSocket[] connections;
            lock (activeConnections)
            {
                connections = activeConnections.ToArray();
            }

            if (connections.Length == 0)
                return;

            try
            {
                // Prepare token update data
                string json = CreateTokenUpdateJson();
                byte[] messageBytes = Encoding.UTF8.GetBytes(json);

                // Send to all connected clients
                List<Task> sendTasks = new List<Task>();
                foreach (var socket in connections)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        sendTasks.Add(socket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None));
                    }
                }

                // Wait for all sends to complete
                if (sendTasks.Count > 0)
                {
                    await Task.WhenAll(sendTasks);
                    messageSendCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending token updates: {ex.Message}");
            }
        }

        /// <summary>
        /// Send token update to a specific client
        /// </summary>
        private async Task SendTokenUpdatesToClient(WebSocket socket)
        {
            if (socket.State != WebSocketState.Open)
                return;

            try
            {
                // Prepare token update data
                string json = CreateTokenUpdateJson();
                byte[] messageBytes = Encoding.UTF8.GetBytes(json);

                // Send to the client
                await socket.SendAsync(
                    new ArraySegment<byte>(messageBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending token update to client: {ex.Message}");
            }
        }

        /// <summary>
        /// Create JSON string with token update data
        /// </summary>
        private string CreateTokenUpdateJson()
        {
            // Create token update object
            var tokenUpdate = new
            {
                type = "tokenUpdate",
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sequence = messageSendCount,
                tableDepth = tableDepth,
                status = hasValidROI && hasValidTableDepth ? "ready" : "notReady",
                tokens = detectedTokens.Select(t => CreateTokenObjectForJson(t)).ToArray()
            };

            return JsonSerializer.Serialize(tokenUpdate);
        }

        /// <summary>
        /// Create a JSON-friendly token object
        /// </summary>
        private object CreateTokenObjectForJson(TTRPGToken token)
        {
            // Convert to a format appropriate for JSON serialization
            return new
            {
                id = token.Id.ToString(),
                name = !string.IsNullOrEmpty(token.Label) ? token.Label : GetTokenTypeString(token.Type),
                // Convert to Foundry grid units - assuming 1 grid = 1 inch and using meters as our base unit
                x = token.RealWorldPosition.X * 39.37, // Convert meters to inches
                y = token.RealWorldPosition.Y * 39.37, // Convert meters to inches
                z = token.RealWorldPosition.Z * 39.37, // Convert meters to inches
                elevation = 0,
                size = GetSizeForFoundry(token),
                type = token.Type.ToString(),
                // Additional metadata
                heightMm = token.HeightMm,
                diameterMm = token.DiameterMeters * 1000, // Convert meters to mm
                depthMm = token.Depth,
                // Include properties for visualization in Foundry
                isHumanoid = token.Type == TokenType.Miniature,
                colorHex = GetTokenHexColor(token),
                // Include actor information from color detection if available
                actorCategory = enableColorDetection ? token.ActorCategory : "Unknown",
                actorType = enableColorDetection ? token.ActorType : "unknown",
                isHostile = enableColorDetection ? token.IsHostile : false,
                // Include grid mapping information if available
                hasGridPosition = isGridMappingActive && token.FoundryPosition != new Point(0, 0),
                gridX = isGridMappingActive ? token.FoundryPosition.X : 0,
                gridY = isGridMappingActive ? token.FoundryPosition.Y : 0
            };
        }

        /// <summary>
        /// Convert token color to hex string
        /// </summary>
        private string GetTokenHexColor(TTRPGToken token)
        {
            if (enableColorDetection && token.Color != Colors.Gray)
            {
                // Use detected color
                return $"#{token.Color.R:X2}{token.Color.G:X2}{token.Color.B:X2}";
            }
            else
            {
                // Use type-based color
                var typeColor = GetTokenTypeColor(token.Type);
                return $"#{typeColor.R:X2}{typeColor.G:X2}{typeColor.B:X2}";
            }
        }

        /// <summary>
        /// Determine appropriate size for Foundry VTT
        /// </summary>
        private double GetSizeForFoundry(TTRPGToken token)
        {
            // Calculate size based on Foundry's grid units (1 = 1 grid square)
            switch (token.Type)
            {
                case TokenType.SmallToken:
                    return 1.0;

                case TokenType.MediumToken:
                    return token.DiameterMeters >= 0.05 ? 1.0 : 0.5; // 5cm threshold

                case TokenType.LargeToken:
                    return token.DiameterMeters >= 0.075 ? 2.0 : 1.0; // 7.5cm threshold

                case TokenType.Miniature:
                    // Calculate size based on base diameter
                    double inchDiameter = token.DiameterMeters * 39.37; // Convert to inches

                    // D&D size categories
                    if (inchDiameter < 0.75) return 0.5; // Tiny (less than 0.75")
                    if (inchDiameter < 1.5) return 1.0; // Small/Medium (0.75"-1.5")
                    if (inchDiameter < 3.0) return 2.0; // Large (1.5"-3")
                    if (inchDiameter < 4.0) return 3.0; // Huge (3"-4")
                    return 4.0; // Gargantuan (4"+)

                case TokenType.Dice:
                    return 0.5; // Dice are typically small

                default:
                    return 1.0; // Default to medium
            }
        }

        /// <summary>
        /// Serve a status page for HTTP requests
        /// </summary>
        private void ServeStatusPage(HttpListenerContext context)
        {
            try
            {
                string responseHtml = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>TableDetector Status</title>
                    <style>
                        body {{ font-family: Arial, sans-serif; margin: 0; padding: 20px; background: #f0f0f0; }}
                        .container {{ max-width: 800px; margin: 0 auto; background: white; padding: 20px; border-radius: 5px; box-shadow: 0 2px 5px rgba(0,0,0,0.1); }}
                        h1 {{ color: #333; }}
                        .status {{ padding: 10px; margin: 10px 0; border-radius: 3px; }}
                        .status.ok {{ background: #d4edda; color: #155724; }}
                        .status.warning {{ background: #fff3cd; color: #856404; }}
                        .status.error {{ background: #f8d7da; color: #721c24; }}
                        table {{ width: 100%; border-collapse: collapse; margin: 20px 0; }}
                        th, td {{ padding: 8px; text-align: left; border-bottom: 1px solid #ddd; }}
                        th {{ background-color: #f2f2f2; }}
                    </style>
                </head>
                <body>
                    <div class='container'>
                        <h1>TableDetector Status</h1>
                        <div class='status {(isWebSocketServerRunning ? "ok" : "error")}'>
                            WebSocket Server: {(isWebSocketServerRunning ? "Running" : "Stopped")}
                        </div>
                        <div class='status {(hasValidROI && hasValidTableDepth ? "ok" : "warning")}'>
                            System Status: {(hasValidROI && hasValidTableDepth ? "Ready" : "Not Ready")}
                            {(!hasValidROI ? " - No valid ROI defined" : "")}
                            {(!hasValidTableDepth ? " - No valid table depth detected" : "")}
                        </div>
                        
                        <h2>Connection Information</h2>
                        <p>WebSocket URL: <code>ws://{context.Request.LocalEndPoint}/</code></p>
                        <p>Connected Clients: {activeConnections.Count}</p>
                        
                        <h2>System Information</h2>
                        <table>
                            <tr><th>Table Depth</th><td>{tableDepth} mm</td></tr>
                            <tr><th>ROI</th><td>{(hasValidROI ? $"{detectedTableROI.Width}x{detectedTableROI.Height}" : "Not defined")}</td></tr>
                            <tr><th>Detected Tokens</th><td>{detectedTokens.Count}</td></tr>
                            <tr><th>Camera Mode</th><td>{(isAngledView ? "Angled View" : "Overhead View")}</td></tr>
                            <tr><th>Updates Sent</th><td>{messageSendCount}</td></tr>
                        </table>
                        
                        <h2>Token Information</h2>
                        <table>
                            <tr>
                                <th>ID</th>
                                <th>Type</th>
                                <th>Height</th>
                                <th>Diameter</th>
                            </tr>
                            {string.Join("", detectedTokens.Take(10).Select(t => $@"
                                <tr>
                                    <td>{t.Id.ToString().Substring(0, 8)}...</td>
                                    <td>{t.Type}</td>
                                    <td>{t.HeightMm} mm</td>
                                    <td>{(t.DiameterMeters * 1000):F1} mm</td>
                                </tr>
                            "))}
                            {(detectedTokens.Count > 10 ? $"<tr><td colspan='4'>...and {detectedTokens.Count - 10} more</td></tr>" : "")}
                        </table>
                        
                        <p><small>Last updated: {DateTime.Now}</small></p>
                    </div>
                </body>
                </html>";

                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentType = "text/html";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.StatusCode = 200;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                string errorHtml = $"<html><body><h1>Error</h1><p>{ex.Message}</p></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(errorHtml);
                context.Response.ContentType = "text/html";
                context.Response.ContentLength64 = buffer.Length;
                context.Response.StatusCode = 500;
                context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                context.Response.Close();
            }
        }

        /// <summary>
        /// Add WebSocket server controls to UI
        /// </summary>
        private void AddWebSocketServerControls()
        {
            this.Dispatcher.Invoke(() => {
                var panel = FindName("AdvancedFeaturesPanel") as StackPanel;
                if (panel != null)
                {
                    var wsButton = new Button
                    {
                        Content = isWebSocketServerRunning ? "Stop WebSocket" : "Start WebSocket",
                        Padding = new Thickness(5, 2, 5, 2),
                        Margin = new Thickness(10, 0, 0, 0),
                        ToolTip = "Start/Stop WebSocket server for Foundry VTT integration"
                    };

                    wsButton.Click += async (s, e) => {
                        if (isWebSocketServerRunning)
                        {
                            StopWebSocketServer();
                            wsButton.Content = "Start WebSocket";
                        }
                        else
                        {
                            await StartWebSocketServer();
                            wsButton.Content = "Stop WebSocket";
                        }
                    };

                    panel.Children.Add(wsButton);

                    // Auto-start if enabled in settings
                    if (GetWebSocketAutoStartSetting())
                    {
                        StartWebSocketServer().ContinueWith(_ => {
                            this.Dispatcher.Invoke(() => {
                                wsButton.Content = "Stop WebSocket";
                            });
                        });
                    }
                }
            });
        }

        /// <summary>
        /// Get WebSocket auto-start setting
        /// </summary>
        private bool GetWebSocketAutoStartSetting()
        {
            // Default: auto-start disabled
            return false;
        }
    }
}