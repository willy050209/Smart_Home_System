# 階段 1: 建置 (Build)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 複製專案檔並還原依賴
COPY ["SmartHomeServer.csproj", "./"]
RUN dotnet restore "SmartHomeServer.csproj"

# 複製其餘程式碼並發布
COPY . .
# -c Release: 使用 Release 模式
# -r linux-arm64: 明確指定目標為 TX2 的架構 (ARM64)
# --self-contained false: 使用共享框架以縮減體積
RUN dotnet publish "SmartHomeServer.csproj" -c Release -r linux-arm64 --self-contained false -o /app/publish

# 階段 2: 執行 (Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# 安裝 OpenCV 與 System.Drawing 所需的原生依賴
# libgdiplus: 支援 System.Drawing
# libgomp1: OpenCV 的多執行緒支援 (OpenMP)
# libopencv-dev: 包含 OpenCV 的基礎函式庫
RUN apt-get update && apt-get install -y \
    libgdiplus \
    libgomp1 \
    libopencv-dev \
    curl \
    && rm -rf /var/lib/apt/lists/*

# 從建置階段複製發布的檔案
COPY --from=build /app/publish .


ENV DOTNET_RUNNING_IN_CONTAINER=true
# 設定時區 
ENV TZ=Asia/Taipei

# 因為我們需要存取 /dev/blackbox, /dev/video0, /dev/gpiomem, /sys/class/pwm
# 且需要寫入權限下載模型檔案 (.prototxt)
USER root

ENTRYPOINT ["dotnet", "SmartHomeServer.dll"]