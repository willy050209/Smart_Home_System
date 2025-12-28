namespace SmartHomeServer.Models
{
    // --- 資料模型 ---
    record UserInput(string Text);
    record AuthDataRequest(string Password, bool Success);
    public class AiCommandResponse
    {
        public string Action { get; set; } = string.Empty;
        public int[] Targets { get; set; } = [];
        public string Message { get; set; } = string.Empty;
    }
}
