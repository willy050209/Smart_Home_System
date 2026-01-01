using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SmartHomeServer.Models;
using SmartHomeServer.Services;

var builder = WebApplication.CreateBuilder(args);

// --- 註冊服務 ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSignalR();

// 註冊硬體單例服務 (Singleton: 整個生命週期只初始化一次)
builder.Services.AddSingleton<BlackboxDriver>();
builder.Services.AddSingleton<HardwareManager>();
builder.Services.AddSingleton<CameraService>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddHostedService<MqttService>();
builder.Services.AddHostedService<BluetoothService>();

var app = builder.Build();

// --- 初始化硬體 ---
// 當 Server 啟動時，自動打開 Driver 與 GPIO
var driver = app.Services.GetRequiredService<BlackboxDriver>();
driver.OpenDriver();
var hw = app.Services.GetRequiredService<HardwareManager>();
hw.Init();
var cam = app.Services.GetRequiredService<CameraService>();
cam.StartCapture();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseStaticFiles();
app.UseRouting();

// --- API 定義 ---

// 協同授權 
const string AUTH_CACHE_KEY = "UserAuthorized";
app.MapPost("/api/auth/authorize", (IMemoryCache cache) => {
    cache.Set(AUTH_CACHE_KEY, true, TimeSpan.FromSeconds(30));
    return Results.Ok(new { message = "Authorized for 30s" });
});
app.MapGet("/api/auth/status", (IMemoryCache cache) => {
    bool auth = cache.TryGetValue(AUTH_CACHE_KEY, out bool status) && status;
    return Results.Ok(new { authorized = auth });
});

// AI 指令
app.MapPost("/api/ai/command", async (AiService ai, [FromBody] UserInput input) => {
    try
    {
        var result = await ai.GetCommandFromText(input.Text);
        return Results.Ok(result);
    }
    catch (Exception ex) { return Results.Problem(ex.Message); }
});

// 硬體控制 API
app.MapPost("/api/hw/led/{id}/{state}", (HardwareManager hw, int id, bool state) => {
    hw.SetLed(id, state);
    return Results.Ok();
});

app.MapGet("/api/hw/sensor", (HardwareManager hw) => {
    return Results.Ok(new { light = hw.ReadLightSensor() });
});

// 驅動程式 API (/dev/blackbox)
app.MapPost("/api/driver/log", (BlackboxDriver drv, [FromBody] AuthDataRequest req) => {
    drv.WriteLog(req.Password, req.Success);
    return Results.Ok();
});

app.MapGet("/api/driver/logs", (BlackboxDriver drv) => {
    var logs = drv.ReadLogs().Select(e => new
    {
        e.Password,
        e.Result,
        e.Timestamp
    });
    return Results.Ok(logs);
});

app.MapGet("/api/driver/dmesg", () => {
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dmesg | grep BlackBox", // 呼叫 Linux 系統指令
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return Results.Problem("Failed to start dmesg process");

        // 讀取所有輸出
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // 過濾與處理
        var logs = output.Split('\n')
                         .Where(line => line.Contains("BlackBox", StringComparison.OrdinalIgnoreCase))
                         .Select(line => line.Trim())
                         .Where(line => !string.IsNullOrEmpty(line))
                         .Reverse()
                         .Take(100);

        return Results.Ok(logs);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error executing dmesg: {ex.Message}. Make sure 'util-linux' is installed and container is privileged.");
    }
});

// 相機影像 API (MJPEG Frame)
app.MapGet("/api/camera/frame", (CameraService cam) => {
    var bytes = cam.GetLatestFrame();
    if (bytes == null || bytes.Length == 0) return Results.NotFound();
    return Results.File(bytes, "image/jpeg");
});

// 取得所有 LED 狀態的 API
app.MapGet("/api/hw/leds", (HardwareManager hw) => {
    return Results.Ok(hw.GetLedStates());
});

// 人臉偵測無人自動關燈 API
app.MapPost("/api/smart/away", (CameraService cam, HardwareManager hw) => {
    int people = cam.PersonCount;

    if (people > 0)
    {
        return Results.Ok(new
        {
            success = false,
            message = $"偵測到 {people} 人，系統保持開啟。"
        });
    }
    else
    {
        hw.SetLed(1, false);
        hw.SetLed(2, false);
        hw.SetLed(3, false);
        hw.SetLed(4, false);

        return Results.Ok(new
        {
            success = true,
            message = "環境中無人，已自動關閉所有燈光。"
        });
    }
});

app.MapGet("/api/hw/system-status", (HardwareManager hw) => {
    return Results.Ok(hw.GetSystemStatus());
});

app.MapPost("/api/hw/fan/manual/{pwm}", (HardwareManager hw, int pwm) => {
    hw.SetManualFan(pwm);
    return Results.Ok(new { message = $"Manual Fan Speed set to {pwm}" });
});

app.MapPost("/api/hw/fan/auto", (HardwareManager hw) => {
    hw.SetAutoFan();
    return Results.Ok(new { message = "Fan set to Auto Mode" });
});

// --- 網頁路由 ---
app.MapGet("/", () => Results.Content(GetIndexContent(), "text/html"));
app.MapGet("/monitor", () => Results.Content(GetmMonitorContent(), "text/html"));

app.MapHub<SensorHub>("/sensorHub");

System.Console.WriteLine("伺服器啟動: http://<TX2_IP>:8080");
app.Run("http://0.0.0.0:8080");


static string GetIndexContent() => File.ReadAllText("./Pages/index.html");
static string GetmMonitorContent() => File.ReadAllText("./Pages/monitor.html");