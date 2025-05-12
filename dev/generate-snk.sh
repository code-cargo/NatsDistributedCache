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

# Get the current date
DATE="$(date "+%Y-%m-%d")"

# Define key names
KEYS_DIR="$CUR_DIR/keys"
SNK_FILE="${KEYS_DIR}/${SLN_NAME}.${DATE}.snk"
PUB_FILE="${KEYS_DIR}/${SLN_NAME}.${DATE}.pub"

# Function to run sn tool using Docker
run_sn_docker() {
    docker run --rm \
        -v "$KEYS_DIR:/mnt/keys" \
        -w "/mnt/keys" \
        -u "$(id -u):$(id -g)" \
        mono:latest sn "$@"
}

# Create the keys directory if it doesn't exist
mkdir -p "$KEYS_DIR"

# Add .gitignore to prevent committing private keys
echo "*.snk" > "${KEYS_DIR}/.gitignore"

# Generate the SNK file directly using sn tool
echo "Generating SNK file..."
run_sn_docker -k "$(basename "${SNK_FILE}")"

# Extract the public key
echo "Extracting public key..."
PUB_KEY_NAME="$(basename "${PUB_FILE}")"
run_sn_docker -p "$(basename "${SNK_FILE}")" "${PUB_KEY_NAME}"

# Display the public key token (helpful for reference)
echo "Public key token:"
run_sn_docker -tp "${PUB_KEY_NAME}"

# Print success message
echo "Generated SNK file: ${SNK_FILE}"
echo "Generated public key file: ${PUB_FILE}"
echo "Done."
