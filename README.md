# **Smart Home System (智慧家庭系統)**

這是一個整合了 **IoT 硬體控制**、**AI 語音指令**、**邊緣運算** 與 **Linux Kernel 驅動** 的完整智慧家庭解決方案。系統核心運行於 **NVIDIA Jetson TX2**，並透過 Docker 容器化部署 ASP.NET Core 伺服器，整合了 ESP32 感測節點、藍牙橋接器與 Avalonia 跨平台客戶端。

## **專案架構**

本專案包含以下五大核心模組：

1. **SmartHomeServer**: 運行於 Jetson TX2 的核心伺服器 (ASP.NET Core)。負責硬體控制 (GPIO/SPI/Camera)、AI 處理、MQTT Broker 溝通與 API 服務。  
2. **SmartHomeClient**: 跨平台桌面控制端 (Avalonia UI)。提供即時影像監控、儀表板、AI 聊天室與黑盒子日誌查看。  
3. **Blackbox Driver**: Linux Kernel Module (字元驅動裝置)。實作核心層級的安全性日誌記錄 (/dev/blackbox)。  
4. **ESP32 Nodes**:  
   * **MQTT Node**: 透過 WiFi 傳送溫濕度數據，具備自動除濕控制功能。  
   * **Bluetooth Node**: 透過藍牙傳送數據，適用於無 WiFi 環境的節點。  
5. **BT Bridge**: Python 腳本。作為藍牙與 MQTT 之間的橋樑，將藍牙節點數據轉發至伺服器。

## **硬體需求**

* **Server**: NVIDIA Jetson TX2 (或相容的 Linux ARM64/x64 設備)  
* **Client**: 
 * Windows / macOS / Linux 電腦  
 * Mobile: Android Phone / Tablet (Android 10.0+ 建議)
* **Nodes**: ESP32 開發板 x2  
* **Sensors & Actuators**:  
  * BME280 (溫濕度氣壓感測器)  
  * SSD1306 OLED 顯示器  
  * MCP3008 (ADC 轉換器，用於 TX2 光敏電阻)  
  * Relay Module (繼電器，用於除濕機控制)  
  * USB/CSI Camera  
  * LEDs (GPIO 控制)

## **安裝與部署指南**

### **1\. Blackbox Driver (Linux Kernel Module)**

在啟動伺服器之前，必須先在 Host OS (TX2) 上編譯並載入驅動程式。

``` Bash
cd blackbox\_driver  
```
``` Bash
make  
```
``` Bash
make load
```


* **驗證**: 執行 ls \-l /dev/blackbox 確認裝置節點是否存在。  
* **卸載**: make unload

### **2\. SmartHomeServer (Docker 部署)**

伺服器負責整合所有服務。請確保您已安裝 Docker。

**步驟一：建置 Docker Image**
``` Bash
cd SmartHomeServer/SmartHomeServer  
```

注意：根據 Dockerfile，基礎映像檔為 .NET 10 
``` Bash
docker build \-t smarthome-server .
```

**步驟二：啟動容器 (Privileged Mode)**

由於需要存取 /dev/blackbox、GPIO、SPI 與 Camera，必須使用 \--privileged 模式並掛載 /dev。
``` Bash
docker run -d \
  --name my-smarthome \
  --restart always \
  --privileged \
  --network host \
  -v /dev:/dev \
  -e GOOGLE_API_KEY="Gemini_API_KEY" \
  smarthome-server
```
* **API 網址**: http://\<TX2\_IP\>:8080  
* **Swagger 文件**: http://\<TX2\_IP\>:8080/swagger  
* **Web 監控頁面**: http://\<TX2\_IP\>:8080/monitor

### **3\. ESP32 Nodes (Arduino)**

請使用 Arduino IDE 燒錄以下韌體。

**必備 Library**:

* Adafruit BME280 Library  
* Adafruit SSD1306  
* PubSubClient (僅 MQTT Node 需要)  
* ArduinoJson

#### **Node A: MQTT Version (客廳)**

* 路徑: ESP32/Smart\_Home\_Sense\_Unit\_MQTT  
* 修改 Smart\_Home\_Sense\_Unit\_MQTT.ino 中的 WiFi SSID/Password 與 MQTT Server IP (TX2 IP)。  
* **功能**: 自動偵測濕度，若 \> 75% 自動開啟繼電器 (除濕機)。

#### **Node B: Bluetooth Version (臥室)**

