namespace SmartHomeServer.Services
{
    using Microsoft.AspNetCore.SignalR;
    using System.IO.Ports;
    using System.Threading;
    using System.Threading.Tasks;

    public class BluetoothService : BackgroundService
    {
        private readonly IHubContext<SensorHub> _hubContext;
        private SerialPort? _serialPort;
        private const string PortName = "/dev/rfcomm0"; 
        private const int BaudRate = 115200; 

        public BluetoothService(IHubContext<SensorHub> hubContext)
        {
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        InitializeSerialPort();
                    }

                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        // 讀取來自 ESP32 的資料
                        string message = _serialPort.ReadLine(); 

                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            //Console.WriteLine($"[BT 收到數據] {message}");

                            await _hubContext.Clients.All.SendAsync("ReceiveSensorData", message, stoppingToken);
                        }
                    }
                }
                catch (TimeoutException) { /* 忽略讀取超時 */ }
                catch (Exception ex)
                {
                    //Console.WriteLine($"[BT Error] {ex.Message}. Retrying in 5s...");
                    CloseSerialPort();
                    await Task.Delay(5000, stoppingToken);
                }

                await Task.Delay(10, stoppingToken);
            }
        }

        private void InitializeSerialPort()
        {
            try
            {
                if (System.IO.File.Exists(PortName))
                {
                    _serialPort = new SerialPort(PortName, BaudRate)
                    {
                        ReadTimeout = 1000,
                        WriteTimeout = 1000
                    };
                    _serialPort.Open();
                    //Console.WriteLine($"[BT] Connected to {PortName}");
                }
                else
                {
                    //Console.WriteLine($"[BT] Device {PortName} not found. Waiting...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BT] Connection Failed: {ex.Message}");
            }
        }

        private void CloseSerialPort()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        // 傳送指令給 ESP32 
        public void SendCommand(string command)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.WriteLine(command);
            }
        }
    }
}