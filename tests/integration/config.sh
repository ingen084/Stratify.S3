#!/bin/bash

# Stratify.S3 Integration Test Configuration

# Server configuration
export S3_ENDPOINT_URL="http://localhost:8000"
export S3_REGION="us-east-1"

# Test configuration
export TEST_BUCKET="integration-test-bucket"
export TEST_BUCKET_2="integration-test-bucket-2"

# AWS CLI configuration for testing
export AWS_ACCESS_KEY_ID="test-access-key"
export AWS_SECRET_ACCESS_KEY="test-secret-key"
export AWS_DEFAULT_REGION="$S3_REGION"

# Directories
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export TEST_DATA_DIR="$SCRIPT_DIR/test-data"
export TEMP_DIR="$SCRIPT_DIR/temp"

# Test file names
export SMALL_FILE="small-file.txt"
export MEDIUM_FILE="medium-file.bin"
export LARGE_FILE="large-file.bin"
export VERY_LARGE_FILE="very-large-file.bin"
export CONTENT_FILE="test-content.txt"

# Colors for output
export RED='\033[0;31m'
export GREEN='\033[0;32m'
export YELLOW='\033[1;33m'
export BLUE='\033[0;34m'
export NC='\033[0m' # No Color

# Helper functions
log_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Color functions for cleaner output
color_blue() {
    echo -e "${BLUE}$1${NC}"
}

color_green() {
    echo -e "${GREEN}$1${NC}"
}

color_red() {
    echo -e "${RED}$1${NC}"
}

# AWS CLI wrapper with endpoint
aws_s3() {
    aws --endpoint-url "$S3_ENDPOINT_URL" s3 "$@"
}

aws_s3api() {
    aws --endpoint-url "$S3_ENDPOINT_URL" s3api "$@"
}

# Utility functions
check_aws_cli() {
    if ! command -v aws >/dev/null 2>&1; then
        log_error "AWS CLI is not installed. Please install it first."
        exit 1
    fi
}

check_server() {
    log_info "Checking if Stratify.S3 server is running..."
    if ! curl -s "$S3_ENDPOINT_URL/health" >/dev/null; then
        log_error "Stratify.S3 server is not running at $S3_ENDPOINT_URL"
        log_info "Please start the server with: dotnet run --project src/Stratify.S3.csproj"
        exit 1
    fi
    log_success "Server is running"
}

cleanup_test_data() {
    log_info "Cleaning up test data..."
    
    # Remove test buckets
    aws_s3 rm "s3://$TEST_BUCKET" --recursive 2>/dev/null || true
    aws_s3 rb "s3://$TEST_BUCKET" 2>/dev/null || true
    
    aws_s3 rm "s3://$TEST_BUCKET_2" --recursive 2>/dev/null || true
    aws_s3 rb "s3://$TEST_BUCKET_2" 2>/dev/null || true
    
    # Clean temp directory
    rm -rf "$TEMP_DIR"/*
    
    log_success "Cleanup completed"
}