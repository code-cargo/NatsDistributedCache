#!/bin/bash
set -e

# Navigate to the root directory containing the .sln file
cd "$(dirname "$0")/.."

# Find the .sln file in the current directory
CUR_DIR="$(pwd)"
SLN_FILE=$(ls "$CUR_DIR"/*.sln 2>/dev/null | head -n 1)
if [ -z "$SLN_FILE" ]; then
    echo "Error: No .sln file found."
    exit 1
fi

# Extract the solution name without the extension
SLN_NAME=$(basename "$SLN_FILE" .sln)

# Define key paths
KEYS_DIR="$CUR_DIR/keys"
SNK_FILE="${KEYS_DIR}/${SLN_NAME}.2025-05-12.snk"
PUB_FILE="${KEYS_DIR}/${SLN_NAME}.2025-05-12.pub"

# Function to run sn tool using Docker
run_sn_docker() {
    docker run --rm \
        -v "$KEYS_DIR:/mnt/keys" \
        -w "/mnt/keys" \
        -u "$(id -u):$(id -g)" \
        mono:latest sn "$@"
}

# Check if the SNK file exists
if [ ! -f "$SNK_FILE" ]; then
    echo "Error: Strong Name Key file does not exist: $SNK_FILE"
    exit 1
fi

echo "Strong Name Key file exists: $SNK_FILE"

# Check if the public key file exists
if [ ! -f "$PUB_FILE" ]; then
    echo "Error: Public key file does not exist: $PUB_FILE"
    exit 1
fi

echo "Public key file exists: $PUB_FILE"

# Extract the public key from SNK to compare with existing public key
echo "Verifying Strong Name Key against public key..."

# Create a temporary public key from the SNK file for comparison
TEMP_PUB_FILE="${PUB_FILE}.temp"
run_sn_docker -p "$(basename "${SNK_FILE}")" "$(basename "${TEMP_PUB_FILE}")"

# Compare the extracted public key with the existing public key
TOKEN1=$(run_sn_docker -tp "$(basename "${PUB_FILE}")" | tail -n 1)
TOKEN2=$(run_sn_docker -tp "$(basename "${TEMP_PUB_FILE}")" | tail -n 1)

# Clean up temporary file
rm -f "${TEMP_PUB_FILE}"

# Compare the tokens
if [ "$TOKEN1" != "$TOKEN2" ]; then
    echo "Error: Public key tokens do not match."
    echo "Expected: $TOKEN1"
    echo "Got: $TOKEN2"
    exit 1
fi

echo "Strong Name Key verification succeeded. Keys match."

# Verify that the project file references the SNK file
PROJECT_FILE="$CUR_DIR/src/NatsDistributedCache/NatsDistributedCache.csproj"
RELATIVE_KEY_PATH="..\\..\\keys\\NatsDistributedCache.2025-05-12.snk"

echo "Checking project file for SNK reference..."
if [ ! -f "$PROJECT_FILE" ]; then
    echo "Error: Project file not found: $PROJECT_FILE"
    exit 1
fi

# Check for SignAssembly property
if ! grep -q "<SignAssembly .*>true</SignAssembly>" "$PROJECT_FILE"; then
    echo "Error: Project file does not have SignAssembly set to true"
    exit 1
fi

# Check for AssemblyOriginatorKeyFile property referencing the correct key
if ! grep -q "<AssemblyOriginatorKeyFile .*>.*NatsDistributedCache\.2025-05-12\.snk</AssemblyOriginatorKeyFile>" "$PROJECT_FILE"; then
    echo "Error: Project file does not reference the correct key file"
    exit 1
fi

echo "Project file correctly references the SNK file."

# Display the public key token for reference
echo "Public key token:"
run_sn_docker -tp "$(basename "${PUB_FILE}")"

echo "Verification completed successfully."
