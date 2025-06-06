using System;
using System.Collections.Generic;
using System.Windows.Media;
using TableDetector;

namespace TableDetector.Utilities
{
    public enum TokenColorCategory
    {
        Red, Green, Blue, Yellow, White, Black,
        Purple, Orange, Brown, Gray, Cyan, Pink, Unknown
    }

    public class ActorClassification
    {
        public string ActorType { get; set; } = "unknown";
        public string FoundryType { get; set; } = "npc";
        public bool IsHostile { get; set; } = false;
        public Color DisplayColor { get; set; } = Colors.Gray;
    }

    public static class TokenColorClassifier
    {
        public static TokenColorCategory CategorizeDominantColor(int r, int g, int b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int delta = max - min;

            double brightness = max / 255.0;
            double saturation = max == 0 ? 0 : delta / (double)max;

            if (brightness < 0.2) return TokenColorCategory.Black;
            if (brightness > 0.9 && saturation < 0.1) return TokenColorCategory.White;
            if (saturation < 0.15) return TokenColorCategory.Gray;

            if (r > g + 50 && r > b + 50) return TokenColorCategory.Red;
            if (g > r + 50 && g > b + 50) return TokenColorCategory.Green;
            if (b > r + 50 && b > g + 50) return TokenColorCategory.Blue;
            if (r > b + 50 && g > b + 50) return TokenColorCategory.Yellow;
            if (r > g + 30 && b > g + 30) return TokenColorCategory.Purple;
            if (g > r + 30 && b > r + 30) return TokenColorCategory.Cyan;
            if (r > g + 50 && g > b + 20 && r > 200) return TokenColorCategory.Orange;
            if (r > g && r < 200 && g < 150 && b < 100) return TokenColorCategory.Brown;
            if (r > g + 50 && r > b && b > g) return TokenColorCategory.Pink;

            return TokenColorCategory.Unknown;
        }

        public static TokenColorCategory DetectFromColorFrame(
            TTRPGToken token,
            byte[] colorData,
            int colorWidth,
            int colorHeight,
            int depthWidth,
            int depthHeight)
        {
            if (colorData == null || token.Points.Count == 0)
                return TokenColorCategory.Unknown;

            int totalR = 0, totalG = 0, totalB = 0, sampleCount = 0;

            foreach (var point in token.Points)
            {
                int x = (int)point.X;
                int y = (int)point.Y;

                int colorX = (int)(x * (double)colorWidth / depthWidth);
                int colorY = (int)(y * (double)colorHeight / depthHeight);

                if (colorX < 0 || colorX >= colorWidth || colorY < 0 || colorY >= colorHeight)
                    continue;

                int colorIndex = (colorY * colorWidth + colorX) * 4;
                if (colorIndex + 2 >= colorData.Length)
                    continue;

                byte b = colorData[colorIndex];
                byte g = colorData[colorIndex + 1];
                byte r = colorData[colorIndex + 2];

                totalB += b;
                totalG += g;
                totalR += r;
                sampleCount++;
            }

            if (sampleCount == 0)
                return TokenColorCategory.Unknown;

            int avgR = totalR / sampleCount;
            int avgG = totalG / sampleCount;
            int avgB = totalB / sampleCount;

            token.Color = Color.FromRgb((byte)avgR, (byte)avgG, (byte)avgB);
            return CategorizeDominantColor(avgR, avgG, avgB);
        }

        public static ActorClassification GetActorClassification(TokenColorCategory category)
        {
            return ActorTypeMap.TryGetValue(category, out var info) ? info : new ActorClassification();
        }

        public static readonly Dictionary<TokenColorCategory, ActorClassification> ActorTypeMap = new Dictionary<TokenColorCategory, ActorClassification>()
        {
            { TokenColorCategory.Red, new ActorClassification { ActorType = "enemy", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.DarkRed } },
            { TokenColorCategory.Green, new ActorClassification { ActorType = "ally", FoundryType = "character", IsHostile = false, DisplayColor = Colors.ForestGreen } },
            { TokenColorCategory.Blue, new ActorClassification { ActorType = "npc", FoundryType = "npc", IsHostile = false, DisplayColor = Colors.RoyalBlue } },
            { TokenColorCategory.Yellow, new ActorClassification { ActorType = "objective", FoundryType = "token", IsHostile = false, DisplayColor = Colors.Gold } },
            { TokenColorCategory.White, new ActorClassification { ActorType = "neutral", FoundryType = "npc", IsHostile = false, DisplayColor = Colors.White } },
            { TokenColorCategory.Black, new ActorClassification { ActorType = "trap", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.Black } },
            { TokenColorCategory.Purple, new ActorClassification { ActorType = "caster", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.DarkMagenta } },
            { TokenColorCategory.Orange, new ActorClassification { ActorType = "leader", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.OrangeRed } },
            { TokenColorCategory.Brown, new ActorClassification { ActorType = "beast", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.SaddleBrown } },
            { TokenColorCategory.Gray, new ActorClassification { ActorType = "hidden", FoundryType = "npc", IsHostile = false, DisplayColor = Colors.Gray } },
            { TokenColorCategory.Cyan, new ActorClassification { ActorType = "construct", FoundryType = "npc", IsHostile = true, DisplayColor = Colors.Cyan } },
            { TokenColorCategory.Pink, new ActorClassification { ActorType = "special", FoundryType = "npc", IsHostile = false, DisplayColor = Colors.HotPink } },
            { TokenColorCategory.Unknown, new ActorClassification { ActorType = "unknown", FoundryType = "npc", IsHostile = false, DisplayColor = Colors.Gray } }
        };
    }
}
