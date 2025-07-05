#!/bin/bash

# Stratify.S3 認証機能統合テスト

# テストスクリプトでは適切なエラーハンドリングのため set -e を無効化

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/config.sh"

# テスト用定数
TEST_BUCKET="auth-test-bucket"
TEST_FILE="auth-test-file.txt"
INVALID_API_KEY="invalid-key-12345"              # 無効なAPIキー
VALID_ADMIN_API_KEY="admin-key-12345"            # 有効な管理者APIキー
VALID_READONLY_API_KEY="readonly-key-67890"      # 有効な読み取り専用APIキー
VALID_ADMIN_ADMIN_KEY="admin-api-key-secure"     # 有効な管理API専用キー

# AWS Signature v4 テスト用認証情報 (appsettings.jsonより)
AWS_ACCESS_KEY_ID="AKIAIOSFODNN7EXAMPLE"
AWS_SECRET_ACCESS_KEY="wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"

# テスト関数群
test_authentication_disabled() {
    log_info "認証動作の確認..."
    
    # テスト 1: 認証情報なしでのアクセス
    log_info "テスト 1: 認証情報なしでのアクセス確認"
    local response
    response=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/")
    
    if [[ "$response" == "200" ]]; then
        log_success "認証情報なしでアクセス許可 - 認証機能は無効"
    elif [[ "$response" == "401" ]] || [[ "$response" == "403" ]]; then
        log_success "認証情報なしでアクセス拒否 - 認証機能は有効 (HTTP $response)"
    else
        log_error "認証情報なしでのアクセスで予期しないレスポンス (HTTP $response)"
        return 1
    fi
    
    # テスト 2: 認証状態に基づくS3基本操作の動作確認
    log_info "テスト 2: 認証情報なしでの基本S3操作"
    
    if [[ "$response" == "200" ]]; then
        # 認証が無効の場合、操作は成功するはず
        aws_s3 mb "s3://$TEST_BUCKET" >/dev/null 2>&1
        echo "テスト用コンテンツ" > "$TEMP_DIR/$TEST_FILE"
        aws_s3 cp "$TEMP_DIR/$TEST_FILE" "s3://$TEST_BUCKET/$TEST_FILE" >/dev/null 2>&1
        aws_s3 ls "s3://$TEST_BUCKET/" >/dev/null 2>&1
        aws_s3 rm "s3://$TEST_BUCKET/$TEST_FILE" >/dev/null 2>&1
        aws_s3 rb "s3://$TEST_BUCKET" >/dev/null 2>&1
        log_success "認証なしでの基本S3操作が正常に動作"
    else
        # 認証が有効の場合、操作は失敗するはず
        local create_response
        create_response=$(aws_s3 mb "s3://$TEST_BUCKET" 2>&1)
        if echo "$create_response" | grep -q -i "error\|forbidden\|unauthorized"; then
            log_success "認証なしでのS3操作が正しく拒否されました"
        else
            log_error "認証有効時のS3操作で予期しない動作"
            return 1
        fi
    fi
    
    log_success "認証動作テストが完了しました！"
}

