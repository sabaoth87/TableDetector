using System;
using System.Windows;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media.Media3D;
using System.Collections.Generic;

namespace TableDetector
{
    // Additional token tracking methods for the MainWindow class
    public partial class MainWindow
    {
        /// <summary>
        /// Performs temporal tracking of tokens between frames
        /// </summary>
        private void TrackTokensOverTime(List<TTRPGToken> previousTokens, List<TTRPGToken> currentTokens)
        {
            if (previousTokens.Count == 0)
                return;

            // For each current token, find the closest previous token
            foreach (var currentToken in currentTokens)
            {
                TTRPGToken closestToken = null;
                double minDistance = double.MaxValue;

                foreach (var prevToken in previousTokens)
                {
                    double distance = CalculateDistance(currentToken.Position, prevToken.Position);

                    // If this previous token is closer than any found so far
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closestToken = prevToken;
                    }
                }

                // If we found a close match (within a threshold)
                if (closestToken != null && minDistance < 30) // 30 pixels threshold
                {
                    // Copy identity and history from the previous token
                    currentToken.Id = closestToken.Id;
                    currentToken.Label = closestToken.Label;
                    currentToken.Type = closestToken.Type;
                    currentToken.IsSelected = closestToken.IsSelected;
                    currentToken.Color = closestToken.Color;

                    // Copy position history
                    currentToken.PositionHistory.Clear();
                    currentToken.PositionHistory.AddRange(closestToken.PositionHistory);

                    // Update the position history
                    currentToken.UpdatePosition(currentToken.Position);
                }
            }
        }

