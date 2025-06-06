using Microsoft.Kinect;
using System;
using System.Linq;
using System.Windows;

namespace TableDetector.Services
{
    public class TableSurfaceEstimator
    {
        public ushort EstimateSurfaceDepth(ushort[] depthData, int width, int height, Int32Rect roi)
        {
            const int maxDepth = 8000;
            int[] histogram = new int[maxDepth + 1];

            for (int y = roi.Y; y < roi.Y + roi.Height && y < height; y++)
            {
                for (int x = roi.X; x < roi.X + roi.Width && x < width; x++)
                {
                    int index = y * width + x;
                    ushort depth = depthData[index];

                    if (depth > 0 && depth <= maxDepth)
                    {
                        histogram[depth]++;
                    }
                }
            }

            // Find the depth value with the highest frequency
            int maxCount = 0;
            ushort tableDepth = 0;
            for (ushort d = 1; d <= maxDepth; d++)
            {
                if (histogram[d] > maxCount)
                {
                    maxCount = histogram[d];
                    tableDepth = d;
                }
            }

            return tableDepth;
        }
    }
}
