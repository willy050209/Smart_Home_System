namespace SmartHomeServer.Services
{
    using Microsoft.AspNetCore.SignalR;
    using MQTTnet;
    using MQTTnet.Client;
    using MQTTnet.Extensions.ManagedClient;
    using MQTTnet.Server;

    public class MqttService : BackgroundService
    {
        private IManagedMqttClient? _mqttClient;
        private readonly IHubContext<SensorHub> _hubContext;

        public MqttService(IHubContext<SensorHub> hubContext)
        {
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {

            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateManagedMqttClient();

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer("localhost", 1883)
                .Build();

            var managedMqttClientOptions = new ManagedMqttClientOptionsBuilder()
                .WithClientOptions(mqttClientOptions)
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);

                if (topic == "home/sensor/livingroom")
                {
                    Console.WriteLine($"[收到 ESP32 數據] Topic: {topic}, Payload: {payload}");
                    await _hubContext.Clients.All.SendAsync("ReceiveSensorData", payload);
                }
            };

            await _mqttClient.StartAsync(managedMqttClientOptions);

            await _mqttClient.SubscribeAsync("home/sensor/livingroom");

            while (!stoppingToken.IsCancellationRequested)
            {
                string timeStr = DateTime.Now.ToString("HH:mm:ss");

                await SendCommandToEsp32($"TIME:{timeStr}");

                await Task.Delay(1000, stoppingToken);
            }
        }

        
        public async Task SendCommandToEsp32(string command)
        {
            if (_mqttClient == null) return;

            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic("home/command/livingroom")
                .WithPayload(command)
                .Build();

            var managedMessage = new ManagedMqttApplicationMessageBuilder()
                .WithApplicationMessage(applicationMessage)
                .Build();

            await _mqttClient.EnqueueAsync(managedMessage);
        }
    }
}