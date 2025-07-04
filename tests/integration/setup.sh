#!/bin/bash

# Stratify.S3 Integration Test Setup
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TEST_DATA_DIR="$SCRIPT_DIR/test-data"
TEMP_DIR="$SCRIPT_DIR/temp"

echo "Setting up integration test environment..."

# Create directories
mkdir -p "$TEST_DATA_DIR"
mkdir -p "$TEMP_DIR"

# Create test files of various sizes
echo "Creating test files..."

# Small file (1KB)
echo "This is a small test file for integration testing." > "$TEST_DATA_DIR/small-file.txt"
dd if=/dev/zero of="$TEST_DATA_DIR/small-file.txt" bs=1 count=1024 status=none

# Medium file (1MB)
dd if=/dev/zero of="$TEST_DATA_DIR/medium-file.bin" bs=1M count=1 status=none

# Large file (10MB) - for multipart upload testing
dd if=/dev/zero of="$TEST_DATA_DIR/large-file.bin" bs=1M count=10 status=none

# Very large file (50MB) - for multipart upload testing
dd if=/dev/zero of="$TEST_DATA_DIR/very-large-file.bin" bs=1M count=50 status=none

# Text file with specific content for verification
cat > "$TEST_DATA_DIR/test-content.txt" << 'EOF'
# Stratify.S3 Test Content File
This file contains specific content for integration testing.
It includes multiple lines and special characters: !@#$%^&*()

Line 1: Basic text
Line 2: Numbers 1234567890
Line 3: Unicode: こんにちは 🌟 ñoël

End of test content.
EOF

echo "Test environment setup complete!"
echo "Test data directory: $TEST_DATA_DIR"
echo "Temporary directory: $TEMP_DIR"
echo ""
echo "Files created:"
ls -lh "$TEST_DATA_DIR"