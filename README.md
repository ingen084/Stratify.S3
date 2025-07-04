# Stratify.S3

レイヤードファイルシステム用S3互換プロキシ - 複数のストレージバックエンドを統合し、S3 APIでアクセス可能にするASP.NET Core Minimal APIアプリケーション

## 概要

Stratify.S3は、複数のファイルシステムストレージを単一のS3互換エンドポイントとして公開するプロキシサーバーです。優先度ベースのストレージ階層、自動フェイルオーバー、自動復旧機能を提供します。

### 主な機能

- **S3互換API**: 基本的なS3操作をサポート（ListBuckets, CreateBucket, DeleteBucket, ListObjects, GetObject, PutObject, DeleteObject）
- **S3マルチパートアップロード**: 大きなファイルの効率的なアップロードをサポート
- **レイヤードストレージ**: 複数のバックエンドストレージを優先度順で管理
- **自動フェイルオーバー**: プライマリストレージが利用不可の場合、自動的にセカンダリストレージにフォールバック
- **自動復旧**: より新しいファイルを優先度の高いストレージに自動的に復旧
- **ヘルスチェック**: 各バックエンドの可用性を定期的に監視
- **Range対応**: 部分的なファイル取得をサポート
- **認証機能**: APIキー認証とAWS Signature V4認証をサポート
- **きめ細かいアクセス制御**: 操作種別とバケット単位でのアクセス制御
- **検証付きファイル移動**: MD5ハッシュによるファイル内容の整合性検証とトランザクショナルな移動
- **自動データマイグレーション**: 低優先度ストレージから高優先度ストレージへの自動移動
- **統合ビュー**: 全バックエンドのファイルを集約した統一されたバケット・オブジェクト一覧

## システム要件

- .NET 9.0以降
- Linux, Windows, macOS対応

## プロジェクト構造

```
Stratify.S3/
├── src/                    # ソースコード
│   ├── Program.cs         # メインアプリケーション
│   ├── Models/            # データモデル
│   ├── Services/          # ビジネスロジック
│   ├── Helpers/           # ユーティリティ
│   └── appsettings.json   # 設定ファイル
├── docker-compose.yml      # Docker構成
├── Dockerfile             # コンテナイメージ定義
└── README.md              # このファイル
```

## インストール

```bash
# リポジトリのクローン
git clone https://github.com/yourusername/Stratify.S3.git
cd Stratify.S3

# ビルド
dotnet build

# 実行
dotnet run --project src/Stratify.S3.csproj

# または Docker を使用
./docker-scripts.sh build
./docker-scripts.sh up
```

## 認証設定

認証機能は設定ファイルで有効/無効を切り替えできます：

```json
{
  "Authentication": {
    "Enabled": true,
    "Mode": "Both",  // "None", "ApiKey", "AwsSignature", "Both"
    "ApiKeys": [
      {
        "Name": "admin",
        "Key": "your-api-key",
        "AllowedOperations": ["*"],  // "*", "read", "write", "delete"
        "AllowedBuckets": ["*"],     // "*" or specific bucket names
        "Enabled": true
      }
    ],
    "AwsCredentials": [
      {
        "AccessKeyId": "AKIAIOSFODNN7EXAMPLE",
        "SecretAccessKey": "your-secret-key",
        "Name": "aws-user",
        "AllowedOperations": ["*"],
        "AllowedBuckets": ["*"],
        "Enabled": true
      }
    ]
  }
}
```

### 認証方法

#### APIキー認証
```bash
curl -H "X-API-Key: your-api-key" http://localhost:5000/mybucket
```

#### AWS署名認証
AWS CLIやSDKが自動的に署名を生成します：
```bash
aws --endpoint-url http://localhost:5000 s3 ls
```

## 設定

`appsettings.json`でストレージバックエンドとアプリケーション設定を構成します：

```json
{
  "AppSettings": {
    "AutoRecoveryEnabled": true,      // 自動復旧の有効/無効
    "RecoveryTimeout": 300,           // 復旧タイムアウト（秒）
    "ChunkSize": 8192,                // ストリーミング時のチャンクサイズ
    "MaxFileSize": 10737418240,       // 最大ファイルサイズ（10GB）
    "RecoveryCheckInterval": 300,     // 復旧チェック間隔（秒）
    "RecoveryBatchSize": 10          // 一度に処理する復旧ファイル数
  },
  "MultipartUpload": {
    "TempDirectory": "/tmp/stratify-s3-multipart",  // 一時ファイル保存先
    "CleanupInterval": 3600,          // クリーンアップ間隔（秒）
    "ExpirationHours": 24             // アップロードの有効期限（時間）
  },
  "Backends": [
    {
      "Name": "primary",
      "Path": "/nfs/unstable/storage",
      "Priority": 1,                  // 優先度（小さいほど高優先）
      "CheckInterval": 30,            // ヘルスチェック間隔（秒）
      "Timeout": 5.0,                 // タイムアウト（秒）
      "MaxRetries": 2
    }
  ]
}
```

## 使用方法

### S3クライアントでの接続

```bash
# AWS CLIの例
aws --endpoint-url http://localhost:5000 s3 ls

# バケット作成
aws --endpoint-url http://localhost:5000 s3 mb s3://mybucket

# ファイルアップロード
aws --endpoint-url http://localhost:5000 s3 cp file.txt s3://mybucket/

# 大きなファイルのアップロード（自動的にマルチパートアップロードが使用される）
aws --endpoint-url http://localhost:5000 s3 cp large-file.zip s3://mybucket/

# ファイルダウンロード
aws --endpoint-url http://localhost:5000 s3 cp s3://mybucket/file.txt ./
```

### 管理エンドポイント

