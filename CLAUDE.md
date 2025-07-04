# Stratify.S3 開発ガイド

このドキュメントは、Claude AIアシスタントがStratify.S3プロジェクトを効率的に理解し、開発をサポートするための情報を提供します。
作業の際は、思いついた方法が正しいかどうか、結論が出てもその結論が完全に確信を持てるまで繰り返し再検討して進めてください。

## プロジェクト概要

Stratify.S3は、複数のファイルシステムストレージをS3互換APIで統合するレイヤードストレージプロキシです。ASP.NET Core Minimal APIで実装されており、高可用性とフェイルオーバー機能を提供します。

## アーキテクチャ

### ストレージ階層
- **プライマリストレージ**: 最高優先度（Priority=1）、正常時にすべてのファイルが保存される
- **セカンダリストレージ**: 低優先度（Priority=2,3...）、プライマリ障害時のフェイルオーバー先
- **動作原理**: 
  - 正常時: プライマリのみに書き込み
  - プライマリ障害時: セカンダリに書き込み
  - プライマリ復旧時: セカンダリからプライマリにファイルを移動

## プロジェクト構造

```
Stratify.S3/
├── Program.cs              # メインアプリケーション、APIエンドポイント定義
├── Models/
│   └── BackendConfiguration.cs  # 設定モデル定義
├── Services/
│   └── BackendManager.cs   # ストレージバックエンド管理ロジック
├── Helpers/
│   └── S3XmlHelper.cs      # S3互換XMLレスポンス生成
├── appsettings.json        # アプリケーション設定
└── Stratify.S3.csproj      # プロジェクトファイル
```

## 主要コンポーネント

### BackendManager
- ストレージバックエンドの健全性チェック
- ファイルの検索、フェイルオーバー、復旧機能
- 優先度ベースのストレージ選択
- プライマリ復旧時のファイル移動処理

### S3XmlHelper
- S3互換のXMLレスポンス生成
- エラーレスポンスのフォーマット

### RecoveryHostedService
- バックグラウンドでの自動復旧処理
- セカンダリからプライマリへのファイル移動
- 冗長ファイルの自動削除

## 開発時の注意事項

### コーディング規約
- C# 12の最新機能を活用（プライマリコンストラクタ、パターンマッチングなど）
- 非同期処理を優先（async/await）
- 適切なログ出力（ILogger使用）

### パフォーマンス考慮事項
- 大きなファイルはストリーミング処理
- 不要なメモリアロケーションを避ける
- バックグラウンドタスクは適切にスロットリング

### エラーハンドリング
- S3互換のエラーコードを返す
- 部分的な失敗でもサービスを継続
- 詳細なログでデバッグを支援

## テストとデバッグ

### ローカル実行
```bash
dotnet run
```

### テスト用コマンド
```bash
# ビルドとテスト
dotnet build
dotnet test

# コードフォーマット
dotnet format

# 静的解析
dotnet analyzers
```

### デバッグ時の確認ポイント
1. ストレージパスのアクセス権限
2. ヘルスチェックログ
3. 復旧タスクの実行状況

## 拡張ポイント

### 新しいS3操作の追加
1. `Program.cs`に新しいエンドポイントを追加
2. `S3XmlHelper.cs`に必要なXMLフォーマッタを追加
3. 適切なエラーハンドリングを実装

### ストレージバックエンドの拡張
1. `BackendConfiguration.cs`に新しい設定項目を追加
2. `BackendManager.cs`にバックエンド固有のロジックを実装

### パフォーマンス最適化
- 並列処理の活用（Parallel.ForEachAsync）
- キャッシング戦略の実装
- 接続プーリングの最適化

## よくある問題と解決方法

### "No available backends"エラー
- すべてのバックエンドパスが存在し、書き込み可能か確認
- ヘルスチェックファイルの作成権限を確認

### メモリ使用量が高い
- `ChunkSize`を小さくする
- 大きなファイルのアップロード時はストリーミング処理を確認