* 路徑: ESP32/Smart\_Home\_Sense\_Unit\_Bluetooth  
* **功能**: 透過 Bluetooth Serial 廣播數據，等待 Bridge 連線。

### **4\. Bluetooth Bridge (Python Gateway)**

此腳本需運行於擁有藍牙功能的設備上 (如 TX2 本機或 Raspberry Pi)，負責將藍牙數據轉發至 MQTT。

**安裝依賴**:

``` Bash
pip install paho-mqtt
```

**設定與執行**:

1. 修改 bt\_bridge/bt\_bridge.py 中的 ESP32\_MAC (請先透過手機或電腦配對確認 ESP32 的 MAC 地址)。  
2. 修改 MQTT\_BROKER 為伺服器 IP。  
3. 執行腳本：  
``` Bash
   python3 bt\_bridge/bt\_bridge.py
```

### **5\. SmartHomeClient (App)**

支援 Desktop 與 Android 雙平台。

* **開發環境需求:**
 * Visual Studio 2022 (安裝 .NET Multi-platform App UI 開發負載) 或 Rider。
 * Android SDK (若要部署至 Android)。
 
* **部署步驟:** 
1. 開啟 SmartHomeClient/SmartHomeClient/SmartHomeClient.csproj (或 .slnx)。
2. ** Desktop (Windows/Mac/Linux): **
 * 選擇 net8.0 作為目標框架。
 * 執行專案即可開啟視窗程式。
3. **Android:**
 * 選擇 net8.0-android 作為目標框架。
 * 連接 Android 手機 (開啟 USB 偵錯) 或使用模擬器。
 * 部署並執行 App。

## **功能操作說明**

### **Client 端功能**

* **Dashboard**: 即時顯示 MQTT 與 Bluetooth 節點的溫濕度數據，以及 TX2 本地的光照強度。  
* **Camera Feed**: 顯示 TX2 鏡頭畫面，具備簡易的動態偵測 (Motion Detection) 警示。  
* **LED Control**:  
  * **手動**: F1-F4 鍵或介面開關控制 TX2 上的 LED。  
  * **進階閃爍**: 支援跑馬燈、同步閃爍等模式。  
* **AI Command**: 在對話框輸入自然語言 (例如 "Turn on the red light" 或 "開啟警示模式")，系統會透過 Gemini API 解析意圖並控制硬體。  
* **Blackbox Logs**: 需先進行密碼驗證 (預設 1234)，驗證成功後會寫入 Kernel Driver，並可讀取核心層級的存取紀錄。  
* **Smart Away**: 透過人臉偵測判斷是否有人，若無人則自動關閉所有燈光。

### **Web 監控介面 (/monitor)**

* 提供一個輕量級的 HTML Dashboard，即時顯示所有感測器數值 (透過 SignalR 更新)。  
* 具備 "安全協同授權" 功能：Client 端進行高權限操作時，需先在網頁端點擊「授權」按鈕。

### **自動化邏輯**

* **除濕自動化**: MQTT Node 若偵測濕度 \> 75%，會自動觸發 GPIO 26 (Relay) 開啟除濕機；\< 70% 自動關閉。  
* **藍牙轉發**: Bridge 收到藍牙節點數據後，會自動封裝成 JSON 轉發至 MQTT Topic home/sensor/livingroom，Server 收到後推播至前端。

## **API 參考**

| Method | Endpoint | Description |
| :---- | :---- | :---- |
| POST | /api/ai/command | 發送自然語言指令控制家電 |
| GET | /api/camera/frame | 取得即時影像截圖 (MJPEG) |
| POST | /api/hw/led/{id}/{state} | 控制指定 LED (1-4) 開關 |
| GET | /api/driver/logs | 讀取 Blackbox 核心日誌 |
| POST | /api/smart/away | 執行無人自動關燈檢測 |

## **常見問題排除**

1. **Docker 內無法存取 /dev/blackbox?**  
   * 請確認 Host OS 已執行 insmod。  
   * 請確認 Docker 啟動指令包含 \--privileged 和 \-v /dev:/dev。  
2. **藍牙 Bridge 無法連線?**  
   * 請確認 ESP32 已上電且未被其他裝置連線。  
   * 請確認 Python 腳本中的 MAC Address 正確。  
3. **Client 連線失敗?**  
   * 請確認 TX2 防火牆允許 8080 Port。  
   * 若在同一區網，請使用 TX2 的區域網路 IP (如 192.168.x.x) 而非 localhost。

開發者: Willy050209  
版本: v1.1.0 (Added Android Support)