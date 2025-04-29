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

    }
}
