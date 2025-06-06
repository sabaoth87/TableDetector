using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows;
using TableDetector.Models;

namespace TableDetector.Utilities
{
    public class TokenRenderer
    {
        private Canvas renderCanvas;

        public TokenRenderer(Canvas targetCanvas = null)
        {
            renderCanvas = targetCanvas;
        }

        public void SetRenderTarget(Canvas canvas)
        {
            renderCanvas = canvas;
        }

        public void Render(List<TTRPGToken> tokens, Int32Rect roi)
        {
            if (renderCanvas == null || tokens == null)
                return;

            renderCanvas.Children.Clear();

            var roiRect = new Rectangle
            {
                Width = roi.Width,
                Height = roi.Height,
                Stroke = Brushes.Red,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            Canvas.SetLeft(roiRect, roi.X);
            Canvas.SetTop(roiRect, roi.Y);
            renderCanvas.Children.Add(roiRect);

            foreach (var token in tokens)
            {
                DrawToken(token);
            }
        }

        private void DrawToken(TTRPGToken token)
        {
            double size = token.DiameterPixels;
            double x = token.Position.X - size / 2;
            double y = token.Position.Y - size / 2;

            var ellipse = new Ellipse
            {
                Width = size,
                Height = size,
                Stroke = new SolidColorBrush(token.Color),
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(64, token.Color.R, token.Color.G, token.Color.B))
            };

            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            renderCanvas.Children.Add(ellipse);

            if (!string.IsNullOrEmpty(token.Label))
            {
                var label = new TextBlock
                {
                    Text = token.Label,
                    Foreground = Brushes.White,
                    Background = Brushes.Black,
                    FontSize = 12,
                    Padding = new Thickness(2)
                };

                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, y - 20);
                renderCanvas.Children.Add(label);
            }
        }
    }
}
