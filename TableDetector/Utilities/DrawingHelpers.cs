using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TableDetector.Utilities
{
    public static class DrawingHelpers
    {
        public static void DrawRectangle(byte[] pixels, int width, int height,
            int x, int y, int rectWidth, int rectHeight, byte r, byte g, byte b)
        {
            int thickness = 2;
            for (int i = 0; i < thickness; i++)
            {
                DrawLine(pixels, width, height, x, y + i, x + rectWidth, y + i, r, g, b);
                DrawLine(pixels, width, height, x, y + rectHeight - i, x + rectWidth, y + rectHeight - i, r, g, b);
                DrawLine(pixels, width, height, x + i, y, x + i, y + rectHeight, r, g, b);
                DrawLine(pixels, width, height, x + rectWidth - i, y, x + rectWidth - i, y + rectHeight, r, g, b);
            }
        }

        public static void DrawLine(byte[] pixels, int width, int height,
            int x0, int y0, int x1, int y1, byte r, byte g, byte b)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
                {
                    int index = (y0 * width + x0) * 4;
                    pixels[index] = b;
                    pixels[index + 1] = g;
                    pixels[index + 2] = r;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }
    }
}
