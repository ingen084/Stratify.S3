# Stratify.S3 Integration Tests

このディレクトリには、Stratify.S3の結合テストが含まれています。AWS CLIを使用して実際のS3 APIエンドポイントをテストし、マルチパートアップロード、フェイルオーバー、基本操作の動作を検証します。

## 前提条件

### 必要なツール

1. **AWS CLI**: S3 APIテスト用
   ```bash
   # Ubuntu/Debian
   sudo apt-get install awscli
   
   # macOS
   brew install awscli
   
   # または pip
   pip install awscli
   ```

2. **Bash**: テストスクリプト実行用（Linux/macOS/WSL）

3. **curl**: ヘルスチェックと管理API用

### Stratify.S3サーバー

テスト実行前にStratify.S3サーバーが起動している必要があります：

```bash
# プロジェクトルートから
dotnet run --project src/Stratify.S3.csproj
```

デフォルトでは `http://localhost:5000` で起動します。

## テストの実行

### 全テストの実行

```bash
# すべてのテストを実行
./run-all-tests.sh

# カスタムエンドポイントでテスト
./run-all-tests.sh --endpoint http://localhost:8080

# カスタムバケット名でテスト
./run-all-tests.sh --bucket my-test-bucket
```

### 個別テストの実行

```bash
# セットアップのみ
./setup.sh

# 基本操作テスト
./test-basic-operations.sh

# マルチパートアップロードテスト
./test-multipart-upload.sh

# フェイルオーバーテスト
./test-failover.sh
```

## テストスイート

### 1. setup.sh
テスト用のデータファイルを生成します：
- `small-file.txt` (1KB) - 基本操作用
- `medium-file.bin` (1MB) - 中サイズファイル用
- `large-file.bin` (10MB) - マルチパートアップロード用
- `very-large-file.bin` (50MB) - 大容量マルチパートアップロード用
- `test-content.txt` - 内容検証用

### 2. test-basic-operations.sh
基本的なS3操作をテストします：
- バケットの作成・削除
- オブジェクトのアップロード・ダウンロード・削除
- オブジェクト一覧の取得
- パス付きオブジェクトの操作
- ファイル内容の整合性検証

### 3. test-multipart-upload.sh
マルチパートアップロード機能をテストします：
- 大きなファイルの自動マルチパートアップロード
- 直接的なマルチパート API の使用
  - InitiateMultipartUpload
  - UploadPart
  - ListParts
  - CompleteMultipartUpload
  - AbortMultipartUpload
- ファイル整合性検証
- パフォーマンス測定

### 4. test-failover.sh
フェイルオーバーと管理機能をテストします：
- バックエンドステータスの確認
- プライマリバックエンドの無効化/有効化
- フェイルオーバー時の読み書き動作
- 手動復旧のトリガー
- ヘルスチェックエンドポイント

## 設定

### 環境変数

テストは以下の環境変数で設定できます：

```bash
export S3_ENDPOINT_URL="http://localhost:5000"
export TEST_BUCKET="integration-test-bucket"
export AWS_ACCESS_KEY_ID="test-access-key"
export AWS_SECRET_ACCESS_KEY="test-secret-key"
```

### 認証

テストは認証が無効化されている環境で動作するように設計されています。認証が有効な場合は、適切なAPIキーまたはAWS認証情報を設定してください。

管理APIテスト用：
```bash
# appsettings.jsonで設定されたキーを使用
ADMIN_API_KEY="admin-api-key-secure"
```

## トラブルシューティング

### サーバーが起動していない
```
[ERROR] Stratify.S3 server is not running at http://localhost:5000
```
→ Stratify.S3サーバーを起動してください

### AWS CLIが見つからない
```
[ERROR] AWS CLI is not installed. Please install it first.
```
→ AWS CLIをインストールしてください

### 認証エラー
```
[ERROR] Access Denied
```
→ 認証設定を確認するか、認証を無効化してください

### ストレージバックエンドエラー
```
[ERROR] ServiceUnavailable
```
→ ストレージパスが存在し、書き込み権限があることを確認してください

## ログとデバッグ

### 詳細ログの有効化

```bash
# デバッグ情報を含めてテスト実行
set -x
./run-all-tests.sh
set +x
```

### 一時ファイルの確認

テスト実行中に生成される一時ファイルは `tests/integration/temp/` に保存されます：

```bash
ls -la tests/integration/temp/
ls -la tests/integration/test-data/
```

### サーバーログの確認

Stratify.S3サーバーのログでテスト実行時の動作を確認できます：

```bash
# サーバー起動時にログレベルを設定
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Stratify.S3.csproj
```

## パフォーマンステスト

大きなファイルのアップロード・ダウンロード時間が計測され、ログに出力されます：

```
[SUCCESS] Large file uploaded in 2s
[SUCCESS] Large file downloaded in 1s
```

パフォーマンスが期待値と大きく異なる場合は、ネットワーク設定やストレージ性能を確認してください。

## CI/CD統合

GitHub ActionsやJenkinsなどのCI/CD環境では：

```yaml
# GitHub Actions例
- name: Run Integration Tests
  run: |
    # サーバー起動
    dotnet run --project src/Stratify.S3.csproj &
    SERVER_PID=$!
    
    # テスト実行
    sleep 5  # サーバー起動待ち
    cd tests/integration
    ./run-all-tests.sh
    
    # クリーンアップ
    kill $SERVER_PID
```

## 貢献

新しいテストケースを追加する場合：

1. 適切なテストスイートファイルに追加
2. `config.sh` で共通設定を利用
3. 適切なログ出力とエラーハンドリングを実装
4. `run-all-tests.sh` の TEST_SUITES 配列に追加（必要に応じて）