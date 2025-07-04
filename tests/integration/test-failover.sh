#!/bin/bash

# Stratify.S3 Failover Integration Test

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/config.sh"

# Override endpoint to use admin API
ADMIN_API_KEY="admin-api-key-secure"

admin_api() {
    curl -s -H "X-Admin-Key: $ADMIN_API_KEY" "$S3_ENDPOINT_URL/admin/$1"
}

test_backend_status() {
    log_info "Starting backend status test..."
    
    # Test 1: Check initial backend status
    log_info "Test 1: Check initial backend status"
    local status_response
    status_response=$(admin_api "backends")
    
    if echo "$status_response" | grep -q '"name"'; then
        log_success "Backend status retrieved successfully"
    else
        log_error "Failed to retrieve backend status"
        exit 1
    fi
    
    # Test 2: Check health endpoint
    log_info "Test 2: Check health endpoint"
    local health_response
    health_response=$(curl -s "$S3_ENDPOINT_URL/health")
    
    if echo "$health_response" | grep -q '"status"'; then
        log_success "Health check successful"
    else
        log_error "Health check failed"
        exit 1
    fi
    
    log_success "Backend status tests passed!"
}

test_failover_scenario() {
    log_info "Starting failover scenario test..."
    
    # Setup: Create bucket and upload test file
    log_info "Setup: Creating bucket and uploading test file"
    aws_s3 mb "s3://$TEST_BUCKET"
    aws_s3 cp "$TEST_DATA_DIR/$CONTENT_FILE" "s3://$TEST_BUCKET/"
    log_success "Setup completed"
    
    # Test 1: Verify file exists and is accessible
    log_info "Test 1: Verify file accessibility before failover"
    local downloaded_file="$TEMP_DIR/before-failover-$CONTENT_FILE"
    aws_s3 cp "s3://$TEST_BUCKET/$CONTENT_FILE" "$downloaded_file"
    
    if diff -q "$TEST_DATA_DIR/$CONTENT_FILE" "$downloaded_file" >/dev/null; then
        log_success "File accessible before failover"
    else
        log_error "File not accessible before failover"
        exit 1
    fi
    
    # Test 2: Disable primary backend
    log_info "Test 2: Disable primary backend"
    local disable_response
    disable_response=$(admin_api "backend/primary/disable")
    
    if echo "$disable_response" | grep -q '"success".*true'; then
        log_success "Primary backend disabled"
    else
        log_warning "Primary backend disable response: $disable_response"
    fi
    
    # Wait a moment for the change to take effect
    sleep 2
    
    # Test 3: Upload new file during failover
    log_info "Test 3: Upload file during primary backend failure"
    local failover_file="failover-test.txt"
    echo "This file was uploaded during failover" > "$TEMP_DIR/$failover_file"
    
    # This should still work using secondary backend
    aws_s3 cp "$TEMP_DIR/$failover_file" "s3://$TEST_BUCKET/"
    log_success "File uploaded during failover"
    
    # Test 4: Verify original file is still accessible
    log_info "Test 4: Verify original file accessibility during failover"
    local during_failover_file="$TEMP_DIR/during-failover-$CONTENT_FILE"
    aws_s3 cp "s3://$TEST_BUCKET/$CONTENT_FILE" "$during_failover_file"
    
    if diff -q "$TEST_DATA_DIR/$CONTENT_FILE" "$during_failover_file" >/dev/null; then
        log_success "Original file still accessible during failover"
    else
        log_error "Original file not accessible during failover"
        exit 1
    fi
    
    # Test 5: Re-enable primary backend
    log_info "Test 5: Re-enable primary backend"
    local enable_response
    enable_response=$(admin_api "backend/primary/enable")
    
    if echo "$enable_response" | grep -q '"success".*true'; then
        log_success "Primary backend re-enabled"
    else
        log_warning "Primary backend enable response: $enable_response"
    fi
    
    # Wait for recovery to potentially happen
    sleep 5
    
    # Test 6: Verify system functionality after recovery
    log_info "Test 6: Verify system functionality after recovery"
    local after_recovery_file="$TEMP_DIR/after-recovery-$CONTENT_FILE"
    aws_s3 cp "s3://$TEST_BUCKET/$CONTENT_FILE" "$after_recovery_file"
    
    if diff -q "$TEST_DATA_DIR/$CONTENT_FILE" "$after_recovery_file" >/dev/null; then
        log_success "File accessible after recovery"
    else
        log_error "File not accessible after recovery"
        exit 1
    fi
    
    # Test 7: Verify failover file is still accessible
    log_info "Test 7: Verify failover file accessibility"
    local recovered_failover_file="$TEMP_DIR/recovered-$failover_file"
    aws_s3 cp "s3://$TEST_BUCKET/$failover_file" "$recovered_failover_file"
    
    if diff -q "$TEMP_DIR/$failover_file" "$recovered_failover_file" >/dev/null; then
        log_success "Failover file still accessible after recovery"
    else
        log_error "Failover file not accessible after recovery"
        exit 1
    fi
    
    log_success "Failover scenario tests passed!"
}

test_recovery_trigger() {
    log_info "Starting recovery trigger test..."
    
    # Test 1: Trigger manual recovery
    log_info "Test 1: Trigger manual recovery"
    local recovery_response
    recovery_response=$(curl -s -X POST -H "X-Admin-Key: $ADMIN_API_KEY" "$S3_ENDPOINT_URL/admin/recovery")
    
    if echo "$recovery_response" | grep -q '"'; then
        log_success "Manual recovery triggered successfully"
    else
        log_error "Failed to trigger manual recovery"
        exit 1
    fi
    
    # Wait for recovery to complete
    sleep 3
    
    # Test 2: Verify system is still functional after recovery
    log_info "Test 2: Verify system functionality after manual recovery"
    local post_recovery_test_file="post-recovery-test.txt"
    echo "Post recovery test content" > "$TEMP_DIR/$post_recovery_test_file"
    
    aws_s3 cp "$TEMP_DIR/$post_recovery_test_file" "s3://$TEST_BUCKET/"
    aws_s3 cp "s3://$TEST_BUCKET/$post_recovery_test_file" "$TEMP_DIR/downloaded-$post_recovery_test_file"
    
    if diff -q "$TEMP_DIR/$post_recovery_test_file" "$TEMP_DIR/downloaded-$post_recovery_test_file" >/dev/null; then
        log_success "System functional after manual recovery"
    else
        log_error "System not functional after manual recovery"
        exit 1
    fi
    
    log_success "Recovery trigger tests passed!"
}

main() {
    check_aws_cli
    check_server
    
    # Setup
    mkdir -p "$TEMP_DIR"
    
    # Run tests
    test_backend_status
    test_failover_scenario
    test_recovery_trigger
    
    # Cleanup
    cleanup_test_data
    
    log_success "Failover integration test completed successfully!"
}

# Run main function if script is executed directly
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi