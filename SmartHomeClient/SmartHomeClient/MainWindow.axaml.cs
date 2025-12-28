using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.AspNetCore.SignalR.Client; 
using OpenCvSharp;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace SmartHomeClient
{
    public partial class MainWindow : Avalonia.Controls.Window
    {
        private HttpClient? _httpClient;
        private HubConnection? _hubConnection;
        private string _currentApiUrl = "";

        private readonly DispatcherTimer _timerUi;
        private readonly DispatcherTimer _timerPoll;

        private Mat? _prevFrame;

        public MainWindow()
        {
            InitializeComponent();

            _timerUi = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timerUi.Tick += OnUiLoop;

            _timerPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timerPoll.Tick += async (s, e) => await PollAuthStatus();
        }

        // --- 連線邏輯 ---
        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            var urlBox = this.FindControl<TextBox>("BoxServerUrl");
            string url = urlBox?.Text?.Trim() ?? "";

            if (string.IsNullOrEmpty(url)) return;

            try
            {
                _currentApiUrl = url;
                _httpClient = new HttpClient { BaseAddress = new Uri(url) };

                //啟動 HTTP 輪詢 Timer (用於影像與光感)
                _timerUi.Start();
                _timerPoll.Start();

                // 建立 SignalR 連線 (用於 ESP32 數據)
                string hubUrl = url.TrimEnd('/') + "/sensorHub";
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(hubUrl)
                    .WithAutomaticReconnect()
                    .Build();

                // 監聽來自 Server 的數據
                _hubConnection.On<string>("ReceiveSensorData", (json) =>
                {
                    Dispatcher.UIThread.Post(() => ProcessSensorData(json));
                });

                await _hubConnection.StartAsync();
                Console.WriteLine("SignalR Connected!");

                // 3. 切換介面
                var connGrid = this.FindControl<Grid>("ConnectionGrid");
                var dashGrid = this.FindControl<Grid>("DashboardGrid");
                var txtUrlDisplay = this.FindControl<TextBlock>("TxtServerUrlDisplay");

                connGrid?.IsVisible = false;
                dashGrid?.IsVisible = true;
                txtUrlDisplay?.Text = url;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection Error: {ex.Message}");
            }
        }

        private void OnDisconnectClick(object sender, RoutedEventArgs e)
        {
            _timerUi.Stop();
            _timerPoll.Stop();
            _hubConnection?.StopAsync();
            _httpClient = null;

            var connGrid = this.FindControl<Grid>("ConnectionGrid");
            var dashGrid = this.FindControl<Grid>("DashboardGrid");

            connGrid?.IsVisible = true;
            dashGrid?.IsVisible = false;
        }

        // --- 處理 ESP32 Sensor 數據 (SignalR) ---
        private void ProcessSensorData(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string sensorId = root.GetProperty("sensorId").GetString() ?? "";
                double temp = root.GetProperty("temp").GetDouble();
                double hum = root.GetProperty("hum").GetDouble();
                double pres = root.GetProperty("pressure").GetDouble();
                string time = DateTime.Now.ToString("HH:mm:ss");

                if (sensorId == "esp32_01") // MQTT Node
                {
                    this.FindControl<TextBlock>("TxtMqttTemp")!.Text = temp.ToString("F1");
                    this.FindControl<TextBlock>("TxtMqttHum")!.Text = hum.ToString("F1");
                    this.FindControl<TextBlock>("TxtMqttPres")!.Text = pres.ToString("F0");
                    this.FindControl<TextBlock>("TxtMqttTime")!.Text = "Last Update: " + time;
                }
                else if (sensorId == "esp32_bt_01") // Bluetooth Node
                {
                    this.FindControl<TextBlock>("TxtBtTemp")!.Text = temp.ToString("F1");
                    this.FindControl<TextBlock>("TxtBtHum")!.Text = hum.ToString("F1");
                    this.FindControl<TextBlock>("TxtBtPres")!.Text = pres.ToString("F0");
                    this.FindControl<TextBlock>("TxtBtTime")!.Text = "Last Update: " + time;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing sensor data: {ex.Message}");
            }
        }

        // --- 抓取 TX2 本地 Sensor 與 影像 ---
        private async void OnUiLoop(object? sender, EventArgs e)
        {
            if (_httpClient == null) return;

            try
            {
                // 讀取光感應器
                var sensorJson = await _httpClient.GetStringAsync("/api/hw/sensor");
                using var doc = JsonDocument.Parse(sensorJson);
                int lightVal = doc.RootElement.GetProperty("light").GetInt32();

                var txtLight = this.FindControl<TextBlock>("TxtLightLevel");
                var barLight = this.FindControl<ProgressBar>("BarLight");

                txtLight?.Text = lightVal.ToString();
                barLight?.Value = lightVal;

                // 讀取 LED 狀態
                var ledJson = await _httpClient.GetStringAsync("/api/hw/leds");
                var ledStates = JsonSerializer.Deserialize<bool[]>(ledJson);
                UpdateLedStatus(ledStates);

                // 讀取相機影像
                var imageBytes = await _httpClient.GetByteArrayAsync("/api/camera/frame");
                if (imageBytes != null && imageBytes.Length > 0)
                {
                    using var ms = new MemoryStream(imageBytes);
                    var bitmap = new Bitmap(ms);
                    this.FindControl<Image>("CamDisplay")!.Source = bitmap;

                    var mat = Mat.FromImageData(imageBytes, ImreadModes.Color);
                    DetectMotion(mat);
                }
            }
            catch { /* 忽略 */ }
        }

        private void UpdateLedStatus(bool[]? states)
        {
            if (states == null || states.Length < 4) return;

            var l1 = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("Led1");
            var l2 = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("Led2");
            var l3 = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("Led3");
            var l4 = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("Led4");

            l1?.Fill = states[0] ? Brushes.Lime : Brushes.DimGray;
            l2?.Fill = states[1] ? Brushes.Lime : Brushes.DimGray;
            l3?.Fill = states[2] ? Brushes.Lime : Brushes.DimGray;
            l4?.Fill = states[3] ? Brushes.Lime : Brushes.DimGray;
        }

        // --- 動態偵測邏輯 ---
        private void DetectMotion(Mat currentFrame)
        {
            using var gray = new Mat();
            Cv2.CvtColor(currentFrame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(21, 21), 0);

            if (_prevFrame == null)
            {
                _prevFrame = gray.Clone();
                return;
            }

            using var diff = new Mat();
            using var thresh = new Mat();

            Cv2.Absdiff(_prevFrame, gray, diff);
            Cv2.Threshold(diff, thresh, 25, 255, ThresholdTypes.Binary);

            int nonZero = Cv2.CountNonZero(thresh);
            var txtMotion = this.FindControl<TextBlock>("TxtMotionStatus");

            if (txtMotion != null)
            {
                if (nonZero > 5000)
                {
                    txtMotion.Text = "MOTION DETECTED!";
                    txtMotion.Foreground = Brushes.Red;
                }
                else
                {
                    txtMotion.Text = "NO MOTION";
                    txtMotion.Foreground = Brushes.Lime;
                }
            }

            _prevFrame = gray.Clone();
        }

        // --- 狀態輪詢 (授權) ---
        private bool _isRemoteAuthorized = false;
        private async Task PollAuthStatus()
        {
            if (_httpClient == null) return;
            try
            {
                var json = await _httpClient.GetStringAsync("/api/auth/status");
                using var doc = JsonDocument.Parse(json);
                _isRemoteAuthorized = doc.RootElement.GetProperty("authorized").GetBoolean();

                var txtStatus = this.FindControl<TextBlock>("TxtRemoteStatus");
                if (txtStatus != null)
                {
                    txtStatus.Text = _isRemoteAuthorized ? "AUTHORIZED" : "LOCKED";
                    txtStatus.Foreground = _isRemoteAuthorized ? Brushes.Lime : Brushes.Red;
                }
            }
            catch { }
        }

        // --- 登入驗證 ---
        private async void OnLoginClick(object? sender, RoutedEventArgs e)
        {
            if (_httpClient == null) return;

            var txtResult = this.FindControl<TextBlock>("TxtLoginResult");
            var boxPwd = this.FindControl<TextBox>("BoxPassword");
            string pwd = boxPwd?.Text ?? "";

            if (!_isRemoteAuthorized)
            {
                txtResult?.Text = "Error: Web Authorization Required first!";
                return;
            }

            bool isSuccess = pwd == "1234"; // 模擬驗證

            try
            {
                var payload = new { Password = pwd, Success = isSuccess };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                await _httpClient.PostAsync("/api/driver/log", content);

                if (txtResult != null)
                {
                    txtResult.Text = isSuccess ? "Login Success (Logged to Kernel)" : "Wrong Password (Logged)";
                    txtResult.Foreground = isSuccess ? Brushes.Lime : Brushes.Red;
                }
                OnRefreshLog(null, null);
            }
            catch (Exception ex)
            {
                txtResult?.Text = $"Network Error: {ex.Message}";
            }
        }

        // --- 刷新 Logs ---
        private async void OnRefreshLog(object? sender, RoutedEventArgs? e)
        {
            if (_httpClient == null) return;
            var listLogs = this.FindControl<ListBox>("ListLogs");
            if (listLogs == null) return;

            try
            {
                var json = await _httpClient.GetStringAsync("/api/driver/logs");
                var logs = JsonSerializer.Deserialize<string[]>(json);

                listLogs.Items.Clear();
                if (logs != null)
                {
                    foreach (var log in logs) listLogs.Items.Add(log);
                }
                else
                {
                    listLogs.Items.Add("No logs returned.");
                }
            }
            catch (Exception ex)
            {
                listLogs.Items.Add($"Error: {ex.Message}");
            }
        }

        // --- AI 指令 ---
        private async void OnSendAiCommand(object sender, RoutedEventArgs e)
        {
            if (_httpClient == null) return;

            var boxChat = this.FindControl<TextBox>("BoxChat");
            var txtResponse = this.FindControl<TextBlock>("TxtAiResponse");

            string input = boxChat?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input)) return;

            txtResponse?.Text = "Thinking...";

            try
            {
                var payload = new { Text = input };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/ai/command", content);
                var resultJson = await response.Content.ReadAsStringAsync();
                var aiCmd = JsonSerializer.Deserialize<AiCommand>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                txtResponse?.Text = aiCmd?.Message;

                if (aiCmd?.Targets != null)
                {
                    foreach (int ledIndex in aiCmd.Targets)
                    {
                        if (aiCmd.Action == "on")
                            await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/true", null);
                        else if (aiCmd.Action == "off")
                            await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/false", null);
                        else if (aiCmd.Action == "blink")
                        {
                            // 簡單的非同步閃爍觸發
                            _ = Task.Run(async () => {
                                for (int i = 0; i < 3; i++)
                                {
                                    await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/true", null);
                                    await Task.Delay(200);
                                    await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/false", null);
                                    await Task.Delay(200);
                                }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                txtResponse?.Text = $"Error: {ex.Message}";
            }
        }

        // --- 鍵盤控制 F1-F4 ---
        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (_httpClient == null) return;
            int ledIndex = 0;
            switch (e.Key)
            {
                case Key.F1: ledIndex = 1; break;
                case Key.F2: ledIndex = 2; break;
                case Key.F3: ledIndex = 3; break;
                case Key.F4: ledIndex = 4; break;
            }

            if (ledIndex > 0)
            {
                try
                {
                    // 這裡簡化邏輯，直接發送切換請求有點困難，因為不知道當前狀態
                    // 所以先讀取再切換
                    var ledJson = await _httpClient.GetStringAsync("/api/hw/leds");
                    var ledStates = JsonSerializer.Deserialize<bool[]>(ledJson);
                    if (ledStates != null)
                    {
                        bool newState = !ledStates[ledIndex - 1];
                        await _httpClient.PostAsync($"/api/hw/led/{ledIndex}/{newState}", null);
                    }
                }
                catch { }
            }
        }

        private void OnOpenLedControl(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentApiUrl)) return;
            var ledWindow = new LedControlWindow(_currentApiUrl);
            ledWindow.ShowDialog(this);
        }

        private async void OnSmartAwayClick(object? sender, RoutedEventArgs e)
        {
            if (_httpClient == null) return;
            var txtStatus = this.FindControl<TextBlock>("TxtSmartStatus");
            var btn = this.FindControl<Button>("BtnSmartAway");

            txtStatus?.Text = "Checking camera...";
            btn?.IsEnabled = false;

            try
            {
                var response = await _httpClient.PostAsync("/api/smart/away", null);
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string message = doc.RootElement.GetProperty("message").GetString() ?? "";
                txtStatus?.Text = message;
            }
            catch (Exception ex)
            {
                txtStatus?.Text = $"Error: {ex.Message}";
            }
            finally
            {
                btn?.IsEnabled = true;
            }
        }

        public class AiCommand
        {
            public string? Action { get; set; }
            public int[]? Targets { get; set; }
            public string? Message { get; set; }
        }
    }
}