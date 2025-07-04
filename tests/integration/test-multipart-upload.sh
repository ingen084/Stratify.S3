#!/bin/bash

# Stratify.S3 Multipart Upload Integration Test

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/config.sh"

test_multipart_upload() {
    log_info "Starting multipart upload test..."
    
    # Test 1: Create bucket for multipart tests
    log_info "Test 1: Create bucket for multipart tests"
    aws_s3 mb "s3://$TEST_BUCKET"
    log_success "Test bucket created"
    
    # Test 2: Upload large file (should trigger multipart upload)
    log_info "Test 2: Upload large file (10MB)"
    local start_time=$(date +%s)
    aws_s3 cp "$TEST_DATA_DIR/$LARGE_FILE" "s3://$TEST_BUCKET/"
    local end_time=$(date +%s)
    local duration=$((end_time - start_time))
    log_success "Large file uploaded in ${duration}s"
    
    # Test 3: Verify large file upload
    log_info "Test 3: Verify large file upload"
    local object_list
    object_list=$(aws_s3 ls "s3://$TEST_BUCKET/")
    if echo "$object_list" | grep -q "$LARGE_FILE"; then
        log_success "Large file appears in object list"
    else
        log_error "Large file not found in object list"
        exit 1
    fi
    
    # Test 4: Download and verify large file
    log_info "Test 4: Download and verify large file"
    local downloaded_file="$TEMP_DIR/downloaded-$LARGE_FILE"
    start_time=$(date +%s)
    aws_s3 cp "s3://$TEST_BUCKET/$LARGE_FILE" "$downloaded_file"
    end_time=$(date +%s)
    duration=$((end_time - start_time))
    log_success "Large file downloaded in ${duration}s"
    
    # Verify file integrity
    if diff -q "$TEST_DATA_DIR/$LARGE_FILE" "$downloaded_file" >/dev/null; then
        log_success "Downloaded large file matches original"
    else
        log_error "Downloaded large file differs from original"
        exit 1
    fi
    
    # Test 5: Upload very large file (should definitely use multipart)
    log_info "Test 5: Upload very large file (50MB)"
    start_time=$(date +%s)
    aws_s3 cp "$TEST_DATA_DIR/$VERY_LARGE_FILE" "s3://$TEST_BUCKET/"
    end_time=$(date +%s)
    duration=$((end_time - start_time))
    log_success "Very large file uploaded in ${duration}s"
    
    # Test 6: Download and verify very large file
    log_info "Test 6: Download and verify very large file"
    local downloaded_very_large="$TEMP_DIR/downloaded-$VERY_LARGE_FILE"
    start_time=$(date +%s)
    aws_s3 cp "s3://$TEST_BUCKET/$VERY_LARGE_FILE" "$downloaded_very_large"
    end_time=$(date +%s)
    duration=$((end_time - start_time))
    log_success "Very large file downloaded in ${duration}s"
    
    # Verify file integrity
    if diff -q "$TEST_DATA_DIR/$VERY_LARGE_FILE" "$downloaded_very_large" >/dev/null; then
        log_success "Downloaded very large file matches original"
    else
        log_error "Downloaded very large file differs from original"
        exit 1
    fi
    
    log_success "All multipart upload tests passed!"
}