### 復旧が完了しない
- `RecoveryTimeout`の値を増やす
- ネットワーク帯域幅とストレージI/O性能を確認
- プライマリストレージのアクセス権限を確認

### プライマリストレージでの削除が失敗する
- プライマリストレージが使用可能な場合、削除操作はプライマリで成功する必要がある
- プライマリストレージのアクセス権限とディスク容量を確認

## S3マルチパートアップロード（実装済み）

Stratify.S3は、S3互換のマルチパートアップロードAPIに対応しています。

### 対応API
- `POST /{bucket}/{key}?uploads` - マルチパートアップロード開始
- `PUT /{bucket}/{key}?partNumber=X&uploadId=Y` - パートアップロード
- `POST /{bucket}/{key}?uploadId=X` - マルチパートアップロード完了
- `DELETE /{bucket}/{key}?uploadId=X` - マルチパートアップロード中断
- `GET /{bucket}/{key}?uploadId=X` - アップロード済みパート一覧

### 設定項目
```json
{
  "MultipartUpload": {
    "TempDirectory": "/tmp/stratify-s3-multipart",
    "CleanupInterval": 3600,
    "ExpirationHours": 24
  }
}
```

### 特徴
- **既存フェイルオーバー機能との統合**: 完了時に`WriteFileWithFallbackAsync`を使用
- **ETag統一**: メタデータベース方式でパフォーマンス重視
- **パート検証**: 各パートでMD5ハッシュによる整合性チェック
- **自動クリーンアップ**: 期限切れのマルチパートアップロードを自動削除
- **S3互換**: AWS SDKやaws-cliから利用可能

### 使用例
```bash
# AWS CLI例
aws s3 cp large-file.zip s3://mybucket/path/to/file.zip \
    --endpoint-url http://localhost:5000

# 大きなファイルは自動的にマルチパートアップロードが使用される
```

## 今後の改善案

1. **メトリクス収集**: Prometheus/OpenTelemetry統合
2. **認証・認可**: S3署名検証の実装
3. **圧縮転送**: gzip/brotli対応
4. **イベント通知**: ファイル変更時のWebhook/メッセージング
5. **マルチパート最適化**: チャンクサイズの動的調整

## テストとデバッグ

### 結合テスト

AWS CLIを使用した包括的な結合テストが利用可能です：

```bash
# プロジェクトルートから
cd tests/integration

# サーバー起動（別ターミナル）
dotnet run --project ../../src/Stratify.S3.csproj

# 全テスト実行
./run-all-tests.sh

# 個別テスト実行
./test-basic-operations.sh      # 基本S3操作
./test-multipart-upload.sh      # マルチパートアップロード
./test-failover.sh              # フェイルオーバー・管理API
```

### テストカバレッジ

- **基本操作**: ListBuckets, CreateBucket, DeleteBucket, PutObject, GetObject, DeleteObject
- **マルチパートアップロード**: 全API（Initiate, UploadPart, Complete, Abort, ListParts）
- **フェイルオーバー**: バックエンド障害シミュレーション
- **管理機能**: ヘルスチェック、手動復旧、バックエンド制御
- **整合性**: ファイル内容のMD5検証
- **パフォーマンス**: アップロード・ダウンロード時間の計測

### デバッグガイド

```bash
# 詳細ログでサーバー起動
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/

# テスト一時ファイルの確認
ls -la tests/integration/temp/
ls -la tests/integration/test-data/

# 特定テストのデバッグ実行
set -x
./test-multipart-upload.sh
set +x
```

## リリース手順

```bash
# テスト実行
cd tests/integration && ./run-all-tests.sh

# リリースビルド
dotnet publish -c Release -o ./publish

# Dockerイメージ作成（Dockerfileが必要）
docker build -t stratify-s3:latest .

# 実行
dotnet ./publish/Stratify.S3.dll
```