# ビルドステージ
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# プロジェクトファイルをコピーして依存関係を復元
COPY ["src/Stratify.S3.csproj", "./"]
RUN dotnet restore "Stratify.S3.csproj"

# ソースコードをコピーしてビルド
COPY src/ .
RUN dotnet build "Stratify.S3.csproj" -c Release -o /app/build

# パブリッシュステージ
FROM build AS publish
RUN dotnet publish "Stratify.S3.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 実行ステージ
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# ストレージディレクトリを作成
RUN mkdir -p /storage/primary /storage/secondary /storage/archive

# アプリケーションファイルをコピー
COPY --from=publish /app/publish .
# ポート設定
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# ヘルスチェック
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# エントリーポイント
ENTRYPOINT ["dotnet", "Stratify.S3.dll"]