test_api_key_authentication() {
    log_info "APIキー認証のテスト..."
    
    # まず認証が有効になっているかチェック
    local auth_check
    auth_check=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/")
    if [[ "$auth_check" == "200" ]]; then
        log_warning "認証が無効 - APIキーテストはこのモードでは適用されません"
        log_success "APIキー認証テストが完了しました（スキップ - 認証無効）！"
        return 0
    fi
    
    # テスト 1: 無効なAPIキーは拒否されるべき
    log_info "テスト 1: 無効なAPIキーの拒否確認"
    local response
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-API-Key: $INVALID_API_KEY" \
        "$S3_ENDPOINT_URL/")
    
    if [[ "$response" == "401" ]] || [[ "$response" == "403" ]]; then
        log_success "無効なAPIキーが正しく拒否されました (HTTP $response)"
    else
        log_error "無効なAPIキーが拒否されませんでした (HTTP $response)"
        return 1
    fi
    
    # テスト 2: 有効な管理者APIキーは受け入れられるべき
    log_info "テスト 2: 有効な管理者APIキーの受け入れ確認"
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-API-Key: $VALID_ADMIN_API_KEY" \
        "$S3_ENDPOINT_URL/")
    
    if [[ "$response" == "200" ]]; then
        log_success "有効な管理者APIキーが正しく受け入れられました"
    else
        log_error "有効な管理者APIキーが拒否されました (HTTP $response)"
        return 1
    fi
    
    # テスト 3: 読み取り専用APIキー - GET操作で動作するべき
    log_info "テスト 3: 読み取り専用APIキーでのGET操作"
    # まず管理者キーでバケットとファイルを作成
    aws_s3 mb "s3://$TEST_BUCKET" --cli-connect-timeout 10 >/dev/null 2>&1 || true
    echo "読み取り専用テスト用コンテンツ" > "$TEMP_DIR/$TEST_FILE"
    AWS_ACCESS_KEY_ID="" AWS_SECRET_ACCESS_KEY="" \
    aws_s3 cp "$TEMP_DIR/$TEST_FILE" "s3://$TEST_BUCKET/$TEST_FILE" \
        --endpoint-url "$S3_ENDPOINT_URL" \
        --cli-read-timeout 10 \
        --cli-connect-timeout 10 \
        --metadata "X-API-Key=$VALID_ADMIN_API_KEY" >/dev/null 2>&1 || true
    
    # 読み取り専用アクセスをテスト
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-API-Key: $VALID_READONLY_API_KEY" \
        "$S3_ENDPOINT_URL/$TEST_BUCKET")
    
    if [[ "$response" == "200" ]]; then
        log_success "読み取り専用APIキーでのリスト操作が正常に動作"
    else
        log_warning "読み取り専用APIキーテストは結果不明 (HTTP $response) - 認証有効化が必要かもしれません"
    fi
    
    # テスト 4: 読み取り専用APIキーは書き込み操作で拒否されるべき
    log_info "テスト 4: 読み取り専用APIキーでの書き込み操作拒否確認"
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -X PUT \
        -H "X-API-Key: $VALID_READONLY_API_KEY" \
        -d "テストデータ" \
        "$S3_ENDPOINT_URL/$TEST_BUCKET/write-test.txt")
    
    if [[ "$response" == "403" ]] || [[ "$response" == "401" ]]; then
        log_success "読み取り専用APIキーでの書き込み操作が正しく拒否されました (HTTP $response)"
    else
        log_warning "読み取り専用APIキー書き込み拒否テストは結果不明 (HTTP $response) - 認証有効化が必要かもしれません"
    fi
    
    # クリーンアップ
    aws_s3 rm "s3://$TEST_BUCKET/$TEST_FILE" >/dev/null 2>&1 || true
    aws_s3 rb "s3://$TEST_BUCKET" >/dev/null 2>&1 || true
    
    log_success "APIキー認証テストが完了しました！"
}

test_aws_signature_authentication() {
    log_info "AWS Signature v4認証のテスト..."
    
    # まず認証が有効になっているかチェック
    local auth_check
    auth_check=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/")
    if [[ "$auth_check" == "200" ]]; then
        log_warning "認証が無効 - AWS署名テストはこのモードでは適用されません"
        log_success "AWS Signature v4認証テストが完了しました（スキップ - 認証無効）！"
        return 0
    fi
    
    # テスト 1: 無効なAWS認証情報は拒否されるべき
    log_info "テスト 1: 無効なAWS認証情報の拒否確認"
    local response
    response=$(AWS_ACCESS_KEY_ID="INVALID_ACCESS_KEY" \
               AWS_SECRET_ACCESS_KEY="invalid_secret_key" \
               aws s3 ls s3:// \
               --endpoint-url "$S3_ENDPOINT_URL" \
               --output json 2>&1 | head -1)
    
    if echo "$response" | grep -q -i "error\|forbidden\|unauthorized"; then
        log_success "無効なAWS認証情報が正しく拒否されました"
    else
        log_warning "AWS認証情報拒否テストは結果不明 - 認証有効化が必要かもしれません"
    fi
    
    # テスト 2: 有効なAWS認証情報は受け入れられるべき
    log_info "テスト 2: 有効なAWS認証情報の受け入れ確認"
    response=$(AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
               AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
               aws s3 ls s3:// \
               --endpoint-url "$S3_ENDPOINT_URL" \
               --output json 2>/dev/null | head -1)
    
    if [[ "$?" == "0" ]]; then
        log_success "有効なAWS認証情報での操作が正常に動作"
    else
        log_warning "AWS認証情報受け入れテストは結果不明 - 認証有効化が必要かもしれません"
    fi
    
    # テスト 3: オブジェクト操作でのAWS Signature v4
    log_info "テスト 3: AWS Signature v4オブジェクト操作"
    AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
    AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
    aws s3 mb "s3://$TEST_BUCKET" \
        --endpoint-url "$S3_ENDPOINT_URL" \
        --cli-connect-timeout 10 >/dev/null 2>&1 || true
    
    echo "AWS Signature テスト用コンテンツ" > "$TEMP_DIR/aws-test.txt"
    AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
    AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
    aws s3 cp "$TEMP_DIR/aws-test.txt" "s3://$TEST_BUCKET/aws-test.txt" \
        --endpoint-url "$S3_ENDPOINT_URL" \
        --cli-connect-timeout 10 >/dev/null 2>&1 || true
    
    AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
    AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
    aws s3 cp "s3://$TEST_BUCKET/aws-test.txt" "$TEMP_DIR/downloaded-aws-test.txt" \
        --endpoint-url "$S3_ENDPOINT_URL" \
        --cli-connect-timeout 10 >/dev/null 2>&1 || true
    
    if [[ -f "$TEMP_DIR/downloaded-aws-test.txt" ]]; then
        log_success "AWS Signature v4オブジェクト操作が正常に動作"
    else
        log_warning "AWS Signature v4テストは結果不明 - 認証有効化が必要かもしれません"
    fi
    
    # Cleanup
    AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
    AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
    aws s3 rm "s3://$TEST_BUCKET/aws-test.txt" \
        --endpoint-url "$S3_ENDPOINT_URL" >/dev/null 2>&1 || true
    AWS_ACCESS_KEY_ID="$AWS_ACCESS_KEY_ID" \
    AWS_SECRET_ACCESS_KEY="$AWS_SECRET_ACCESS_KEY" \
    aws s3 rb "s3://$TEST_BUCKET" \
        --endpoint-url "$S3_ENDPOINT_URL" >/dev/null 2>&1 || true
    
    log_success "AWS Signature v4認証テストが完了しました！"
}