test_multipart_api_directly() {
    log_info "Starting direct multipart API test..."
    
    local test_file="$TEST_DATA_DIR/$MEDIUM_FILE"
    local object_key="multipart-api-test.bin"
    
    # Test 1: Initiate multipart upload
    log_info "Test 1: Initiate multipart upload"
    local upload_response
    upload_response=$(aws_s3api create-multipart-upload \
        --bucket "$TEST_BUCKET" \
        --key "$object_key")
    
    local upload_id
    upload_id=$(echo "$upload_response" | grep '"UploadId"' | cut -d'"' -f4)
    
    if [[ -n "$upload_id" ]]; then
        log_success "Multipart upload initiated: $upload_id"
    else
        log_error "Failed to initiate multipart upload"
        exit 1
    fi
    
    # Test 2: Upload parts
    log_info "Test 2: Upload parts"
    
    # Split file into parts (512KB each)
    local part_size=524288  # 512KB
    local part_dir="$TEMP_DIR/parts"
    mkdir -p "$part_dir"
    
    split -b $part_size "$test_file" "$part_dir/part"
    
    local parts_json=""
    local part_number=1
    
    for part_file in "$part_dir"/part*; do
        if [[ -f "$part_file" ]]; then
            log_info "Uploading part $part_number"
            
            local upload_part_response
            upload_part_response=$(aws_s3api upload-part \
                --bucket "$TEST_BUCKET" \
                --key "$object_key" \
                --part-number "$part_number" \
                --upload-id "$upload_id" \
                --body "$part_file")
            
            local etag
            # Extract the actual ETag value using jq (removes quotes automatically)
            etag=$(echo "$upload_part_response" | jq -r '.ETag' | tr -d '"')
            
            if [[ $part_number -gt 1 ]]; then
                parts_json+=","
            fi
            parts_json+="{\"ETag\":\"$etag\",\"PartNumber\":$part_number}"
            
            log_success "Part $part_number uploaded with ETag: $etag"
            ((part_number++))
        fi
    done
    
    # Test 3: List parts
    log_info "Test 3: List parts"
    local list_parts_response
    list_parts_response=$(aws_s3api list-parts \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --upload-id "$upload_id")
    
    if echo "$list_parts_response" | grep -q '"PartNumber"'; then
        log_success "Parts listed successfully"
    else
        log_error "Failed to list parts"
        exit 1
    fi
    
    # Test 4: Complete multipart upload
    log_info "Test 4: Complete multipart upload"
    local complete_request="{\"Parts\":[$parts_json]}"
    echo "$complete_request" > "$TEMP_DIR/complete-request.json"
    
    local complete_response
    complete_response=$(aws_s3api complete-multipart-upload \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --upload-id "$upload_id" \
        --multipart-upload file://"$TEMP_DIR/complete-request.json")
    
    if echo "$complete_response" | grep -q '"ETag"'; then
        log_success "Multipart upload completed successfully"
    else
        log_error "Failed to complete multipart upload"
        exit 1
    fi
    
    # Test 5: Verify completed upload
    log_info "Test 5: Verify completed upload"
    local downloaded_file="$TEMP_DIR/downloaded-multipart-api-test.bin"
    aws_s3 cp "s3://$TEST_BUCKET/$object_key" "$downloaded_file"
    
    if diff -q "$test_file" "$downloaded_file" >/dev/null; then
        log_success "Multipart uploaded file matches original"
    else
        log_error "Multipart uploaded file differs from original"
        exit 1
    fi
    
    log_success "All direct multipart API tests passed!"
}

test_multipart_abort() {
    log_info "Starting multipart abort test..."
    
    local object_key="abort-test.bin"
    
    # Test 1: Initiate multipart upload
    log_info "Test 1: Initiate multipart upload for abort test"
    local upload_response
    upload_response=$(aws_s3api create-multipart-upload \
        --bucket "$TEST_BUCKET" \
        --key "$object_key")
    
    local upload_id
    upload_id=$(echo "$upload_response" | grep '"UploadId"' | cut -d'"' -f4)
    
    if [[ -n "$upload_id" ]]; then
        log_success "Multipart upload initiated for abort test: $upload_id"
    else
        log_error "Failed to initiate multipart upload for abort test"
        exit 1
    fi
    
    # Test 2: Upload a part
    log_info "Test 2: Upload part for abort test"
    local part_file="$TEST_DATA_DIR/$SMALL_FILE"
    
    aws_s3api upload-part \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --part-number 1 \
        --upload-id "$upload_id" \
        --body "$part_file" >/dev/null
    
    log_success "Part uploaded for abort test"
    
    # Test 3: List parts (should show our part)
    log_info "Test 3: List parts before abort"
    local list_response
    list_response=$(aws_s3api list-parts \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --upload-id "$upload_id")
    
    if echo "$list_response" | grep -q '"PartNumber"'; then
        log_success "Parts found before abort"
    else
        log_error "No parts found before abort"
        exit 1
    fi
    
    # Test 4: Abort multipart upload
    log_info "Test 4: Abort multipart upload"
    aws_s3api abort-multipart-upload \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --upload-id "$upload_id"
    
    log_success "Multipart upload aborted"
    
    # Test 5: Verify abort (list-parts should fail)
    log_info "Test 5: Verify abort"
    set +e  # Allow command to fail
    local list_after_abort
    list_after_abort=$(aws_s3api list-parts \
        --bucket "$TEST_BUCKET" \
        --key "$object_key" \
        --upload-id "$upload_id" 2>&1)
    local list_exit_code=$?
    set -e
    
    if [[ $list_exit_code -ne 0 ]]; then
        log_success "List parts correctly failed after abort"
    else
        log_error "List parts should have failed after abort"
        exit 1
    fi
    
    log_success "Multipart abort test passed!"
}

main() {
    check_aws_cli
    check_server
    
    # Setup
    mkdir -p "$TEMP_DIR"
    
    # Run tests
    test_multipart_upload
    test_multipart_api_directly
    test_multipart_abort
    
    # Cleanup
    cleanup_test_data
    
    log_success "Multipart upload integration test completed successfully!"
}

# Run main function if script is executed directly
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi