#!/bin/bash

# Stratify.S3 Integration Test Runner

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/config.sh"

# Test suites
TEST_SUITES=(
    "setup.sh"
    "test-basic-operations.sh"
    "test-multipart-upload.sh"
    "test-failover.sh"
)

# Statistics
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0
FAILED_SUITE_NAMES=()

run_test_suite() {
    local test_script="$1"
    local test_name=$(basename "$test_script" .sh)
    
    echo ""
    log_info "========================================"
    log_info "Running test suite: $test_name"
    log_info "========================================"
    
    TOTAL_TESTS=$((TOTAL_TESTS + 1))
    
    if [[ "$test_script" == "setup.sh" ]]; then
        # Run setup script
        if bash "$SCRIPT_DIR/$test_script"; then
            log_success "Setup completed successfully"
            PASSED_TESTS=$((PASSED_TESTS + 1))
        else
            log_error "Setup failed"
            FAILED_TESTS=$((FAILED_TESTS + 1))
            FAILED_SUITE_NAMES+=("$test_name")
            return 1
        fi
    else
        # Run test script
        if bash "$SCRIPT_DIR/$test_script"; then
            log_success "Test suite '$test_name' passed"
            PASSED_TESTS=$((PASSED_TESTS + 1))
        else
            log_error "Test suite '$test_name' failed"
            FAILED_TESTS=$((FAILED_TESTS + 1))
            FAILED_SUITE_NAMES+=("$test_name")
        fi
    fi
}

print_summary() {
    echo ""
    log_info "========================================"
    log_info "TEST SUMMARY"
    log_info "========================================"
    echo -e "Total test suites: ${BLUE}$TOTAL_TESTS${NC}"
    echo -e "Passed: ${GREEN}$PASSED_TESTS${NC}"
    echo -e "Failed: ${RED}$FAILED_TESTS${NC}"
    
    if [[ $FAILED_TESTS -gt 0 ]]; then
        echo ""
        log_error "Failed test suites:"
        for failed_suite in "${FAILED_SUITE_NAMES[@]}"; do
            echo -e "  ${RED}- $failed_suite${NC}"
        done
        echo ""
        log_error "Integration tests FAILED!"
        return 1
    else
        echo ""
        log_success "All integration tests PASSED!"
        return 0
    fi
}

check_prerequisites() {
    log_info "Checking prerequisites..."
    
    # Check AWS CLI
    check_aws_cli
    
    # Check server
    check_server
    
    # Check if server supports admin API (optional)
    local admin_check
    admin_check=$(curl -s -H "X-Admin-Key: admin-api-key-secure" "$S3_ENDPOINT_URL/admin/backends" || echo "")
    if [[ -n "$admin_check" ]] && echo "$admin_check" | grep -q '"Name"'; then
        log_success "Admin API is available"
    else
        log_warning "Admin API may not be available or authentication is disabled"
        log_warning "Some failover tests may not work properly"
    fi
    
    log_success "Prerequisites check completed"
}

cleanup_on_exit() {
    log_info "Performing final cleanup..."
    cleanup_test_data 2>/dev/null || true
}

main() {
    echo ""
    log_info "========================================"
    log_info "Stratify.S3 Integration Test Suite"
    log_info "========================================"
    echo -e "Endpoint: ${BLUE}$S3_ENDPOINT_URL${NC}"
    echo -e "Test Bucket: ${BLUE}$TEST_BUCKET${NC}"
    echo ""
    
    # Set up cleanup on exit
    trap cleanup_on_exit EXIT
    
    # Check prerequisites
    check_prerequisites
    
    # Initial cleanup
    cleanup_test_data
    
    # Run test suites
    local continue_tests=true
    for test_suite in "${TEST_SUITES[@]}"; do
        if [[ "$continue_tests" == "true" ]]; then
            if ! run_test_suite "$test_suite"; then
                if [[ "$test_suite" == "setup.sh" ]]; then
                    log_error "Setup failed, cannot continue with tests"
                    continue_tests=false
                else
                    log_warning "Test suite failed, but continuing with remaining tests"
                fi
            fi
        else
            log_info "Skipping $test_suite due to setup failure"
            TOTAL_TESTS=$((TOTAL_TESTS + 1))
            FAILED_TESTS=$((FAILED_TESTS + 1))
            FAILED_SUITE_NAMES+=("$(basename "$test_suite" .sh)")
        fi
    done
    
    # Print summary and exit with appropriate code
    if print_summary; then
        exit 0
    else
        exit 1
    fi
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --endpoint)
            S3_ENDPOINT_URL="$2"
            shift 2
            ;;
        --bucket)
            TEST_BUCKET="$2"
            shift 2
            ;;
        --help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --endpoint URL    Set S3 endpoint URL (default: $S3_ENDPOINT_URL)"
            echo "  --bucket NAME     Set test bucket name (default: $TEST_BUCKET)"
            echo "  --help           Show this help message"
            echo ""
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

# Run main function if script is executed directly
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
    main "$@"
fi