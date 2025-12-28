namespace SmartHomeServer.Services
{
    using System.Text.Json;
    using SmartHomeServer.Models;
    using Google.GenAI;

    public class AiService
    {
        private readonly Client _client;

        public AiService()
        {
            _client = new Client();
        }

        public async Task<AiCommandResponse> GetCommandFromText(string userText)
        {
            // 定義系統提示 (System Prompt)
            var systemPrompt = @"
                You are a smart home assistant for a Jetson TX2 system.
                
                HARDWARE:
                - You control 4 LEDs defined as IDs: 1, 2, 3, 4.
                - ID 1 is usually Red, ID 2 is Green, etc. (Generic LEDs).
                
                YOUR JOB:
                - Analyze the user's natural language command.
                - Map the command to one of these actions: ""on"", ""off"", ""blink"".
                - Identify which target IDs (1-4) to control.
                - Generate a friendly, short response message.
                
                OUTPUT FORMAT (JSON ONLY):
                {
                    ""action"": ""on"" | ""off"" | ""blink"",
                    ""targets"": [1, 2, ...],
                    ""message"": ""Response to user""
                }
                
                EXAMPLES:
                - User: ""Turn on the first light"" -> { ""action"": ""on"", ""targets"": [1], ""message"": ""Turning on LED 1."" }
                - User: ""Lights out!"" -> { ""action"": ""off"", ""targets"": [1, 2, 3, 4], ""message"": ""Turning off all lights."" }
                - User: ""Alert mode"" -> { ""action"": ""blink"", ""targets"": [1, 2, 3, 4], ""message"": ""Activating alert sequence."" }
                
                User input: ";

            try
            {
                // 使用 Google.GenAI SDK 發送請求
                var response = await _client.Models.GenerateContentAsync(
                    model: "gemini-2.5-flash",
                    contents: systemPrompt + userText
                );

                // 取得回應文字
                string? text = response?.Candidates?[0]?.Content?.Parts?[0]?.Text;

                // 清理與反序列化 
                if (!string.IsNullOrEmpty(text))
                {
                    // 移除可能的 Markdown JSON 標記
                    text = text.Trim().Replace("```json", "").Replace("```", "");

                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var command = JsonSerializer.Deserialize<AiCommandResponse>(text, options);

                    if (command != null) return command;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AI Service SDK Error: {ex.Message}");

                return new AiCommandResponse
                {
                    Action = "none",
                    Targets = [],
                    Message = "Sorry, I couldn't process that command right now."
                };
            }

            return new AiCommandResponse { Action = "none", Targets = [], Message = "I didn't understand that." };
        }
    }
}