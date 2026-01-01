namespace SmartHomeServer.Services
{
    using OpenCvSharp;
    using OpenCvSharp.Dnn;
    using System.Runtime.InteropServices;
    using System.Net.Http;
    using System;
    using System.IO;
    using System.Threading;

    public class CameraService : IDisposable
    {
        private VideoCapture? _capture;
        private Thread? _captureThread;
        private bool _isRunning = false;
        private readonly Lock _lock = new();
        private byte[] _latestFrame = [];

        // --- 模型控制變數 ---
        private Net? _faceNet;
        private CascadeClassifier? _cascadeClassifier;

        // 狀態旗標
        private bool _isDnnLoaded = false;
        private bool _isHaarLoaded = false;

        // DNN 檔案路徑 & URL (優先使用，GPU 加速)
        private const string ProtoTxt = "deploy.prototxt";
        private const string CaffeModel = "res10_300x300_ssd_iter_140000.caffemodel";
        private const string ProtoTxtUrl = "https://raw.githubusercontent.com/opencv/opencv/master/samples/dnn/face_detector/deploy.prototxt";
        private const string CaffeModelUrl = "https://raw.githubusercontent.com/opencv/opencv_3rdparty/dnn_samples_face_detector_20170830/res10_300x300_ssd_iter_140000.caffemodel";

        // Haar Cascade 檔案路徑 & URL (備案，CPU)
        private const string HaarXml = "haarcascade_frontalface_default.xml";
        private const string HaarUrl = "https://raw.githubusercontent.com/opencv/opencv/master/data/haarcascades/haarcascade_frontalface_default.xml";

        // 當前偵測到的人數
        public int PersonCount { get; private set; } = 0;

        public CameraService()
        {
            EnsureModelFilesExist();
            LoadDetectionModels();
        }

        private static void EnsureModelFilesExist()
        {
            try
            {
                using var client = new HttpClient();

                // 1. 檢查並下載 DNN 模型
                if (!File.Exists(ProtoTxt))
                {
                    Console.WriteLine($"[CameraService] '{ProtoTxt}' not found. Downloading...");
                    File.WriteAllBytes(ProtoTxt, client.GetByteArrayAsync(ProtoTxtUrl).Result);
                }
                if (!File.Exists(CaffeModel))
                {
                    Console.WriteLine($"[CameraService] '{CaffeModel}' not found. Downloading (this may take a while)...");
                    File.WriteAllBytes(CaffeModel, client.GetByteArrayAsync(CaffeModelUrl).Result);
                }

                // 2. 檢查並下載 Haar Cascade XML (Fallback 用)
                if (!File.Exists(HaarXml))
                {
                    Console.WriteLine($"[CameraService] '{HaarXml}' not found. Downloading...");
                    File.WriteAllBytes(HaarXml, client.GetByteArrayAsync(HaarUrl).Result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CameraService] Model download warning: {ex.Message}");
                Console.WriteLine("System will attempt to run with whatever files are available.");
            }
        }

        /// <summary>
        /// 設定 DNN 推論的後端與硬體目標
        /// 優先順序: NPU (OpenVINO) -> NVIDIA GPU (CUDA) -> OpenCL -> CPU
        /// </summary>
        private static void ConfigureBackend(Net net)
        {
            Console.WriteLine("[Init] 正在測試並選擇最佳硬體加速模式...");

            // 定義硬體加速的嘗試清單 (優先順序由上而下)
            var configs = new[]
            {
                
                
                // NVIDIA GPU (CUDA)
                (Backend.CUDA, Target.CUDA, "NVIDIA GPU (CUDA)"),
                
                // Intel OpenVINO (NPU / CPU 優化)
                (Backend.INFERENCE_ENGINE, Target.MYRIAD, "Intel OpenVINO (NPU/CPU)"),
                
                // OpenCL (通用 GPU)
                (Backend.OPENCV, Target.OPENCL, "OpenCL (General GPU)"),
                
                // CPU (保證可用)
                (Backend.OPENCV, Target.CPU, "CPU (Fallback)")
            };

            foreach (var (backend, target, name) in configs)
            {
                try
                {
                    Console.Write($"[Init] 測試: {name} ... ");

                    net.SetPreferableBackend(backend);
                    net.SetPreferableTarget(target);

                    // 進行一次 Dummy Forward Pass 來驗證該設定是否真正可用
                    // 建立 1x3x300x300 的假資料 (符合模型輸入規格)
                    using var dummyBlob = CvDnn.BlobFromImage(
                        new Mat(300, 300, MatType.CV_8UC3, new Scalar(0, 0, 0)),
                        1.0, new OpenCvSharp.Size(300, 300), new Scalar(0, 0, 0), false, false);

                    net.SetInput(dummyBlob);
                    using var dummyOut = net.Forward(); // 若硬體不支援或缺驅動/DLL，此處會拋出例外

                    Console.WriteLine("成功!");
                    Console.WriteLine($"[System] 已啟用硬體加速模式: {name}");
                    return; // 成功後直接返回，保持目前的設定
                }
                catch (Exception)
                {
                    // 這裡不印出詳細錯誤以免洗版，只顯示失敗
                    Console.WriteLine("失敗 (跳過)");
                    // 失敗則繼續迴圈嘗試下一個
                }
            }

            Console.WriteLine("[Error] 嚴重錯誤: 所有運算後端皆無法初始化。");
        }

        private void LoadDetectionModels()
        {
            // 優先嘗試載入 DNN (為了 GPU 加速)，若失敗則載入 Haar
            _isDnnLoaded = false;
            _isHaarLoaded = false;

            // --- 嘗試 DNN ---
            try
            {
                if (File.Exists(ProtoTxt) && File.Exists(CaffeModel))
                {
                    Console.WriteLine("[CameraService] Attempting to load DNN Model...");
                    _faceNet = CvDnn.ReadNetFromCaffe(ProtoTxt, CaffeModel) ?? throw new Exception("DNN initialization returned null.");

                    // 使用新的自動硬體加速配置邏輯
                    ConfigureBackend(_faceNet);

                    _isDnnLoaded = true;
                    Console.WriteLine("[CameraService] DNN Model loaded successfully.");
                }
                else
                {
                    Console.WriteLine("[CameraService] DNN files missing. Skipping DNN load.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CameraService] Failed to load DNN: {ex.Message}");
                _isDnnLoaded = false;
            }

            // --- 嘗試 Haar Cascade (Fallback) ---
            if (!_isDnnLoaded)
            {
                Console.WriteLine("[CameraService] Fallback: Attempting to load Haar Cascade...");
                try
                {
                    if (File.Exists(HaarXml))
                    {
                        _cascadeClassifier = new CascadeClassifier(HaarXml);
                        _isHaarLoaded = true;
                        Console.WriteLine("[CameraService] Haar Cascade loaded (CPU Mode).");
                    }
                    else
                    {
                        Console.WriteLine("[CameraService] Critical: Haar XML missing. No face detection available.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CameraService] Failed to load Haar: {ex.Message}");
                }
            }
        }

        public void StartCapture()
        {
            if (_isRunning) return;

            _isRunning = true;
            // 將相機初始化移至 Thread 內，避免卡住主執行緒，並支援重試邏輯
            _captureThread = new Thread(CaptureLoop) { IsBackground = true };
            _captureThread.Start();
            Console.WriteLine("Camera Capture Thread Started.");
        }

        private void CaptureLoop()
        {
            // --- 整合的相機初始化邏輯 (故障轉移) ---
            int[] cameraIndexes = { 0, 1 };
            bool cameraFound = false;

            foreach (var index in cameraIndexes)
            {
                try
                {
                    Console.WriteLine($"[Camera] Attempting to open camera index {index}...");
                    var tempCap = new VideoCapture(index);

                    if (tempCap.IsOpened())
                    {
                        // 嘗試讀取一幀以驗證
                        using var testFrame = new Mat();
                        if (tempCap.Read(testFrame) && !testFrame.Empty())
                        {
                            _capture = tempCap;
                            // 設定參數
                            _capture.Set(VideoCaptureProperties.FrameWidth, 640);
                            _capture.Set(VideoCaptureProperties.FrameHeight, 480);
                            _capture.Set(VideoCaptureProperties.Fps, 30);

                            Console.WriteLine($"[Camera] Successfully connected to camera index {index}.");
                            cameraFound = true;
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"[Camera] Index {index} opened but read failed. Disposing...");
                            tempCap.Dispose();
                        }
                    }
                    else
                    {
                        tempCap.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Camera] Error trying index {index}: {ex.Message}");
                }
            }

            if (!cameraFound || _capture == null)
            {
                Console.WriteLine("[Camera] Fatal Error: No functional camera found.");
                return;
            }

            // --- 主循環 ---
            using var frame = new Mat();

            while (_isRunning)
            {
                try
                {
                    if (_capture != null && _capture.IsOpened() && _capture.Read(frame) && !frame.Empty())
                    {
                        // 根據載入狀態選擇演算法
                        if (_isDnnLoaded)
                        {
                            DetectFacesDnn(frame);
                        }
                        else if (_isHaarLoaded)
                        {
                            DetectFacesHaar(frame);
                        }

                        // 壓縮圖片
                        var bytes = frame.ImEncode(".jpg", new int[] { (int)ImwriteFlags.JpegQuality, 70 });
                        lock (_lock)
                        {
                            _latestFrame = bytes;
                        }
                    }
                    else
                    {
                        // 讀取失敗時的保護
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Camera] Frame Loop Error: {ex.Message}");
                }

                Thread.Sleep(30);
            }
        }

        // --- DNN 偵測 (GPU) ---
        private void DetectFacesDnn(Mat frame)
        {
            if (_faceNet == null) return;

            try
            {
                int h = frame.Rows;
                int w = frame.Cols;
                using var blob = CvDnn.BlobFromImage(frame, 1.0, new OpenCvSharp.Size(300, 300), new Scalar(104.0, 177.0, 123.0), false, false);
                _faceNet.SetInput(blob);
                using var detection = _faceNet.Forward();

                var mat = detection.Reshape(1, detection.Size(2));
                int rows = mat.Rows;
                int count = 0;

                for (int i = 0; i < rows; i++)
                {
                    float confidence = mat.At<float>(i, 2);
                    if (confidence > 0.5f)
                    {
                        count++;
                        int x1 = (int)(mat.At<float>(i, 3) * w);
                        int y1 = (int)(mat.At<float>(i, 4) * h);
                        int x2 = (int)(mat.At<float>(i, 5) * w);
                        int y2 = (int)(mat.At<float>(i, 6) * h);

                        Cv2.Rectangle(frame, new OpenCvSharp.Point(x1, y1), new OpenCvSharp.Point(x2, y2), Scalar.LimeGreen, 2);
                        Cv2.PutText(frame, $"DNN: {confidence:P0}", new OpenCvSharp.Point(x1, y1 - 10), HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1);
                    }
                }
                PersonCount = count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DNN Runtime Error: {ex.Message}");
            }
        }

        // --- Haar Cascade 偵測 (CPU Fallback) ---
        private void DetectFacesHaar(Mat frame)
        {
            if (_cascadeClassifier == null) return;

            try
            {
                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.EqualizeHist(gray, gray);

                var faces = _cascadeClassifier.DetectMultiScale(gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(30, 30));

                foreach (var face in faces)
                {
                    Cv2.Rectangle(frame, face, Scalar.Red, 2); // 使用紅色框區分 Haar
                    Cv2.PutText(frame, "Haar", new OpenCvSharp.Point(face.X, face.Y - 10), HersheyFonts.HersheySimplex, 0.5, Scalar.Red, 1);
                }
                PersonCount = faces.Length;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Haar Error: {ex.Message}");
            }
        }

        public byte[] GetLatestFrame()
        {
            lock (_lock) { return _latestFrame; }
        }

        public void StopCapture()
        {
            _isRunning = false;
            _captureThread?.Join();
            _capture?.Dispose();
            _faceNet?.Dispose();
            _cascadeClassifier?.Dispose();
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}