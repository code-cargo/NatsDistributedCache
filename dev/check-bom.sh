#!/bin/bash

cd "$(dirname "$0")/.."
set -e

# Check for BOM in git-tracked text files
echo "Checking for BOM in git-tracked text files..."

# Track if we found any files with BOM
found_bom="false"

# Process all tracked files
while read -r file; do
    # Check if file exists (skip deleted files tracked by git)
    if [[ ! -f "$file" ]]; then
        continue
    fi

    # Check if file is binary using git-diff
    if ! git diff --no-index --numstat /dev/null "$file" | grep -q "^-"; then
        # Check if file has BOM
        if head -c3 "$file" | grep -q $'\xEF\xBB\xBF'; then
            echo "Found BOM in: $file"
            found_bom="true"
        fi
    fi
done < <(git ls-files)

# Exit with error if we found any BOMs
if [ "$found_bom" = "true" ]; then
    echo "Error: Found files with BOM. Run './dev/fix-bom.sh' to fix them."
    exit 1
fi

echo "No BOMs found in text files."
exit 0
