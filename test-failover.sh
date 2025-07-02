#!/bin/bash

# Stratify.S3 フェイルオーバーテストスクリプト

# 色付き出力
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

BASE_URL="http://localhost:5000"

echo -e "${BLUE}=== Stratify.S3 フェイルオーバーテスト ===${NC}"
echo ""

# ヘルプ表示
show_help() {
    echo "使用方法: $0 [command]"
    echo ""
    echo "コマンド:"
    echo "  status       - 全バックエンドの状態を表示"
    echo "  disable NAME - 指定されたバックエンドを無効化"
    echo "  enable NAME  - 指定されたバックエンドを有効化"
    echo "  health       - ヘルスチェック結果を表示"
    echo "  test-scenario - 自動テストシナリオを実行"
    echo "  test-delete   - 削除操作のテストシナリオを実行"
    echo "  test-upload   - アップロード/ダウンロードテストを実行"
    echo "  reset        - 全バックエンドを有効化"
    echo ""
    echo "例："
    echo "  $0 status"
    echo "  $0 disable primary"
    echo "  $0 enable primary"
    echo "  $0 test-scenario"
    echo "  $0 test-delete"
    echo "  $0 test-upload"
}

# バックエンドステータス表示
show_status() {
    echo -e "${YELLOW}バックエンドステータス:${NC}"
    curl -s "$BASE_URL/admin/backends" | jq -r '.[] | "\(.name): \(.status) (Priority: \(.priority))"' 2>/dev/null || echo "エラー: Stratify.S3が起動していないか、jqがインストールされていません"
    echo ""
}

# バックエンド無効化
disable_backend() {
    local backend_name=$1
    if [ -z "$backend_name" ]; then
        echo -e "${RED}エラー: バックエンド名を指定してください${NC}"
        return 1
    fi
    
    echo -e "${YELLOW}バックエンド '$backend_name' を無効化中...${NC}"
    response=$(curl -s -X POST "$BASE_URL/admin/backend/$backend_name/disable")
    success=$(echo "$response" | jq -r '.success' 2>/dev/null)
    message=$(echo "$response" | jq -r '.message' 2>/dev/null)
    
    if [ "$success" = "true" ]; then
        echo -e "${GREEN}✓ $message${NC}"
    else
        echo -e "${RED}✗ $message${NC}"
    fi
    echo ""
}

# バックエンド有効化
enable_backend() {
    local backend_name=$1
    if [ -z "$backend_name" ]; then
        echo -e "${RED}エラー: バックエンド名を指定してください${NC}"
        return 1
    fi
    
    echo -e "${YELLOW}バックエンド '$backend_name' を有効化中...${NC}"
    response=$(curl -s -X POST "$BASE_URL/admin/backend/$backend_name/enable")
    success=$(echo "$response" | jq -r '.success' 2>/dev/null)
    message=$(echo "$response" | jq -r '.message' 2>/dev/null)
    
    if [ "$success" = "true" ]; then
        echo -e "${GREEN}✓ $message${NC}"
    else
        echo -e "${RED}✗ $message${NC}"
    fi
    echo ""
}

# ヘルスチェック表示
show_health() {
    echo -e "${YELLOW}ヘルスチェック結果:${NC}"
    curl -s "$BASE_URL/health" | jq . 2>/dev/null || echo "エラー: Stratify.S3が起動していないか、jqがインストールされていません"
    echo ""
}

# 全バックエンド有効化
reset_backends() {
    echo -e "${YELLOW}全バックエンドを有効化中...${NC}"
    for backend in primary secondary archive; do
        enable_backend "$backend"
    done
}

# テストファイルのアップロード
upload_test_file() {
    local bucket="$1"
    local key="$2"
    local content="$3"
    
    echo -e "${YELLOW}ファイルアップロード: $bucket/$key${NC}"
    response=$(curl -s -X PUT "$BASE_URL/$bucket/$key" \
        -H "Content-Type: text/plain" \
        -d "$content")
    
    if [ $? -eq 0 ]; then
        echo -e "${GREEN}✓ アップロード成功${NC}"
        return 0
    else
        echo -e "${RED}✗ アップロード失敗${NC}"
        return 1
    fi
}

# テストファイルのダウンロード
download_test_file() {
    local bucket="$1"
    local key="$2"
    
    echo -e "${YELLOW}ファイルダウンロード: $bucket/$key${NC}"
    response=$(curl -s "$BASE_URL/$bucket/$key")
    
    if [ $? -eq 0 ] && [ -n "$response" ]; then
        echo -e "${GREEN}✓ ダウンロード成功: $response${NC}"
        return 0
    else
        echo -e "${RED}✗ ダウンロード失敗${NC}"
        return 1
    fi
}