        /// <summary>
        /// Calculate the distance between two points
        /// </summary>
        private double CalculateDistance(Point p1, Point p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Exports the tokens to a format compatible with common VTT software
        /// </summary>
        private void ExportToVTT(string filePath)
        {
            try
            {
                // Create a structured object for VTT representation
                var vttExport = new
                {
                    SceneName = "Kinect Tabletop Scene",
                    GridSize = 1.0, // 1 inch grid
                    Objects = detectedTokens.Select(t => new
                    {
                        Id = t.Id.ToString(),
                        Name = t.Label.Length > 0 ? t.Label : GetTokenTypeString(t.Type),
                        Type = GetVTTObjectType(t.Type),
                        Position = new
                        {
                            X = t.RealWorldPosition.X * 39.37, // Convert to inches
                            Y = t.RealWorldPosition.Y * 39.37,
                            Z = t.RealWorldPosition.Z * 39.37
                        },
                        Size = new
                        {
                            Width = t.DiameterMeters * 39.37, // Convert to inches
                            Height = t.HeightMm / 25.4, // Convert mm to inches
                            Depth = t.DiameterMeters * 39.37
                        },
                        Color = t.Color.ToString(),
                        IsToken = IsTokenType(t.Type)
                    }).ToArray()
                };

                // Serialize to JSON
                string json = System.Text.Json.JsonSerializer.Serialize(vttExport,
                    new JsonSerializerOptions { WriteIndented = true });

                // Write to file
                File.WriteAllText(filePath, json);

                StatusText = $"VTT data exported to {filePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error exporting VTT data: {ex.Message}";
            }
        }

        /// <summary>
        /// Gets the VTT object type string for the token type
        /// </summary>
        private string GetVTTObjectType(TokenType type)
        {
            switch (type)
            {
                case TokenType.SmallToken:
                    return "token";
                case TokenType.MediumToken:
                    return "token";
                case TokenType.LargeToken:
                    return "token";
                case TokenType.Miniature:
                    return "figurine";
                case TokenType.Dice:
                    return "dice";
                case TokenType.Custom:
                    return "prop";
                default:
                    return "object";
            }
        }

        /// <summary>
        /// Gets a string representation of the token type
        /// </summary>
        private string GetTokenTypeString(TokenType type)
        {
            switch (type)
            {
                case TokenType.SmallToken:
                    return "Small Token";
                case TokenType.MediumToken:
                    return "Medium Token";
                case TokenType.LargeToken:
                    return "Large Token";
                case TokenType.Miniature:
                    return "Miniature";
                case TokenType.Dice:
                    return "Dice";
                case TokenType.Custom:
                    return "Custom Object";
                default:
                    return "Unknown Object";
            }
        }

        /// <summary>
        /// Determines if a token type should be treated as a game token in VTT
        /// </summary>
        private bool IsTokenType(TokenType type)
        {
            return type == TokenType.SmallToken ||
                   type == TokenType.MediumToken ||
                   type == TokenType.LargeToken;
        }

        /// <summary>
        /// Projects a 3D point to screen coordinates
        /// </summary>
        private Point ProjectToScreen(Point3D point3D)
        {
            // Simple projection - would need a proper camera matrix in production
            double focalLength = 525; // Approximate focal length for Kinect
            double x = (point3D.X * focalLength / point3D.Z) + (depthWidth / 2);
            double y = (point3D.Y * focalLength / point3D.Z) + (depthHeight / 2);

            return new Point(x, y);
        }

        /// <summary>
        /// Attempts to estimate the token 3D orientation
        /// </summary>
        private Matrix3D EstimateTokenOrientation(List<Point> tokenPoints, ushort[] depthData, int depthWidth)
        {
            // This is a simplified approach - a production system would use more sophisticated methods
            // such as principal component analysis on the point cloud

            if (tokenPoints.Count < 10)
                return Matrix3D.Identity;

            // Create a simple plane fit to the 3D points
            List<Point3D> points3D = new List<Point3D>();

            foreach (var point in tokenPoints)
            {
                int idx = (int)point.Y * depthWidth + (int)point.X;
                if (idx >= 0 && idx < depthData.Length)
                {
                    ushort depth = depthData[idx];
                    if (depth > 0)
                    {
                        // Convert to 3D point (simplified - would use coordinate mapper in production)
                        points3D.Add(new Point3D(
                            point.X - depthWidth / 2,
                            point.Y - depthHeight / 2,
                            depth));
                    }
                }
            }

            // For simplicity, we'll just return identity matrix
            // A real implementation would compute normal vector and create rotation matrix
            return Matrix3D.Identity;
        }

        /// <summary>
        /// Saves the detected tokens to a .tokens file format
        /// </summary>
        private async Task SaveTokensToFile(string filePath)
        {
            try
            {
                // Create a token data format
                var tokenData = new
                {
                    Version = "1.0",
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    TableDepth = tableDepth,
                    TableSurface = new
                    {
                        ROI_X = (int)detectedTableROI.X,
                        ROI_Y = (int)detectedTableROI.Y,
                        ROI_Width = (int)detectedTableROI.Width,
                        ROI_Height = (int)detectedTableROI.Height
                    },
                    Tokens = detectedTokens.Select(t => new
                    {
                        ID = t.Id.ToString(),
                        Type = t.Type.ToString(),
                        X = t.Position.X,
                        Y = t.Position.Y,
                        Height = t.HeightMm,
                        Diameter = t.DiameterPixels,
                        Label = t.Label
                    }).ToArray()
                };

                // Serialize to JSON
                string json = JsonSerializer.Serialize(tokenData,
                    new JsonSerializerOptions { WriteIndented = true });

                // Write to file asynchronously
                await Task.Run(() => File.WriteAllText(filePath, json));

                StatusText = $"Tokens saved to {filePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving tokens: {ex.Message}";
            }
        }

        /// <summary>
        /// Loads tokens from a .tokens file
        /// </summary>
        private async Task LoadTokensFromFile(string filePath)
        {
            try
            {
                string json = await Task.Run(() => File.ReadAllText(filePath));

                // Deserialize the JSON
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;

                    // Extract tokens
                    JsonElement tokensElement = root.GetProperty("Tokens");

                    // Clear existing tokens
                    detectedTokens.Clear();

                    // Load tokens
                    foreach (JsonElement tokenElement in tokensElement.EnumerateArray())
                    {
                        string id = tokenElement.GetProperty("ID").GetString();
                        string typeStr = tokenElement.GetProperty("Type").GetString();
                        double x = tokenElement.GetProperty("X").GetDouble();
                        double y = tokenElement.GetProperty("Y").GetDouble();
                        int height = tokenElement.GetProperty("Height").GetInt32();
                        double diameter = tokenElement.GetProperty("Diameter").GetDouble();
                        string label = tokenElement.GetProperty("Label").GetString();

                        // Parse token type
                        TokenType type = TokenType.Unknown;
                        Enum.TryParse(typeStr, out type);

                        // Create token object
                        TTRPGToken token = new TTRPGToken
                        {
                            Position = new Point(x, y),
                            HeightMm = (ushort)height,
                            DiameterPixels = diameter,
                            Type = type,
                            Label = label
                        };

                        // Set ID if valid
                        if (Guid.TryParse(id, out Guid tokenId))
                        {
                            // This would require modifying the TTRPGToken class to allow setting Id
                            // For now, we'll use the new Id generated in the constructor
                        }

                        detectedTokens.Add(token);
                    }
                }

                // Update token overlay
                UpdateTokenOverlay();

                TokenCountText = $"{detectedTokens.Count} tokens loaded";
                StatusText = $"Tokens loaded from {filePath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading tokens: {ex.Message}";
            }
        }
    }
}
