#!/bin/bash

# Stratify.S3 Basic Operations Integration Test

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/config.sh"

test_basic_operations() {
    log_info "Starting basic operations test..."
    
    # Test 1: List buckets (should be empty initially)
    log_info "Test 1: List buckets"
    local bucket_list
    bucket_list=$(aws_s3 ls)
    if [[ -z "$bucket_list" ]]; then
        log_success "Initial bucket list is empty"
    else
        log_warning "Initial bucket list is not empty: $bucket_list"
    fi
    
    # Test 2: Create bucket
    log_info "Test 2: Create bucket"
    aws_s3 mb "s3://$TEST_BUCKET"
    log_success "Bucket created: $TEST_BUCKET"
    
    # Test 3: List buckets (should contain our bucket)
    log_info "Test 3: Verify bucket creation"
    bucket_list=$(aws_s3 ls)
    if echo "$bucket_list" | grep -q "$TEST_BUCKET"; then
        log_success "Bucket appears in list"
    else
        log_error "Bucket not found in list"
        exit 1
    fi
    
    # Test 4: List objects (should be empty)
    log_info "Test 4: List objects in empty bucket"
    local object_list
    object_list=$(aws_s3 ls "s3://$TEST_BUCKET/")
    if [[ -z "$object_list" ]]; then
        log_success "Bucket is empty as expected"
    else
        log_error "Bucket should be empty but contains: $object_list"
        exit 1
    fi
    
    # Test 5: Upload small file
    log_info "Test 5: Upload small file"
    aws_s3 cp "$TEST_DATA_DIR/$SMALL_FILE" "s3://$TEST_BUCKET/"
    log_success "Small file uploaded"
    
    # Test 6: List objects (should contain our file)
    log_info "Test 6: Verify file upload"
    object_list=$(aws_s3 ls "s3://$TEST_BUCKET/")
    if echo "$object_list" | grep -q "$SMALL_FILE"; then
        log_success "File appears in object list"
    else
        log_error "File not found in object list"
        exit 1
    fi
    
    # Test 7: Download file and verify content
    log_info "Test 7: Download and verify file"
    local downloaded_file="$TEMP_DIR/downloaded-$SMALL_FILE"
    aws_s3 cp "s3://$TEST_BUCKET/$SMALL_FILE" "$downloaded_file"
    
    if diff -q "$TEST_DATA_DIR/$SMALL_FILE" "$downloaded_file" >/dev/null; then
        log_success "Downloaded file matches original"
    else
        log_error "Downloaded file differs from original"
        exit 1
    fi
    
    # Test 8: Upload file with path
    log_info "Test 8: Upload file with path"
    aws_s3 cp "$TEST_DATA_DIR/$CONTENT_FILE" "s3://$TEST_BUCKET/folder/subfolder/$CONTENT_FILE"
    log_success "File uploaded with path"
    
    # Test 9: List objects with prefix
    log_info "Test 9: List objects with prefix"
    object_list=$(aws_s3 ls "s3://$TEST_BUCKET/folder/" --recursive)
    if echo "$object_list" | grep -q "folder/subfolder/$CONTENT_FILE"; then
        log_success "File with path appears in list"
    else
        log_error "File with path not found"
        exit 1
    fi
    
    # Test 10: Delete file
    log_info "Test 10: Delete file"
    aws_s3 rm "s3://$TEST_BUCKET/$SMALL_FILE"
    log_success "File deleted"
    
    # Test 11: Verify file deletion
    log_info "Test 11: Verify file deletion"
    object_list=$(aws_s3 ls "s3://$TEST_BUCKET/")
    if echo "$object_list" | grep -q "$SMALL_FILE"; then
        log_error "File still exists after deletion"
        exit 1
    else
        log_success "File successfully deleted"
    fi
    
    # Test 12: Delete file with path
    log_info "Test 12: Delete file with path"
    aws_s3 rm "s3://$TEST_BUCKET/folder/subfolder/$CONTENT_FILE"
    log_success "File with path deleted"
    
    log_success "All basic operations tests passed!"
}

main() {
    check_aws_cli
    check_server
    
    # Setup
    mkdir -p "$TEMP_DIR"
    
    # Run tests
    test_basic_operations
    
    # Cleanup
    cleanup_test_data
    
    log_success "Basic operations integration test completed successfully!"
}

# Run main function if script is executed directly
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi