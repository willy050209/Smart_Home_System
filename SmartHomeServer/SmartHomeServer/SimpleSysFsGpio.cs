namespace SmartHomeServer
{
    using System;
    using System.IO;
    using System.Threading;
    /// <summary>
    /// Provides static methods for basic GPIO pin control using the Linux sysfs interface.
    /// </summary>
    /// <remarks>This class enables exporting, unexporting, configuring, and writing to GPIO pins by interacting with
    /// the /sys/class/gpio directory on Linux systems. It is intended for simple GPIO operations and does not provide
    /// advanced error handling or asynchronous support. All methods are static and can be used without creating an instance
    /// of the class. These methods require appropriate permissions to access the sysfs GPIO interface, and may not work on
    /// all Linux distributions or hardware platforms.</remarks>
    public static class SimpleSysFsGpio
    {
        private const string GpioRoot = "/sys/class/gpio";

        public static void Export(int pin)
        {
            string pinPath = Path.Combine(GpioRoot, $"gpio{pin}");
            if (!Directory.Exists(pinPath))
            {
                try
                {
                    File.WriteAllText(Path.Combine(GpioRoot, "export"), pin.ToString());
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"無法匯出 GPIO {pin}: {ex.Message}");
                }
            }
        }

        public static void Unexport(int pin)
        {
            try
            {
                string pinPath = Path.Combine(GpioRoot, $"gpio{pin}");
                if (Directory.Exists(pinPath))
                {
                    File.WriteAllText(Path.Combine(GpioRoot, "unexport"), pin.ToString());
                }
            }
            catch { }
        }

        public static void SetDirection(int pin, string direction)
        {
            try
            {
                string path = Path.Combine(GpioRoot, $"gpio{pin}", "direction");
                File.WriteAllText(path, direction);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"設定 GPIO {pin} 方向失敗: {ex.Message}");
            }
        }

        public static void Write(int pin, int value)
        {
            try
            {
                string path = Path.Combine(GpioRoot, $"gpio{pin}", "value");
                File.WriteAllText(path, value.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"寫入 GPIO {pin} 失敗: {ex.Message}");
            }
        }
    }
}