管理エンドポイントはAPIキー認証で保護されています（認証が有効な場合）。

```bash
# ヘルスチェック（認証不要）
curl http://localhost:5000/health

# 手動復旧トリガー（認証が必要）
curl -X POST -H "X-Admin-Key: admin-api-key-secure" http://localhost:5000/admin/recovery

# バックエンド無効化
curl -X POST -H "X-Admin-Key: admin-api-key-secure" http://localhost:5000/admin/backend/primary/disable

# バックエンド有効化
curl -X POST -H "X-Admin-Key: admin-api-key-secure" http://localhost:5000/admin/backend/primary/enable

# バックエンドステータス確認
curl -H "X-Admin-Key: admin-api-key-secure" http://localhost:5000/admin/backends
```

#### 管理API認証設定

```json
{
  "Authentication": {
    "Enabled": true,
    "ApiKeys": [
      {
        "Name": "admin-api",
        "Key": "your-secure-admin-key",
        "AllowedOperations": ["admin"],
        "AllowedBuckets": ["*"],
        "Enabled": true
      }
    ]
  }
}
```

## S3マルチパートアップロード

Stratify.S3は、S3互換のマルチパートアップロードをフルサポートしています。

### サポートされているAPI

- `POST /{bucket}/{key}?uploads` - マルチパートアップロード開始
- `PUT /{bucket}/{key}?partNumber=X&uploadId=Y` - パートアップロード
- `POST /{bucket}/{key}?uploadId=X` - マルチパートアップロード完了
- `DELETE /{bucket}/{key}?uploadId=X` - マルチパートアップロード中断
- `GET /{bucket}/{key}?uploadId=X` - アップロード済みパート一覧

### 特徴

- **既存フェイルオーバー機能との統合**: 完了時に既存の`WriteFileWithFallbackAsync`を使用してストレージ階層を活用
- **高性能**: パートごとの並列アップロード対応
- **整合性保証**: 各パートでMD5ハッシュによる整合性チェック
- **自動クリーンアップ**: 期限切れのマルチパートアップロードを自動削除
- **S3完全互換**: AWS SDK、aws-cli、サードパーティツールから利用可能

### 使用例

```bash
# AWS CLI - 大きなファイルは自動的にマルチパートアップロードが使用される
aws --endpoint-url http://localhost:5000 s3 cp large-file.zip s3://mybucket/

# Python boto3
import boto3
s3 = boto3.client('s3', endpoint_url='http://localhost:5000')
s3.upload_file('large-file.zip', 'mybucket', 'large-file.zip')
```

## アーキテクチャ

### ストレージ階層

1. **Primary Storage**: 最優先で使用される高速ストレージ
2. **Secondary Storage**: プライマリが利用不可の場合のフォールバック
3. **Archive Storage**: 長期保存用の低速ストレージ

### ファイル操作フロー

- **読み取り**: 優先度順にストレージを検索し、最初に見つかったファイルを返す
- **書き込み**: 正常時はプライマリストレージのみに書き込み、プライマリ障害時はセカンダリにフォールバック
- **削除**: プライマリストレージが使用可能な場合はプライマリでの削除が成功する必要があり、全ストレージからファイルを削除
- **移動**: 低優先度から高優先度への自動移動（MD5ハッシュ検証付き）
- **一覧表示**: 全バックエンドからファイルを集約し、重複を排除して統一ビューを提供

### 統合ビュー機能

- **バケット一覧**: 全バックエンドからバケットを収集し、重複を排除
- **オブジェクト一覧**: 優先度の低いバックエンドから順に処理し、同名ファイルは高優先度で上書き
- **透明な集約**: クライアントからは単一のストレージとして見える
- **優先度に基づく選択**: 同じファイルが複数のバックエンドに存在する場合、最高優先度のものを表示

### 自動復旧メカニズム

- 定期的に全ストレージをスキャン
- より新しいファイルや欠落しているファイルを検出
- 優先度の高いストレージに自動的に移動（元ファイルは削除）
- MD5ハッシュによるファイル内容の整合性検証で安全な転送を保証
- トランザクショナルな操作でデータ損失を防止

## テスト

### 結合テスト

AWS CLIを使用した包括的な結合テストが利用可能です：

```bash
# テストセットアップ
cd tests/integration

# 全テストの実行
./run-all-tests.sh

# 個別テストの実行
./test-basic-operations.sh      # 基本操作
./test-multipart-upload.sh      # マルチパートアップロード
./test-failover.sh              # フェイルオーバー
```

テストカバレッジ：
- **基本操作**: バケット・オブジェクトの CRUD 操作
- **マルチパートアップロード**: 大きなファイルの分割アップロード
- **フェイルオーバー**: バックエンド障害時の動作
- **管理API**: ヘルスチェック、手動復旧、バックエンド制御

詳細は [tests/integration/README.md](tests/integration/README.md) を参照してください。

## パフォーマンスチューニング

- `ChunkSize`: ネットワーク帯域幅に応じて調整
- `RecoveryBatchSize`: ストレージI/O能力に応じて調整
- `CheckInterval`: ストレージの安定性に応じて調整

## トラブルシューティング

### ストレージが利用不可と表示される
- ストレージパスへのアクセス権限を確認
- ネットワークストレージの場合、マウント状態を確認

### 復旧が遅い
- `RecoveryTimeout`と`RecoveryBatchSize`を調整
- ネットワーク帯域幅とストレージI/O性能を確認

### メモリ使用量が高い
- 大きなファイルを扱う場合は`ChunkSize`を小さくする

## ライセンス

MIT License

## 貢献

プルリクエストを歓迎します。大きな変更の場合は、まずissueを作成して変更内容について議論してください。
