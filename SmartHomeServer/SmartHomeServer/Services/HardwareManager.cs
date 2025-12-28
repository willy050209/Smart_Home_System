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

        public void Init()
        {
            // 確保是在 Linux 環境下執行
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

            try
            {
                // --- 初始化 GPIO ---
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

                // --- SPI 初始化 ---
                try
                {
                    // 檢查 SPI 裝置檔案是否存在
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
                        Console.WriteLine("Warning: SPI device '/dev/spidev0.0' not found. Light sensor disabled.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"SPI Init Failed (Sensor disabled): {ex.Message}");
                    _isSpiEnabled = false;
                }

                Console.WriteLine("Hardware Initialized (SysFs + SPI Status checked).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HW Init Error: {ex.Message}");
            }
        }

        public void SetLed(int id, bool on)
        {
            int idx = id - 1;
            if (idx >= 0 && idx < _ledPins.Length)
            {
                SimpleSysFsGpio.Write(_ledPins[idx], on ? 1 : 0);
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
                    SimpleSysFsGpio.Write(_GPIO388, 1);
                }
                else
                {
                    SimpleSysFsGpio.Write(_GPIO388, 0);
                }
                return sensorValue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading SPI: {ex.Message}");
                return -1;
            }
        }

        public bool[] GetLedStates()
        {
            return _ledStates;
        }
    }
}