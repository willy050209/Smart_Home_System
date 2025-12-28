import socket
import paho.mqtt.client as mqtt
import time
import sys
import threading

# --- 設定 ---
ESP32_MAC = "10:97:BD:31:E2:5A"
PORT = 1
MQTT_BROKER = "localhost"
MQTT_TOPIC_RX = "home/sensor/livingroom"   
MQTT_TOPIC_TX = "home/command/livingroom"  

# 全域變數用來儲存 Socket
bt_socket = None

# --- MQTT 回呼函式 ---
def on_connect(client, userdata, flags, rc):
    print(f"[MQTT] Connected with result code {rc}")
    # 連線成功後，訂閱 Server 發送指令的 Topic
    client.subscribe(MQTT_TOPIC_TX)
    print(f"[MQTT] Subscribed to {MQTT_TOPIC_TX}")

def on_message(client, userdata, msg):
    global bt_socket
    try:
        # 1. 取得 Server 傳來的訊息 (例如 "TIME:12:34:56")
        payload = msg.payload.decode("utf-8")
        
        # 2. 檢查藍牙是否連線中
        if bt_socket:
            print(f"[Bridge] MQTT -> BT: {payload}")
            
            # 3. 補上換行符號 (ESP32 的 readStringUntil('\n') 需要這個)
            if not payload.endswith('\n'):
                payload += '\n'
                
            # 4. 透過藍牙發送
            bt_socket.send(payload.encode())
        else:
            print("[Bridge] Drop message (Bluetooth not connected)")
            
    except Exception as e:
        print(f"[Bridge] Forward Error: {e}")

# --- 初始化 MQTT ---
client = mqtt.Client()
client.on_connect = on_connect
client.on_message = on_message # 綁定接收訊息的處理函式

try:
    client.connect(MQTT_BROKER, 1883, 60)
    client.loop_start() # 啟動背景執行緒處理 MQTT
except Exception as e:
    print(f"MQTT Init Failed: {e}")
    sys.exit(1)

# --- 藍牙連線與讀取迴圈 ---
def connect_bluetooth():
    s = socket.socket(socket.AF_BLUETOOTH, socket.SOCK_STREAM, socket.BTPROTO_RFCOMM)
    try:
        print(f"Connecting to ESP32 ({ESP32_MAC})...")
        s.connect((ESP32_MAC, PORT))
        print("Bluetooth Connected!")
        return s
    except Exception as e:
        print(f"BT Connection Error: {e}")
        s.close()
        return None

while True:
    # 嘗試連線
    bt_socket = connect_bluetooth()
    
    if bt_socket:
        try:
            # 進入讀取迴圈 (阻斷式)
            buffer = ""
            while True:
                data = bt_socket.recv(1024)
                if not data:
                    break
                
                text = data.decode("utf-8", errors='ignore')
                buffer += text
                
                # 處理來自 ESP32 的訊息 (以換行分割)
                while '\n' in buffer:
                    line, buffer = buffer.split('\n', 1)
                    line = line.strip()
                    if line:
                        # 如果是 JSON 格式就轉發給 Server
                        if line.startswith("{") and line.endswith("}"):
                            print(f"[Bridge] BT -> MQTT: {line}")
                            client.publish(MQTT_TOPIC_RX, line)
                        else:
                            print(f"[Bridge] Log: {line}")
                            
        except Exception as e:
            print(f"Bluetooth Error: {e}")
            try:
                bt_socket.close()
            except:
                pass
            bt_socket = None
    
    print("Reconnecting Bluetooth in 5 seconds...")
    time.sleep(5)
