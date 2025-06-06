using Microsoft.Kinect;
using System;

namespace TableDetector.Services
{
    public class KinectManager : IDisposable
    {
        public KinectSensor Sensor { get; private set; }
        public MultiSourceFrameReader FrameReader { get; private set; }
        public event Action<MultiSourceFrame> FrameArrived;

        public bool IsSensorAvailable => Sensor != null && Sensor.IsAvailable;

        public bool Initialize()
        {
            Sensor = KinectSensor.GetDefault();
            if (Sensor == null)
                return false;

            FrameReader = Sensor.OpenMultiSourceFrameReader(
                FrameSourceTypes.Color | FrameSourceTypes.Depth | FrameSourceTypes.BodyIndex);

            FrameReader.MultiSourceFrameArrived += OnFrameArrived;

            Sensor.Open();
            return true;
        }

        private void OnFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            var frame = e.FrameReference.AcquireFrame();
            if (frame != null)
                FrameArrived?.Invoke(frame);
        }

        public void Dispose()
        {
            if (FrameReader != null)
            {
                FrameReader.Dispose();
                FrameReader = null;
            }

            if (Sensor != null)
            {
                if (Sensor.IsOpen)
                    Sensor.Close();

                Sensor = null;
            }
        }
    }
}
