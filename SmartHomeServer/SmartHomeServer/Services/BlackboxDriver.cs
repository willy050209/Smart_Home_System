namespace SmartHomeServer.Services
{
    using System.Runtime.InteropServices;
    using SmartHomeServer.Models;
    public class BlackboxDriver
    {
        private readonly List<string> _logs = [];
        private const string DriverPath = "/dev/blackbox";
        private int _fd = -1; 

        // Linux syscalls
        [DllImport("libc", EntryPoint = "open")] private static extern int open(string p, int f);
        [DllImport("libc", EntryPoint = "close")] private static extern int close(int fd);
        [DllImport("libc", EntryPoint = "ioctl")] private static extern int ioctl(int fd, int r, ref AuthData d);
        [DllImport("libc", EntryPoint = "read")] private static extern int read(int fd, IntPtr b, int c);


        private const int IOCTL_WRITE_LOG = 0x40186b01; // 請確認此數值正確


        public void OpenDriver()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _fd = open(DriverPath, 2); // O_RDWR
                Console.WriteLine("[Info] Blackbox driver opened for writing.");
                if (_fd < 0) Console.WriteLine($"[Error] Cannot open {DriverPath}");
            }
        }

        public void WriteLog(string pwd, bool success)
        {
            if (_fd < 0) return;
            var data = new AuthData { Password = pwd, Result = success ? 1 : 0 };
            _logs.Add($"Time: {DateTimeOffset.Now}, Password: {pwd}, Result: {(success ? "Success" : "Failure")}");
            Console.WriteLine("[Info] Blackbox driver write.");
            ioctl(_fd, IOCTL_WRITE_LOG, ref data);
        }

        public List<LogEntry> ReadLogs()
        {
            //return _logs;
            var logs = new List<LogEntry>();

            // 每次讀取都重新開啟檔案，確保 offset 從 0 開始
            // 0 = O_RDONLY
            int readFd = -1;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                readFd = open(DriverPath, 0);
            }

            if (readFd < 0) return logs; // 無法開啟

            int size = Marshal.SizeOf<LogEntry>();
            // 預配足夠的緩衝區 (100 筆資料)
            int bufferSize = size * 100;
            IntPtr ptr = Marshal.AllocHGlobal(bufferSize);

            try
            {
                Console.WriteLine("[Info] Blackbox driver read (new fd).");
                int bytes = read(readFd, ptr, bufferSize);

                if (bytes > 0)
                {
                    int count = bytes / size;
                    for (int i = 0; i < count; i++)
                    {
                        var entry = Marshal.PtrToStructure<LogEntry>(IntPtr.Add(ptr, i * size));
                        // 過濾掉時間戳記為 0 的無效資料
                        if (entry.Timestamp != 0) logs.Add(entry);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
                close(readFd);
            }

            logs.Reverse(); // 讓最新的顯示在最上面
            return logs;
        }
    }
}