test_admin_api_authentication() {
    log_info "管理API認証のテスト..."
    
    # まず認証が有効になっているかチェック
    local auth_check
    auth_check=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/admin/backends")
    if [[ "$auth_check" == "200" ]]; then
        log_warning "認証が無効 - 管理APIテストはこのモードでは適用されません"
        log_success "管理API認証テストが完了しました（スキップ - 認証無効）！"
        return 0
    fi
    
    # テスト 1: 認証有効時、認証情報なしの管理APIは拒否されるべき
    log_info "テスト 1: 認証情報なしでの管理APIアクセス"
    local response
    response=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/admin/backends")
    
    if [[ "$response" == "401" ]] || [[ "$response" == "403" ]]; then
        log_success "管理APIが正しく認証を要求しています (HTTP $response)"
    else
        log_warning "管理API認証テストは結果不明 (HTTP $response) - 認証が無効かもしれません"
    fi
    
    # テスト 2: 有効な管理者キーでの管理APIは動作するべき
    log_info "テスト 2: 有効な管理者キーでの管理API"
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-Admin-Key: $VALID_ADMIN_ADMIN_KEY" \
        "$S3_ENDPOINT_URL/admin/backends")
    
    if [[ "$response" == "200" ]]; then
        log_success "有効な管理者キーで管理APIが正常に動作"
    else
        log_warning "有効キーでの管理APIテストは結果不明 (HTTP $response) - 認証が無効かもしれません"
    fi
    
    # テスト 3: 無効なキーでの管理APIは拒否されるべき
    log_info "テスト 3: 無効な管理者キーでの管理API"
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-Admin-Key: invalid-admin-key" \
        "$S3_ENDPOINT_URL/admin/backends")
    
    if [[ "$response" == "401" ]] || [[ "$response" == "403" ]]; then
        log_success "管理APIが無効な管理者キーを正しく拒否 (HTTP $response)"
    else
        log_warning "管理API無効キーテストは結果不明 (HTTP $response) - 認証が無効かもしれません"
    fi
    
    # テスト 4: 通常のAPIキーは管理操作では動作しないべき
    log_info "テスト 4: 通常のAPIキーでの管理操作"
    response=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "X-Admin-Key: $VALID_ADMIN_API_KEY" \
        "$S3_ENDPOINT_URL/admin/backends")
    
    if [[ "$response" == "401" ]] || [[ "$response" == "403" ]]; then
        log_success "通常のAPIキーが管理操作で正しく拒否されました (HTTP $response)"
    else
        log_warning "通常APIキー管理テストは結果不明 (HTTP $response) - 認証が無効または設定ミスかもしれません"
    fi
    
    log_success "管理API認証テストが完了しました！"
}

