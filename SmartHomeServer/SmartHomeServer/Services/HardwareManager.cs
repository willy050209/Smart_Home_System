namespace SmartHomeServer.Services
{
    using Iot.Device.Adc;
    using System.Device.Spi;
    using System.Runtime.InteropServices;
    public class HardwareManager
    {

        private Mcp3008? _adc;
        private SpiDevice? _spi;
        private bool _isSpiEnabled = false; // 標記 SPI 是否可用
        private const int SpiBusId = 3;
        private const int ChipSelectLine = 0;
        private const int ClockFrequency = 1000000;

        // LED 狀態陣列
        private bool[] _ledStates = new bool[4];

        // TX2 的 GPIO 腳位編號
        private readonly int[] _ledPins = [396, 466, 397, 255];

        private readonly int _GPIO388 = 388;

        private const string ThermalPath = "/sys/devices/virtual/thermal/thermal_zone0/temp";
        private const string FanPwmPath = "/sys/devices/pwm-fan/target_pwm";

        private Timer? _fanControlTimer;
        private bool _isAutoFan = true; 
        private int _currentFanSpeed = 0;
        private double _currentCpuTemp = 0.0;

        public void Init()
        {
            // 確保是在 Linux 環境下執行
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

            try
            {
                Console.WriteLine("Initializing GPIO via SysFs...");
                foreach (var pin in _ledPins)
                {
                    SimpleSysFsGpio.Export(pin);
                    SimpleSysFsGpio.SetDirection(pin, "out");
                    SimpleSysFsGpio.Write(pin, 0);
                }
                SimpleSysFsGpio.Export(_GPIO388);
                SimpleSysFsGpio.SetDirection(_GPIO388, "out");
                SimpleSysFsGpio.Write(_GPIO388, 0);

                try
                {
                    if (File.Exists("/dev/spidev3.0"))
                    {
                        var settings = new SpiConnectionSettings(SpiBusId, ChipSelectLine) { ClockFrequency = ClockFrequency, Mode = SpiMode.Mode0 };
                        _spi = SpiDevice.Create(settings);
                        _adc = new Mcp3008(_spi);
                        _isSpiEnabled = true;
                        Console.WriteLine("SPI (MCP3008) Initialized successfully.");
                    }
                    else
                    {
                        Console.WriteLine("Warning: SPI device not found.");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"SPI Init Failed: {ex.Message}"); }

                _fanControlTimer = new Timer(FanControlLoop, null, 0, 2000);
                Console.WriteLine("System Monitor & Fan Control Started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HW Init Error: {ex.Message}");
            }
        }

        private void FanControlLoop(object? state)
        {
            try
            {
                // 1. 讀取溫度
                if (File.Exists(ThermalPath))
                {
                    string tempStr = File.ReadAllText(ThermalPath).Trim();
                    if (double.TryParse(tempStr, out double tempRaw))
                    {
                        // Linux thermal zone 通常是 1000 倍 (例如 45000 代表 45度)
                        _currentCpuTemp = tempRaw / 1000.0;
                    }
                }

                // 2. 自動風扇控制邏輯
                if (_isAutoFan)
                {
                    int targetPwm = 0;

                    if (_currentCpuTemp < 40) targetPwm = 0;       // 低溫靜音
                    else if (_currentCpuTemp > 70) targetPwm = 255; // 高溫全速
                    else
                    {
                        // 線性插值: 40度~70度 對應 PWM 50~255
                        // y = mx + c
                        // m = (255 - 50) / (70 - 40) = 205 / 30 ~= 6.83
                        targetPwm = 50 + (int)((_currentCpuTemp - 40) * 6.83);
                    }

                    SetFanSpeedInternal(targetPwm);
                }
            }
            catch { /* 忽略讀取錯誤，避免 crash */ }
        }

        private void SetFanSpeedInternal(int pwm)
        {
            if (pwm < 0) pwm = 0;
            if (pwm > 255) pwm = 255;

            // 只有當數值改變時才寫入檔案，減少 IO
            if (_currentFanSpeed != pwm)
            {
                try
                {
                    if (File.Exists(FanPwmPath))
                    {
                        File.WriteAllText(FanPwmPath, pwm.ToString());
                        _currentFanSpeed = pwm;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Fan Control Error: {ex.Message}");
                }
            }
        }

        public void SetManualFan(int pwm)
        {
            _isAutoFan = false; // 切換為手動模式
            SetFanSpeedInternal(pwm);
        }

        public void SetAutoFan()
        {
            _isAutoFan = true; // 切換回自動模式
            // 下一次 Timer Tick 會自動修正轉速
        }

        public Models.SystemStatus GetSystemStatus()
        {
            return new Models.SystemStatus
            {
                CpuTemp = _currentCpuTemp,
                FanSpeed = _currentFanSpeed,
                IsAutoFan = _isAutoFan
            };
        }

        public void SetLed(int id, bool on)
        {
            int idx = id - 1;
            if (idx >= 0 && idx < _ledPins.Length)
            {
                SimpleSysFsGpio.Write(_ledPins[idx], on ? 0 : 1);
                _ledStates[idx] = on;
            }
        }


        public int ReadLightSensor()
        {
            if (!_isSpiEnabled || _adc == null) return -1;

            try
            {
                var sensorValue = _adc.Read(0);
                if(sensorValue > 600 )
                {
                    SimpleSysFsGpio.Write(_GPIO388, 0);
                }
                else
                {
                    SimpleSysFsGpio.Write(_GPIO388, 1);
                }
                return sensorValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading SPI: {ex.Message}");
                return -1;
            }
        }

        public bool[] GetLedStates() => _ledStates;

        public void Dispose()
        {
            _fanControlTimer?.Dispose();
            _spi?.Dispose();
            _adc = null; 
        }
    }
}