# テストファイルの削除
delete_test_file() {
    local bucket="$1"
    local key="$2"
    local expect_success="$3"  # true/false
    
    echo -e "${YELLOW}ファイル削除: $bucket/$key${NC}"
    response=$(curl -s -X DELETE "$BASE_URL/$bucket/$key" -w "%{http_code}")
    http_code=$(echo "$response" | tail -c 4)
    
    if [ "$expect_success" = "true" ]; then
        if [ "$http_code" = "204" ] || [ "$http_code" = "200" ]; then
            echo -e "${GREEN}✓ 削除成功 (HTTP $http_code)${NC}"
            return 0
        else
            echo -e "${RED}✗ 削除失敗 (HTTP $http_code)${NC}"
            return 1
        fi
    else
        if [ "$http_code" != "204" ] && [ "$http_code" != "200" ]; then
            echo -e "${GREEN}✓ 期待通り削除失敗 (HTTP $http_code)${NC}"
            return 0
        else
            echo -e "${RED}✗ 削除が予期せず成功 (HTTP $http_code)${NC}"
            return 1
        fi
    fi
}

# バックエンドの物理的な無効化（権限変更）
disable_backend_physical() {
    local backend_name="$1"
    local backend_path="$2"
    
    if [ -n "$backend_path" ] && [ -d "$backend_path" ]; then
        echo -e "${YELLOW}物理的に無効化: $backend_name ($backend_path)${NC}"
        chmod 000 "$backend_path" 2>/dev/null || echo -e "${RED}権限変更失敗 (sudo権限が必要な可能性)${NC}"
    fi
}

# バックエンドの物理的な有効化（権限復元）
enable_backend_physical() {
    local backend_name="$1"
    local backend_path="$2"
    
    if [ -n "$backend_path" ] && [ -d "$backend_path" ]; then
        echo -e "${YELLOW}物理的に有効化: $backend_name ($backend_path)${NC}"
        chmod 755 "$backend_path" 2>/dev/null || echo -e "${RED}権限変更失敗 (sudo権限が必要な可能性)${NC}"
    fi
}

# 削除操作テストシナリオ
test_delete_scenario() {
    echo -e "${BLUE}=== 削除操作テストシナリオ開始 ===${NC}"
    echo ""
    
    local test_bucket="test-bucket"
    local test_key="delete-test.txt"
    local test_content="This is a delete test file $(date)"
    
    # 初期状態確認
    echo -e "${YELLOW}1. 初期状態確認${NC}"
    show_status
    
    # テストファイルをアップロード
    echo -e "${YELLOW}2. テストファイルのアップロード${NC}"
    upload_test_file "$test_bucket" "$test_key" "$test_content"
    sleep 2
    
    # 正常な削除テスト
    echo -e "${YELLOW}3. 正常状態での削除テスト${NC}"
    delete_test_file "$test_bucket" "$test_key" "true"
    echo ""
    
    # 再度アップロード
    echo -e "${YELLOW}4. 削除テスト用にファイル再アップロード${NC}"
    upload_test_file "$test_bucket" "$test_key" "$test_content"
    sleep 2
    
    # プライマリストレージを無効化して削除テスト
    echo -e "${YELLOW}5. プライマリストレージ無効化時の削除テスト${NC}"
    disable_backend "primary"
    sleep 1
    delete_test_file "$test_bucket" "$test_key" "true"  # セカンダリで削除は成功するはず
    echo ""
    
    # 再度アップロード（セカンダリに保存される）
    echo -e "${YELLOW}6. セカンダリストレージにファイル再アップロード${NC}"
    upload_test_file "$test_bucket" "$test_key" "$test_content"
    sleep 2
    
    # プライマリを有効化、セカンダリを無効化
    echo -e "${YELLOW}7. プライマリ有効化、セカンダリ無効化${NC}"
    enable_backend "primary"
    disable_backend "secondary"
    sleep 1
    
    # プライマリにファイルがない状態で削除テスト
    echo -e "${YELLOW}8. プライマリにファイルがない状態での削除テスト${NC}"
    delete_test_file "$test_bucket" "$test_key" "true"  # ファイルが存在しない削除は成功するはず
    echo ""
    
    # プライマリにファイルをアップロードして、物理的にプライマリを無効化
    echo -e "${YELLOW}9. プライマリアクセス不可時の削除テスト準備${NC}"
    upload_test_file "$test_bucket" "$test_key" "$test_content"
    sleep 2
    
    # 注意: 実際の物理的無効化は危険なので、コメントアウトしておく
    echo -e "${YELLOW}10. 物理的アクセス不可時の削除テスト（スキップ）${NC}"
    echo -e "${BLUE}※ 物理的な無効化テストは手動で実行してください${NC}"
    # disable_backend_physical "primary" "/path/to/primary"
    # delete_test_file "$test_bucket" "$test_key" "false"  # プライマリアクセス不可なら削除失敗するはず
    
    # 復旧
    echo -e "${YELLOW}11. 全バックエンド復旧${NC}"
    reset_backends
    
    # 最終確認
    echo -e "${YELLOW}12. 最終状態確認${NC}"
    show_status
    
    echo -e "${GREEN}=== 削除操作テストシナリオ完了 ===${NC}"
}