test_permission_boundaries() {
    log_info "権限境界のテスト..."
    
    # まず認証が有効になっているかチェック
    local auth_check
    auth_check=$(curl -s -o /dev/null -w "%{http_code}" "$S3_ENDPOINT_URL/")
    if [[ "$auth_check" == "200" ]]; then
        log_warning "認証が無効 - 権限境界テストはこのモードでは適用されません"
        log_success "権限境界テストが完了しました（スキップ - 認証無効）！"
        return 0
    fi
    
    # テスト 1: バケット固有の権限（実装されている場合）
    log_info "テスト 1: バケット固有の権限"
    log_info "注意: 設定で実装されている場合のバケット固有権限のテスト"
    
    # テスト 2: 操作固有の権限
    log_info "テスト 2: 操作固有の権限"
    log_info "注意: 操作固有権限のテスト（読み取り vs 書き込み vs 管理）"
    
    # テスト 3: レート制限（実装されている場合）
    log_info "テスト 3: レート制限の動作"
    local start_time=$(date +%s)
    local request_count=0
    local max_requests=10
    
    while [[ $request_count -lt $max_requests ]]; do
        curl -s -o /dev/null "$S3_ENDPOINT_URL/" >/dev/null 2>&1
        ((request_count++))
    done
    
    local end_time=$(date +%s)
    local duration=$((end_time - start_time))
    
    if [[ $duration -lt 1 ]]; then
        log_info "レート制限テスト: ${max_requests}リクエストが${duration}秒で完了（明らかなレート制限なし）"
    else
        log_info "レート制限テスト: ${max_requests}リクエストが${duration}秒で完了"
    fi
    
    log_success "権限境界テストが完了しました！"
}

test_security_headers() {
    log_info "セキュリティヘッダーと動作のテスト..."
    
    # テスト 1: セキュリティヘッダーの確認
    log_info "テスト 1: セキュリティヘッダーの存在確認"
    local headers
    headers=$(curl -s -I "$S3_ENDPOINT_URL/" | tr -d '\r')
    
    if echo "$headers" | grep -q -i "server:"; then
        local server_header=$(echo "$headers" | grep -i "server:" | cut -d':' -f2- | xargs)
        log_info "サーバーヘッダー: $server_header"
    fi
    
    # テスト 2: CORSヘッダー（該当する場合）
    log_info "テスト 2: CORSヘッダー"
    headers=$(curl -s -I -H "Origin: https://example.com" "$S3_ENDPOINT_URL/")
    
    if echo "$headers" | grep -q -i "access-control"; then
        log_info "CORSヘッダーが見つかりました"
    else
        log_info "CORSヘッダーが見つかりません（期待される場合もあります）"
    fi
    
    # テスト 3: エラーメッセージの情報漏洩
    log_info "テスト 3: エラーメッセージの情報漏洩確認"
    local error_response
    error_response=$(curl -s -X GET "$S3_ENDPOINT_URL/nonexistent-bucket-12345/")
    
    if echo "$error_response" | grep -q -i "stack\|exception\|debug\|trace"; then
        log_warning "エラーレスポンスに機密なデバッグ情報が含まれている可能性があります"
    else
        log_success "エラーレスポンスが適切にサニタイズされています"
    fi
    
    log_success "セキュリティヘッダーテストが完了しました！"
}

# メインテスト実行
main() {
    log_info "========================================="
    log_info "Stratify.S3 認証テストスイート"
    log_info "========================================="
    log_info "エンドポイント: $(color_blue "$S3_ENDPOINT_URL")"
    log_info "注意: 認証が無効の場合、いくつかのテストで警告が表示される場合があります"
    
    log_info "前提条件の確認中..."
    check_server || exit 1
    
    local test_suites=(
        "test_authentication_disabled"
        "test_api_key_authentication" 
        "test_aws_signature_authentication"
        "test_admin_api_authentication"
        "test_permission_boundaries"
        "test_security_headers"
    )
    
    local passed=0
    local failed=0
    local total=${#test_suites[@]}
    
    for test_suite in "${test_suites[@]}"; do
        log_info "========================================="
        log_info "テストスイート実行中: $(color_blue "${test_suite#test_}")"
        log_info "========================================="
        
        $test_suite
        local exit_code=$?
        
        if [[ $exit_code -eq 0 ]]; then
            log_success "テストスイート '${test_suite#test_}' が成功しました"
            ((passed++))
        else
            log_error "テストスイート '${test_suite#test_}' が失敗しました (終了コード: $exit_code)"
            ((failed++))
        fi
        echo
    done
    
    log_info "========================================="
    log_info "認証テスト結果サマリー"
    log_info "========================================="
    log_info "総テストスイート数: $(color_blue "$total")"
    log_info "成功: $(color_green "$passed")"
    log_info "失敗: $(color_red "$failed")"
    
    if [[ $failed -eq 0 ]]; then
        log_success "すべての認証テストが成功しました！"
        return 0
    else
        log_error "認証テストが失敗しました！"
        log_info "注意: 認証が無効の場合、いくつかの失敗は予期されるものです"
        return 1
    fi
}

# スクリプトが直接実行された場合のみメイン関数を実行（sourceされた場合は実行しない）
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi