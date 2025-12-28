# --- 第一階段：建置環境 (SDK) ---
# FIX: 使用 .NET 10 SDK (配合您的專案設定)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1. 先複製專案檔並還原相依套件 (利用 Docker Cache 加速)
COPY ["SmartHomeServer.csproj", "./"]
# 明確指定還原 linux-arm64 架構的套件
RUN dotnet restore -r linux-arm64 "SmartHomeServer.csproj"

# 2. 複製剩餘程式碼並編譯
COPY . .
# FIX: 修正專案檔名為 SmartHomeServer.csproj (解決 MSB1009 錯誤)
RUN dotnet publish "SmartHomeServer.csproj" -c Release -o /app/publish -r linux-arm64 --no-self-contained

# --- 第二階段：執行環境 (Runtime) ---
# 使用 ASP.NET Core Runtime 10.0 (支援 ARM64)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 3. 安裝硬體與視覺所需的原生函式庫
# libopencv-dev: OpenCV 依賴
# libgdiplus: 處理圖片時常需要的 GDI+ 相容層 (System.Drawing)
# libgpiod-dev: GPIO 控制可能需要
# tzdata: 設定時區需要此套件
RUN apt-get update && apt-get install -y \
    libopencv-dev \
    libgdiplus \
    libc6-dev \
    v4l-utils \
    tzdata \
    util-linux \   
    && rm -rf /var/lib/apt/lists/*

# 4. 從建置階段複製編譯好的檔案
COPY --from=build /app/publish .

# 5. 設定環境變數
ENV ASPNETCORE_URLS=http://+:8080
# 設定時區為台北 (方便 Log 檢視)
ENV TZ=Asia/Taipei

# 6. 啟動伺服器
ENTRYPOINT ["dotnet", "SmartHomeServer.dll"]