# アップロード/ダウンロードテストシナリオ
test_upload_scenario() {
    echo -e "${BLUE}=== アップロード/ダウンロードテストシナリオ開始 ===${NC}"
    echo ""
    
    local test_bucket="test-bucket"
    local test_content="Test content $(date)"
    
    # 初期状態確認
    echo -e "${YELLOW}1. 初期状態確認${NC}"
    show_status
    
    # 全バックエンド有効時のテスト
    echo -e "${YELLOW}2. 全バックエンド有効時のアップロード/ダウンロードテスト${NC}"
    for i in {1..3}; do
        local key="upload-test-$i.txt"
        upload_test_file "$test_bucket" "$key" "$test_content $i"
        download_test_file "$test_bucket" "$key"
        echo ""
    done
    
    # プライマリ無効時のテスト
    echo -e "${YELLOW}3. プライマリ無効時のアップロード/ダウンロードテスト${NC}"
    disable_backend "primary"
    for i in {4..6}; do
        local key="upload-test-$i.txt"
        upload_test_file "$test_bucket" "$key" "$test_content $i"
        download_test_file "$test_bucket" "$key"
        echo ""
    done
    
    # プライマリ・セカンダリ無効時のテスト
    echo -e "${YELLOW}4. プライマリ・セカンダリ無効時のアップロード/ダウンロードテスト${NC}"
    disable_backend "secondary"
    for i in {7..9}; do
        local key="upload-test-$i.txt"
        upload_test_file "$test_bucket" "$key" "$test_content $i"
        download_test_file "$test_bucket" "$key"
        echo ""
    done
    
    # 復旧テスト
    echo -e "${YELLOW}5. 段階的復旧テスト${NC}"
    enable_backend "secondary"
    upload_test_file "$test_bucket" "recovery-test-1.txt" "Recovery test 1"
    download_test_file "$test_bucket" "recovery-test-1.txt"
    
    enable_backend "primary"
    upload_test_file "$test_bucket" "recovery-test-2.txt" "Recovery test 2"
    download_test_file "$test_bucket" "recovery-test-2.txt"
    
    # バケット一覧テスト
    echo -e "${YELLOW}6. バケット一覧テスト${NC}"
    echo -e "${YELLOW}バケット一覧:${NC}"
    curl -s "$BASE_URL/" | head -10
    echo ""
    
    # オブジェクト一覧テスト
    echo -e "${YELLOW}7. オブジェクト一覧テスト${NC}"
    echo -e "${YELLOW}$test_bucket内のオブジェクト:${NC}"
    curl -s "$BASE_URL/$test_bucket" | head -10
    echo ""
    
    echo -e "${GREEN}=== アップロード/ダウンロードテストシナリオ完了 ===${NC}"
}

# テストシナリオ実行
test_scenario() {
    echo -e "${BLUE}=== 自動テストシナリオ開始 ===${NC}"
    echo ""
    
    # 初期状態確認
    echo -e "${YELLOW}1. 初期状態の確認${NC}"
    show_status
    show_health
    
    # プライマリを無効化
    echo -e "${YELLOW}2. プライマリバックエンドを無効化${NC}"
    disable_backend "primary"
    show_status
    
    echo -e "${YELLOW}S3リクエストテスト (プライマリ無効):${NC}"
    curl -s "$BASE_URL/" | head -5
    echo ""
    
    # セカンダリも無効化
    echo -e "${YELLOW}3. セカンダリバックエンドも無効化${NC}"
    disable_backend "secondary"
    show_status
    
    echo -e "${YELLOW}S3リクエストテスト (プライマリ・セカンダリ無効):${NC}"
    curl -s "$BASE_URL/" | head -5
    echo ""
    
    # プライマリを復旧
    echo -e "${YELLOW}4. プライマリバックエンドを復旧${NC}"
    enable_backend "primary"
    show_status
    
    echo -e "${YELLOW}S3リクエストテスト (プライマリ復旧):${NC}"
    curl -s "$BASE_URL/" | head -5
    echo ""
    
    # 全復旧
    echo -e "${YELLOW}5. 全バックエンドを復旧${NC}"
    reset_backends
    show_status
    
    echo -e "${GREEN}=== テストシナリオ完了 ===${NC}"
}

# メイン処理
case "$1" in
    status)
        show_status
        ;;
    disable)
        disable_backend "$2"
        ;;
    enable)
        enable_backend "$2"
        ;;
    health)
        show_health
        ;;
    test-scenario)
        test_scenario
        ;;
    test-delete)
        test_delete_scenario
        ;;
    test-upload)
        test_upload_scenario
        ;;
    reset)
        reset_backends
        ;;
    *)
        show_help
        ;;
esac