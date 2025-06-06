using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using TableDetector.Models;
using TableDetector.Utilities;

namespace TableDetector.Services
{
    public class TokenDetectionSettings
    {
        public int MinHeight { get; set; }
        public int MaxHeight { get; set; }
        public int DetectionThreshold { get; set; }
        public double BaseToHeightRatio { get; set; }
    }

    public class TokenDetectionService
    {
        private readonly Dictionary<string, int> tokenStabilityMap = new Dictionary<string, int>();
        private const int StabilityThreshold = 3;
        private readonly TokenDetectionSettings settings;
        private readonly TableSurfaceEstimator tableSurfaceEstimator = new TableSurfaceEstimator();

        public TokenDetectionService(TokenDetectionSettings detectionSettings)
        {
            settings = detectionSettings;
        }

        /// <summary>
        /// Detects tokens in a given ROI using both depth and color frames. Includes temporal filtering and color-based classification.
        /// </summary>
        public List<TTRPGToken> DetectTokens(MultiSourceFrame frame, Int32Rect roi)
        {
            var tokens = new List<TTRPGToken>();

            using (var depthFrame = frame.DepthFrameReference.AcquireFrame())
            using (var colorFrame = frame.ColorFrameReference.AcquireFrame())
            {
                if (depthFrame == null || colorFrame == null)
                {
                    CleanupStabilityMap();
                    return tokens;
                }

                int width = depthFrame.FrameDescription.Width;
                int height = depthFrame.FrameDescription.Height;
                ushort[] depthData = new ushort[width * height];
                depthFrame.CopyFrameDataToArray(depthData);

                int colorWidth = colorFrame.FrameDescription.Width;
                int colorHeight = colorFrame.FrameDescription.Height;
                byte[] colorData = new byte[colorWidth * colorHeight * 4];
                colorFrame.CopyConvertedFrameDataToArray(colorData, ColorImageFormat.Bgra);

                ushort tableDepth = tableSurfaceEstimator.EstimateSurfaceDepth(depthData, width, height, roi);

                bool[] visited = new bool[width * height];
                HashSet<string> currentKeys = new HashSet<string>();

                for (int y = Math.Max(1, roi.Y); y < Math.Min(height - 1, roi.Y + roi.Height); y++)
                {
                    for (int x = Math.Max(1, roi.X); x < Math.Min(width - 1, roi.X + roi.Width); x++)
                    {
                        int index = y * width + x;
                        if (visited[index]) continue;

                        ushort centerDepth = depthData[index];
                        if (centerDepth == 0) continue;

                        var blob = FloodFillBlob(x, y, width, height, depthData, visited, centerDepth, settings.DetectionThreshold);
                        if (blob.Count < 10) continue;

                        Point centroid = GetCentroid(blob);
                        double diameter = Math.Sqrt(blob.Count);
                        double heightMm = tableDepth > centerDepth ? tableDepth - centerDepth : 0;

                        if (heightMm < settings.MinHeight || heightMm > settings.MaxHeight)
                            continue;

                        string bucketKey = GetBucketKey(centroid);
                        currentKeys.Add(bucketKey);

                        if (!tokenStabilityMap.ContainsKey(bucketKey))
                            tokenStabilityMap[bucketKey] = 1;
                        else
                            tokenStabilityMap[bucketKey]++;

                        int stableFrames = tokenStabilityMap[bucketKey];
                        if (stableFrames >= StabilityThreshold)
                        {
                            var token = new TTRPGToken
                            {
                                Position = centroid,
                                Depth = centerDepth,
                                HeightMm = (ushort)heightMm,
                                DiameterPixels = diameter,
                                DiameterMeters = diameter * 0.001,
                                Points = blob,
                                Type = ClassifyToken(heightMm, diameter),
                                StabilityFrames = stableFrames
                            };

                            // Consolidated color classification
                            var category = TokenColorClassifier.DetectFromColorFrame(token, colorData, colorWidth, colorHeight, width, height);
                            var classification = TokenColorClassifier.GetActorClassification(category);
                            token.Color = classification.DisplayColor;
                            token.ActorType = classification.FoundryType;
                            token.IsHostile = classification.IsHostile;
                            token.Label = $"{classification.ActorType}\nH:{heightMm:F0}\nStb:{stableFrames}";

                            tokens.Add(token);
                        }
                    }
                }

                CleanupStabilityMap(currentKeys);

                tokens.Add(new TTRPGToken
                {
                    Position = new Point(roi.X + roi.Width - 60, roi.Y + 30),
                    Label = $"Table: {tableDepth}mm",
                    Color = Colors.Cyan,
                    DiameterPixels = 50,
                    HeightMm = 0,
                    Type = TokenType.Custom
                });
            }

            return tokens;
        }

        private string GetBucketKey(Point p) => $"{(int)(p.X / 5)}_{(int)(p.Y / 5)}";

        private void CleanupStabilityMap(HashSet<string> currentKeys)
        {
            var keys = new List<string>(tokenStabilityMap.Keys);
            foreach (var key in keys)
            {
                if (!currentKeys.Contains(key))
                    tokenStabilityMap.Remove(key);
            }
        }

        private void CleanupStabilityMap() => tokenStabilityMap.Clear();

        private List<Point> FloodFillBlob(int startX, int startY, int width, int height, ushort[] depthData, bool[] visited, ushort referenceDepth, int threshold)
        {
            var blob = new List<Point>();
            var queue = new Queue<Point>();
            queue.Enqueue(new Point(startX, startY));

            while (queue.Count > 0)
            {
                Point p = queue.Dequeue();
                int x = (int)p.X;
                int y = (int)p.Y;
                int index = y * width + x;

                if (x < 0 || x >= width || y < 0 || y >= height || visited[index])
                    continue;

                ushort depth = depthData[index];
                if (depth == 0 || Math.Abs(depth - referenceDepth) > threshold)
                    continue;

                visited[index] = true;
                blob.Add(p);

                queue.Enqueue(new Point(x + 1, y));
                queue.Enqueue(new Point(x - 1, y));
                queue.Enqueue(new Point(x, y + 1));
                queue.Enqueue(new Point(x, y - 1));
            }

            return blob;
        }

        private Point GetCentroid(List<Point> points)
        {
            double sumX = 0, sumY = 0;
            foreach (var p in points)
            {
                sumX += p.X;
                sumY += p.Y;
            }
            return new Point(sumX / points.Count, sumY / points.Count);
        }

        private TokenType ClassifyToken(double height, double diameter)
        {
            if (height > 50) return TokenType.Miniature;
            if (diameter < 20) return TokenType.SmallToken;
            if (diameter < 40) return TokenType.MediumToken;
            return TokenType.LargeToken;
        }
    }
}
