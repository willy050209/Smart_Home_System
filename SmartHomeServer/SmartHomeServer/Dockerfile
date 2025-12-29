# --- 第一階段：建置環境 (使用你電腦原本的架構，例如 x64，避免 QEMU 崩潰) ---
# 注意：這裡加了 --platform=$BUILDPLATFORM
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# 1. 複製專案檔
COPY ["SmartHomeServer.csproj", "./"]

# 2. 還原相依套件
# 關鍵：加上 -r linux-arm64 明確告訴 SDK 我們要還原 ARM64 的套件
# 關鍵：加上 --disable-parallel 減少多執行緒衝突風險
RUN dotnet restore -r linux-arm64 "SmartHomeServer.csproj" --disable-parallel

# 3. 複製剩餘程式碼並編譯
COPY . .
# 關鍵：發布時指定 -r linux-arm64
RUN dotnet publish "SmartHomeServer.csproj" -c Release -o /app/publish -r linux-arm64 --no-self-contained

# --- 第二階段：執行環境 (這層會自動使用目標架構 ARM64) ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 4. 安裝硬體與視覺所需的原生函式庫 (TX2 需要這些)
RUN apt-get update && apt-get install -y \
    libopencv-dev \
    libgdiplus \
    libc6-dev \
    v4l-utils \
    tzdata \
    util-linux \
    && rm -rf /var/lib/apt/lists/*

# 5. 從建置階段複製編譯好的檔案
COPY --from=build /app/publish .

# 6. 設定環境變數
ENV ASPNETCORE_URLS=http://+:8080
ENV TZ=Asia/Taipei

# 7. 啟動伺服器
ENTRYPOINT ["dotnet", "SmartHomeServer.dll"]