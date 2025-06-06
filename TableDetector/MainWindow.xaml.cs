using System;
using System.Windows;
using TableDetector.Services;
using TableDetector.Utilities;
using Microsoft.Kinect;
using TableDetector.Models;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;

namespace TableDetector
{
    public partial class MainWindow : Window
    {
        private KinectManager kinectManager;
        private TokenDetectionService tokenDetectionService;
        private TokenTrackingService tokenTrackingService;
        private TokenRenderer tokenRenderer;
        private WebSocketService webSocketService;
        private WriteableBitmap colorBitmap;
        private List<TTRPGToken> currentTokens = new List<TTRPGToken>();
        private Int32Rect roiRect = new Int32Rect(300, 100, 700, 400); // Example values
        public Int32Rect ROI => roiRect;
        private bool isDrawingRoi = false;
        private Point roiStartPoint;
        private Rectangle roiVisual;

        public MainWindow()
        {
            InitializeComponent();
            InitializeKinect();

            roiVisual = new Rectangle
            {
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                Visibility = Visibility.Hidden
            };
            RoiSelectionCanvas.Children.Add(roiVisual);

            var detectionSettings = new TokenDetectionSettings
            {
                MinHeight = 10,
                MaxHeight = 50,
                DetectionThreshold = 15,
                BaseToHeightRatio = 0.6
            };

            tokenDetectionService = new TokenDetectionService(detectionSettings);
            tokenTrackingService = new TokenTrackingService();
            tokenRenderer = new TokenRenderer(TokenOverlayCanvas);
            webSocketService = new WebSocketService("ws://localhost:3000");
            _ = webSocketService.ConnectAsync();
        }

        private void InitializeKinect()
        {
            kinectManager = new KinectManager();
            if (!kinectManager.Initialize())
            {
                StatusText = "Kinect initialization failed.";
                return;
            }

            kinectManager.FrameArrived += OnFrameArrived;
            StatusText = "Kinect initialized successfully.";
        }

        private async void OnFrameArrived(MultiSourceFrame frame)
        {
            await ProcessMultiSourceFrame(frame);

        }

        private async Task ProcessMultiSourceFrame(MultiSourceFrame frame)
        {
            // Show camera feed
            using (var colorFrame = frame.ColorFrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    using (KinectBuffer colorBuffer = colorFrame.LockRawImageBuffer())
                    {
                        var width = colorFrame.FrameDescription.Width;
                        var height = colorFrame.FrameDescription.Height;

                        // Allocate or reuse a WriteableBitmap
                        if (colorBitmap == null || colorBitmap.PixelWidth != width || colorBitmap.PixelHeight != height)
                        {
                            colorBitmap = new WriteableBitmap(width, height, 96.0, 96.0, PixelFormats.Bgr32, null);
                            KinectColorImage.Source = colorBitmap;
                        }

                        colorBitmap.Lock();
                        colorFrame.CopyConvertedFrameDataToIntPtr(
                            colorBitmap.BackBuffer,
                            (uint)(width * height * 4),
                            ColorImageFormat.Bgra);

                        colorBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                        colorBitmap.Unlock();
                    }
                }
            }

            // Token detection
            var detectedTokens = tokenDetectionService.DetectTokens(frame, ROI);
            currentTokens = tokenTrackingService.Track(detectedTokens);

            foreach (var token in currentTokens)
            {
                var category = TokenColorClassifier.CategorizeDominantColor(token.Color.R, token.Color.G, token.Color.B);
                var classification = TokenColorClassifier.GetActorClassification(category);
                token.Label = classification.ActorType;
                token.ActorType = classification.FoundryType;
                token.IsHostile = classification.IsHostile;
            }

            tokenRenderer.Render(currentTokens, ROI);
            await webSocketService.SendTokensAsync(currentTokens);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            kinectManager?.Dispose();
            webSocketService?.Dispose();
        }

        private void RoiCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            roiStartPoint = e.GetPosition(RoiSelectionCanvas);
            Canvas.SetLeft(roiVisual, roiStartPoint.X);
            Canvas.SetTop(roiVisual, roiStartPoint.Y);
            roiVisual.Width = 0;
            roiVisual.Height = 0;
            roiVisual.Visibility = Visibility.Visible;
            isDrawingRoi = true;
        }

        private void RoiCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDrawingRoi) return;

            Point pos = e.GetPosition(RoiSelectionCanvas);
            double x = Math.Min(pos.X, roiStartPoint.X);
            double y = Math.Min(pos.Y, roiStartPoint.Y);
            double width = Math.Abs(pos.X - roiStartPoint.X);
            double height = Math.Abs(pos.Y - roiStartPoint.Y);

            Canvas.SetLeft(roiVisual, x);
            Canvas.SetTop(roiVisual, y);
            roiVisual.Width = width;
            roiVisual.Height = height;
        }

        private void RoiCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!isDrawingRoi) return;
            isDrawingRoi = false;
            roiVisual.Visibility = Visibility.Hidden;

            Point end = e.GetPosition(RoiSelectionCanvas);
            double x = Math.Min(end.X, roiStartPoint.X);
            double y = Math.Min(end.Y, roiStartPoint.Y);
            double width = Math.Abs(end.X - roiStartPoint.X);
            double height = Math.Abs(end.Y - roiStartPoint.Y);

            roiRect = new Int32Rect((int)x, (int)y, (int)width, (int)height);
            StatusText = $"ROI updated: {roiRect.X},{roiRect.Y} {roiRect.Width}x{roiRect.Height}";
        }

    }
}
