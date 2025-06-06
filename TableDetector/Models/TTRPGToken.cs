using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Media3D;

namespace TableDetector
{
    /// <summary>
    /// Represents a TTRPG token detected on the table surface
    /// </summary>
    public class TTRPGToken
    {
        public double Confidence { get; set; } = 1.0;
        public int StabilityFrames { get; set; } = 1;
        /// <summary>
        /// Position of the token in the depth image (pixels)
        /// </summary>
        public Point Position { get; set; }

        /// <summary>
        /// Depth value of the token in the depth image (millimeters)
        /// </summary>
        public ushort Depth { get; set; }

        /// <summary>
        /// Height of the token above the table surface (millimeters)
        /// </summary>
        public ushort HeightMm { get; set; }

        /// <summary>
        /// Diameter of the token in the depth image (pixels)
        /// </summary>
        public double DiameterPixels { get; set; }

        /// <summary>
        /// Diameter of the token in real-world coordinates (meters)
        /// </summary>
        public double DiameterMeters { get; set; }

        /// <summary>
        /// List of points that make up the token in the depth image
        /// </summary>
        public List<Point> Points { get; set; } = new List<Point>();

        /// <summary>
        /// Position of the token in real-world coordinates (meters)
        /// </summary>
        public Point3D RealWorldPosition { get; set; }

        /// <summary>
        /// Unique ID for the token - can be used for tracking over time
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Time when the token was last detected
        /// </summary>
        public DateTime LastDetectedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// Color assigned to this token for visualization
        /// </summary>
        public System.Windows.Media.Color Color { get; set; } = System.Windows.Media.Colors.Gray;

        /// <summary>
        /// Label for the token (can be assigned by the user)
        /// </summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Token type identification based on physical characteristics
        /// </summary>
        public TokenType Type { get; set; } = TokenType.Unknown;

        /// <summary>
        /// Determines if the token is currently selected
        /// </summary>
        public bool IsSelected { get; set; } = false;

        /// <summary>
        /// History of positions for tracking movement
        /// </summary>
        public List<Point> PositionHistory { get; private set; } = new List<Point>();

        /// <summary>
        /// Maximum length of position history to maintain
        /// </summary>
        private const int MaxHistoryLength = 20;
        
        /// <summary>
        /// The category of the token based on color
        /// </summary>
        public string ActorCategory { get; set; } = "Unknown";

        /// <summary>
        /// The type of actor in the game
        /// </summary>
        public string ActorType { get; set; } = "unknown";

        /// <summary>
        /// Whether the token is hostile
        /// </summary>
        public bool IsHostile { get; set; } = false;

        /// <summary>
        /// Position in Foundry VTT grid coordinates after mapping
        /// </summary>
        public Point FoundryPosition { get; set; }

        /// <summary>
        /// Updates the token position and adds it to the history
        /// </summary>
        public void UpdatePosition(Point newPosition)
        {
            // Add current position to history before updating
            if (!Position.Equals(new Point(0, 0)))
            {
                PositionHistory.Add(Position);

                // Trim history if needed
                if (PositionHistory.Count > MaxHistoryLength)
                {
                    PositionHistory.RemoveAt(0);
                }
            }

            Position = newPosition;
            LastDetectedTime = DateTime.Now;
        }

        /// <summary>
        /// Calculates distance to another token (in pixels)
        /// </summary>
        public double DistanceTo(TTRPGToken other)
        {
            double dx = Position.X - other.Position.X;
            double dy = Position.Y - other.Position.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Calculates real-world distance to another token (in meters)
        /// </summary>
        public double RealWorldDistanceTo(TTRPGToken other)
        {
            double dx = RealWorldPosition.X - other.RealWorldPosition.X;
            double dy = RealWorldPosition.Y - other.RealWorldPosition.Y;
            double dz = RealWorldPosition.Z - other.RealWorldPosition.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>
    /// Enumerates possible token types based on physical characteristics
    /// </summary>
    public enum TokenType
    {
        Unknown,
        SmallToken,   // Small flat token (e.g., 1 inch)
        MediumToken,  // Medium flat token (e.g., 2 inch)
        LargeToken,   // Large flat token (e.g., 3+ inch)
        Miniature,    // Taller miniature figure
        Dice,         // Dice
        Custom        // User-defined type
    }
}