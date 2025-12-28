#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>
#include <Adafruit_Sensor.h>
#include <Adafruit_BME280.h>

// --- OLED 設定 ---
#define SCREEN_WIDTH 128 // OLED 寬度
#define SCREEN_HEIGHT 64 // OLED 高度
#define OLED_RESET    -1 // 重置腳位 
// LT-ESP32 預設 I2C 腳位: SDA=21, SCL=22
Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET);

// --- BME280 設定 ---
Adafruit_BME280 bme; // 使用 I2C 介面

const char* ssid = "YOUR_SSID"
const char* password = "YOUR_PASSWORD"
const char* mqtt_server = "192.168.137.104"; 

// RY10 (繼電器/除濕機控制)
#define RY10PIN 26
bool ry10_isopen = false;

WiFiClient espClient;
PubSubClient client(espClient);

unsigned long lastMsg = 0;

String sysTime = "--:--:--";
float lastTemp = 0.0;
float lastHum = 0.0;
float lastPress = 0.0;

void setup() {
  Serial.begin(115200);
  
  // 初始化 OLED
  if(!display.begin(SSD1306_SWITCHCAPVCC, 0x3C)) { 
    Serial.println(F("SSD1306 allocation failed"));
  }
  
  display.clearDisplay();
  display.setTextColor(WHITE);
  display.setTextSize(1);
  display.setCursor(0,0);
  display.println("System Booting...");
  display.println("Init BME280...");
  display.display();

  // 初始化 BME280
  // 使用位址 0x76 (SDO=0)
  if (!bme.begin(0x76)) {
    Serial.println("Could not find a valid BME280 sensor, check wiring!");
    display.println("BME280 Error!");
    display.display();
    while (1); 
  }

  setup_wifi();
  client.setServer(mqtt_server, 1883);
  client.setCallback(callback); 
  
  pinMode(RY10PIN, OUTPUT);
  ry10_isopen = false;
  digitalWrite(RY10PIN, 0);
}

void setup_wifi() {
  delay(10);
  Serial.print("Connecting to ");
  Serial.println(ssid);
  
  display.clearDisplay();
  display.setCursor(0,0);
  display.println("Connecting WiFi...");
  display.display();

  WiFi.begin(ssid, password);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println("\nWiFi connected");
}

// --- 接收並處理伺服器訊息 ---
void callback(char* topic, byte* payload, unsigned int length) {
  String message;
  for (int i = 0; i < length; i++) {
    message += (char)payload[i];
  }
  Serial.print("Message arrived [");
  Serial.print(topic);
  Serial.print("] ");
  Serial.println(message);

  if (message == "led_on") {
    Serial.println("Turn ON LED via Remote Command");
  } 
  else if (message.startsWith("TIME:")) {
    sysTime = message.substring(5); 
    updateOled();
  }
}

void reconnect() {
  while (!client.connected()) {
    Serial.print("Attempting MQTT connection...");

    if (client.connect("ESP32_LivingRoom")) {
      Serial.println("connected");
      client.subscribe("home/command/livingroom");
    } else {
      Serial.print("failed, rc=");
      Serial.print(client.state());
      delay(5000);
    }
  }
}

// --- OLED 畫面更新函式 ---
void updateOled() {
  display.clearDisplay();
  
  // 標題列
  display.setTextSize(1);
  display.setCursor(0, 0);
  display.print("Smart Home (MQTT)");
  display.drawLine(0, 12, 128, 12, WHITE); 

  // 溫度 
  display.setCursor(0, 16);
  display.print("Temp: "); 
  display.print(lastTemp, 1);
  display.print(" C");

  // 濕度 
  display.setCursor(0, 28);
  display.print("Humi: "); 
  display.print(lastHum, 1);
  display.print(" %");

  // 氣壓 
  display.setCursor(0, 40);
  display.print("Pres: "); 
  display.print(lastPress, 0); 
  display.print(" hPa");

  // 時間顯示 
  display.setCursor(0, 54);
  display.print("Time: ");
  display.print(sysTime);

  display.display();
}

void loop() {
  if (!client.connected()) {
    reconnect();
  }
  client.loop();

  unsigned long now = millis();
  if (now - lastMsg > 5000) {
    lastMsg = now;
    
    // 讀取 BME280 數據
    float t = bme.readTemperature();
    float h = bme.readHumidity();
    float p = bme.readPressure() / 100.0F; // 轉換為 hPa

    // 檢查讀取是否失敗
    if (isnan(h) || isnan(t) || isnan(p)) {
      Serial.println("Failed to read from BME sensor!");
      return;
    }

    lastTemp = t;
    lastHum = h;
    lastPress = p;
    updateOled(); 

    // 打包 JSON 資料
    StaticJsonDocument<256> doc;
    doc["sensorId"] = "esp32_01";
    doc["temp"] = t;
    doc["hum"] = h;
    doc["pressure"] = p; 
    
    char buffer[256];
    serializeJson(doc, buffer);
    client.publish("home/sensor/livingroom", buffer);
    Serial.print("Published: ");
    Serial.println(buffer);

    // 除濕機控制邏輯 (RY10)
    if (h > 75.0 && !ry10_isopen)
    {
      digitalWrite(RY10PIN, 1); // 高電位觸發
      ry10_isopen = true;
      Serial.println("dehumidifier is open ");
    }
    else if(h < 70.0 && ry10_isopen)
    {
      digitalWrite(RY10PIN, 0);
      ry10_isopen = false;
      Serial.println("dehumidifier is close ");
    }
  }
}
