using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
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

namespace SmartHomeClient.Views
{
    public partial class MainView : UserControl
    {
        private HttpClient? _httpClient;
        private HubConnection? _hubConnection;
        private string _currentApiUrl = "";

        private readonly DispatcherTimer _timerUi;
        private readonly DispatcherTimer _timerPoll;
        private bool _isDraggingSlider = false;

        private Mat? _prevFrame;

        public MainView()
        {
            InitializeComponent();
			
			if (OperatingSystem.IsAndroid())
            {

                this.Padding = new Thickness(0, 35, 0, 0);
            }

            _timerUi = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timerUi.Tick += OnUiLoop;

            _timerPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timerPoll.Tick += async (s, e) => await PollAuthStatus();

            this.AttachedToVisualTree += (s, e) => this.Focus();
			var ledOverlay = this.FindControl<LedControlWindow>("LedOverlay");
            if (ledOverlay != null)
            {
                ledOverlay.RequestClose += (s, e) => {
                    ledOverlay.IsVisible = false; // 隱藏面板
                };
            }

            var slider = this.FindControl<Slider>("SliderFan");
            if (slider != null)
            {
                slider.PropertyChanged += (s, e) => {
                    if (e.Property.Name == "Value")
                    {
                        var txt = this.FindControl<TextBlock>("TxtTargetPwm");
                        if (txt != null) txt.Text = ((int)slider.Value).ToString();
                    }
                };
            }
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

                if (connGrid != null) connGrid.IsVisible = false;
                if (dashGrid != null) dashGrid.IsVisible = true;
                if (txtUrlDisplay != null) txtUrlDisplay.Text = url;
            }
            catch (Exception ex)
			{
				Console.WriteLine($"Connection Error: {ex.Message}");
				
				var txtUrlDisplay = this.FindControl<TextBlock>("TxtServerUrlDisplay");
				if (txtUrlDisplay != null) 
				{
					txtUrlDisplay.Text = $"連線失敗: {ex.Message}";
					txtUrlDisplay.Foreground = Brushes.Red;
				}
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

            if (connGrid != null) connGrid.IsVisible = true;
            if (dashGrid != null) dashGrid.IsVisible = false;
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
                    var t = this.FindControl<TextBlock>("TxtMqttTemp"); if (t != null) t.Text = temp.ToString("F1");
                    var h = this.FindControl<TextBlock>("TxtMqttHum"); if (h != null) h.Text = hum.ToString("F1");
                    var p = this.FindControl<TextBlock>("TxtMqttPres"); if (p != null) p.Text = pres.ToString("F0");
                    var tm = this.FindControl<TextBlock>("TxtMqttTime"); if (tm != null) tm.Text = "Last: " + time;
                }
                else if (sensorId == "esp32_bt_01") // Bluetooth Node
                {
                    var t = this.FindControl<TextBlock>("TxtBtTemp"); if (t != null) t.Text = temp.ToString("F1");
                    var h = this.FindControl<TextBlock>("TxtBtHum"); if (h != null) h.Text = hum.ToString("F1");
                    var p = this.FindControl<TextBlock>("TxtBtPres"); if (p != null) p.Text = pres.ToString("F0");
                    var tm = this.FindControl<TextBlock>("TxtBtTime"); if (tm != null) tm.Text = "Last: " + time;
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
                // 讀取系統狀態
                var sysJson = await _httpClient.GetStringAsync("/api/hw/system-status");
                var sysOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var status = JsonSerializer.Deserialize<SystemStatus>(sysJson, sysOptions);

                if (status != null)
                {
                    var textcuptemp = this.FindControl<TextBlock>("TxtCpuTemp");
                    var textfanspeed = this.FindControl<TextBlock>("TxtFanSpeed");
                    if(textcuptemp != null) textcuptemp.Text = status.CpuTemp.ToString("F1");
                    if (textfanspeed != null) textfanspeed.Text = status.FanSpeed.ToString();

                    var btnAuto = this.FindControl<ToggleButton>("BtnAutoFan");
                    var slider = this.FindControl<Slider>("SliderFan");

                    if (btnAuto != null)
                    {
                        // 只有當狀態不一致時才更新 UI，避免干擾使用者操作
                        if (btnAuto.IsChecked != status.IsAutoFan)
                        {
                            btnAuto.IsChecked = status.IsAutoFan;
                            btnAuto.Content = status.IsAutoFan ? "AUTO MODE" : "MANUAL";
                        }
                    }

                    if (slider != null)
                    {
                        slider.IsEnabled = !status.IsAutoFan; // 自動模式下禁用 Slider
                        // 如果是自動模式，或者手動模式但使用者沒有在拖動，則更新 Slider 位置
                        if (status.IsAutoFan || !_isDraggingSlider)
                        {
                            slider.Value = status.FanSpeed;
                        }
                    }
                }

                // 讀取光感應器
                var sensorJson = await _httpClient.GetStringAsync("/api/hw/sensor");
                using var doc = JsonDocument.Parse(sensorJson);
                int lightVal = doc.RootElement.GetProperty("light").GetInt32();

                var txtLight = this.FindControl<TextBlock>("TxtLightLevel");
                var barLight = this.FindControl<ProgressBar>("BarLight");

                if (txtLight != null) txtLight.Text = lightVal.ToString();
                if (barLight != null) barLight.Value = lightVal;

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
                    var img = this.FindControl<Image>("CamDisplay");
                    if (img != null) img.Source = bitmap;

                    // 注意：OpenCvSharp 在 Android 上可能需要特定的 setup
                    // 若在 Android 上崩潰，請用 try-catch 包裹或拿掉這段
                    try
                    {
                        var mat = Mat.FromImageData(imageBytes, ImreadModes.Color);
                        DetectMotion(mat);
                    }
                    catch { }
                }
            }
            catch { /* 忽略 */ }
        }

        private void UpdateLedStatus(bool[]? states)
        {
            if (states == null || states.Length < 4) return;

            var l1 = this.FindControl<Ellipse>("Led1");
            var l2 = this.FindControl<Ellipse>("Led2");
            var l3 = this.FindControl<Ellipse>("Led3");
            var l4 = this.FindControl<Ellipse>("Led4");

            if (l1 != null) l1.Fill = states[0] ? Brushes.Lime : Brushes.DimGray;
            if (l2 != null) l2.Fill = states[1] ? Brushes.Lime : Brushes.DimGray;
            if (l3 != null) l3.Fill = states[2] ? Brushes.Lime : Brushes.DimGray;
            if (l4 != null) l4.Fill = states[3] ? Brushes.Lime : Brushes.DimGray;
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
                if (txtResult != null) txtResult.Text = "Error: Web Authorization Required first!";
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
                if (txtResult != null) txtResult.Text = $"Network Error: {ex.Message}";
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

            if (txtResponse != null) txtResponse.Text = "Thinking...";

            try
            {
                var payload = new { Text = input };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("/api/ai/command", content);
                var resultJson = await response.Content.ReadAsStringAsync();
                var aiCmd = JsonSerializer.Deserialize<AiCommand>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (txtResponse != null) txtResponse.Text = aiCmd?.Message;

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
                if (txtResponse != null) txtResponse.Text = $"Error: {ex.Message}";
            }
        }

        // --- 鍵盤控制 F1-F4 ---
        // 注意：在手機上可能無實體鍵盤，此功能主要保留給 Desktop
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); // 呼叫基底
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
                // 非同步呼叫，但不等待 (Fire and Forget)
                _ = ToggleLed(ledIndex);
            }
        }

        private async Task ToggleLed(int ledIndex)
        {
			if (_httpClient == null) return;
            try
            {
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

        private void OnOpenLedControl(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentApiUrl)) return;

            var ledOverlay = this.FindControl<LedControlWindow>("LedOverlay");
            if (ledOverlay != null)
            {
                ledOverlay.Init(_currentApiUrl); 
                ledOverlay.IsVisible = true;   
            }
            else
            {
                var txt = this.FindControl<TextBlock>("TxtAiResponse");
                if (txt != null) txt.Text = "Advanced Window not supported on Mobile yet.";
            }
        }

        private async void OnSmartAwayClick(object? sender, RoutedEventArgs e)
        {
            if (_httpClient == null) return;
            var txtStatus = this.FindControl<TextBlock>("TxtSmartStatus");
            var btn = this.FindControl<Button>("BtnSmartAway");

            if (txtStatus != null) txtStatus.Text = "Checking camera...";
            if (btn != null) btn.IsEnabled = false;

            try
            {
                var response = await _httpClient.PostAsync("/api/smart/away", null);
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string message = doc.RootElement.GetProperty("message").GetString() ?? "";
                if (txtStatus != null) txtStatus.Text = message;
            }
            catch (Exception ex)
            {
                if (txtStatus != null) txtStatus.Text = $"Error: {ex.Message}";
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private async void OnAutoFanToggle(object? sender, RoutedEventArgs e)
        {
            if (_httpClient == null) return;
            var btn = sender as ToggleButton;
            bool isAuto = btn?.IsChecked ?? true;

            btn!.Content = isAuto ? "AUTO MODE" : "MANUAL";
            var slider = this.FindControl<Slider>("SliderFan");
            if (slider != null) slider.IsEnabled = !isAuto;

            if (isAuto)
            {
                await _httpClient.PostAsync("/api/hw/fan/auto", null);
            }
            else
            {
                int pwm = (int)(slider?.Value ?? 0);
                await _httpClient.PostAsync($"/api/hw/fan/manual/{pwm}", null);
            }
        }

        private async void OnFanSliderRelease(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_httpClient == null) return;
            if (sender is Slider slider && slider.IsEnabled)
            {
                int pwm = (int)slider.Value;
                await _httpClient.PostAsync($"/api/hw/fan/manual/{pwm}", null);
            }
        }

        public class AiCommand
        {
            public string? Action { get; set; }
            public int[]? Targets { get; set; }
            public string? Message { get; set; }
        }

        public class SystemStatus
        {
            public double CpuTemp { get; set; }
            public int FanSpeed { get; set; }
            public bool IsAutoFan { get; set; }
        }
    }
}