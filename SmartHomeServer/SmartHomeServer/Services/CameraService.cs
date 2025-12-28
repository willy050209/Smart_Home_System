namespace SmartHomeServer.Services 
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Threading;
    using OpenCvSharp;
    public class CameraService : IDisposable
    {
        private VideoCapture? _capture;
        private Thread? _captureThread; 
        private bool _isRunning;
        public int PersonCount { get; private set; } = 0;

        // 影像緩衝區
        private byte[] _bytesOriginal = [];
        private byte[] _bytesFace = []; 

        private readonly Lock _frameLock = new();

        private readonly Mat _frame = new();
        private readonly Mat _grayMat = new();
        private readonly Mat _faceMat = new(); 

        private CascadeClassifier? _faceCascade;

        public CameraService()
        {
            InitializeFaceModel();
        }

        public void StartCapture()
        {
            if (_captureThread != null && _captureThread.IsAlive) return;

            _isRunning = true;
            _captureThread = new Thread(CaptureLoop);
            _captureThread.Start();
            Console.WriteLine("[CameraService] Capture thread started.");
        }

        private void InitializeFaceModel()
        {
            // 確保模型檔案存在 
            string xmlFile = "haarcascade_frontalface_default.xml";
            if (!File.Exists(xmlFile))
            {
                Console.WriteLine("[System] Downloading Face Model...");
                try
                {
                    using var client = new HttpClient();
                    var bytes = client.GetByteArrayAsync("https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml").Result;
                    File.WriteAllBytes(xmlFile, bytes);
                }
                catch (Exception ex) { Console.WriteLine($"[System] Model Download Failed: {ex.Message}"); }
            }

            if (File.Exists(xmlFile))
            {
                _faceCascade = new CascadeClassifier(xmlFile);
            }
        }

        private void CaptureLoop()
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    var tempCap = new VideoCapture(i);
                    if (tempCap.IsOpened()) { _capture = tempCap; break; }
                    tempCap.Dispose();
                }

                if (_capture == null || !_capture.IsOpened())
                {
                    Console.WriteLine("[Camera] No camera found.");
                    return;
                }

                _capture.Set(VideoCaptureProperties.FrameWidth, 640);
                _capture.Set(VideoCaptureProperties.FrameHeight, 480);

                long frameCounter = 0;

                while (_isRunning)
                {
                    if (_capture.Read(_frame) && !_frame.Empty())
                    {
                        frameCounter++;

                        // 每 5 幀做一次人臉偵測
                        if (frameCounter % 5 == 0)
                        {
                            DetectFaces();
                        }
                        else
                        {
                            lock (_frameLock)
                            {
                                _bytesOriginal = _frame.ImEncode(".jpg");
                                if (_bytesFace.Length == 0) _bytesFace = _bytesOriginal;
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                    Thread.Sleep(30);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Camera] Loop Error: {ex.Message}");
            }
        }

        private void DetectFaces()
        {
            if (_faceCascade == null) return;

            using var smallFrame = new Mat();

            Cv2.Resize(_frame, smallFrame, new Size(320, 240));
            using var gray = new Mat();
            Cv2.CvtColor(smallFrame, gray, ColorConversionCodes.BGR2GRAY);

            var faces = _faceCascade.DetectMultiScale(
                image: gray,
                scaleFactor: 1.1,
                minNeighbors: 3,
                minSize: new Size(20, 20)
            );

            PersonCount = faces.Length;

            foreach (var rect in faces)
            {
                var scaledRect = new Rect(rect.X * 2, rect.Y * 2, rect.Width * 2, rect.Height * 2);
                Cv2.Rectangle(_frame, scaledRect, Scalar.Red, 2);
            }

            lock (_frameLock)
            {
                _bytesFace = _frame.ImEncode(".jpg");
                _bytesOriginal = _bytesFace;
            }
        }

        public byte[]? GetLatestFrame()
        {
            lock (_frameLock) return _bytesOriginal;
        }

        public void Dispose()
        {
            _isRunning = false;
            _captureThread?.Join(500);
            _capture?.Dispose();
            _frame.Dispose();
            _grayMat.Dispose();
            _faceCascade?.Dispose();
        }
    }
}