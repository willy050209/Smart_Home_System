#include "BluetoothSerial.h"
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
Adafruit_SSD1306 display(SCREEN_WIDTH, SCREEN_HEIGHT, &Wire, OLED_RESET);

// --- BME280 設定 ---
Adafruit_BME280 bme; // 使用 I2C 介面

// --- 藍牙物件宣告 ---
BluetoothSerial SerialBT;

// RY10 (繼電器/除濕機控制)
#define RY10PIN 26
bool ry10_isopen = false;

unsigned long lastMsg = 0;

String sysTime = "--:--:--";
float lastTemp = 0.0;
float lastHum = 0.0;
float lastPress = 0.0;

void setup() {
  Serial.begin(115200);

  // --- 啟動藍牙 ---
  SerialBT.begin("ESP32_Smart_Home"); 
  Serial.println("Bluetooth Started! Ready to pair...");
  
  // 初始化 OLED
  if(!display.begin(SSD1306_SWITCHCAPVCC, 0x3C)) { 
    Serial.println(F("SSD1306 allocation failed"));
  }
  
  display.clearDisplay();
  display.setTextColor(WHITE);
  display.setTextSize(1);
  display.setCursor(0,0);
  display.println("System Booting...");
  display.println("Mode: Bluetooth");
  display.display();

  // 初始化 BME280 (I2C Address 0x76)
  if (!bme.begin(0x76)) {
    Serial.println("Could not find a valid BME280 sensor, check wiring!");
    display.println("BME280 Error!");
    display.display();
    while (1); 
  }
  
  pinMode(RY10PIN, OUTPUT);
  ry10_isopen = false;
  digitalWrite(RY10PIN, 0);

  // 顯示初始畫面
  updateOled();
}

// --- 處理接收到的藍牙指令 ---
void handleBluetoothCommand(String message) {
  message.trim(); // 移除前後空白與換行符號
  
  Serial.print("BT Command received: ");
  Serial.println(message);

  if (message == "led_on") {
    Serial.println("Turn ON LED via Bluetooth Command");
    // 您可以在這裡加入 LED 控制邏輯
  } 
  else if (message.startsWith("TIME:")) {
    // 格式範例: TIME:14:30:00
    sysTime = message.substring(5); 
    updateOled();
    Serial.println("Time updated via Bluetooth");
  }
}

// --- OLED 畫面更新函式 ---
void updateOled() {
  display.clearDisplay();
  
  // 標題列
  display.setTextSize(1);
  display.setCursor(0, 0);
  display.print("Smart Home (BT)"); // 標題改為 BT
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
  // 1. 檢查是否有藍牙資料傳入
  if (SerialBT.available()) {
    // 讀取直到換行符號的字串
    String incomingMsg = SerialBT.readStringUntil('\n');
    handleBluetoothCommand(incomingMsg);
  }

  // 2. 定時讀取感測器並傳送資料
  unsigned long now = millis();
  if (now - lastMsg > 5000) {
    lastMsg = now;
    
    // 讀取 BME280 數據
    float t = bme.readTemperature();
    float h = bme.readHumidity();
    float p = bme.readPressure() / 100.0F; // 轉換為 hPa

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
    doc["sensorId"] = "esp32_bt_01";
    doc["temp"] = t;
    doc["hum"] = h;
    doc["pressure"] = p; 
    
    char buffer[256];
    serializeJson(doc, buffer);
    
    // --- 透過藍牙傳送 JSON 字串 ---
    SerialBT.println(buffer);
    
    Serial.print("BT Sent: ");
    Serial.println(buffer);

    // 除濕機控制邏輯 (RY10)
    if (h > 75.0 && !ry10_isopen)
    {
      digitalWrite(RY10PIN, 1); 
      ry10_isopen = true;
      SerialBT.println("Info: Dehumidifier ON"); 
    }
    else if(h < 70.0 && ry10_isopen)
    {
      digitalWrite(RY10PIN, 0);
      ry10_isopen = false;
      SerialBT.println("Info: Dehumidifier OFF");
    }
  }
}
