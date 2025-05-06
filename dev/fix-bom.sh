#!/bin/bash

cd "$(dirname "$0")/.."
set -e

# Remove BOM from all text files tracked by git (excluding binary files)
echo "Removing BOM from git-tracked text files..."

# Detect OS for sed compatibility
if [[ "$(uname)" == "Darwin" ]]; then
    # Check if user is using GNU sed instead of default macOS sed
    if sed --version 2>/dev/null | grep -q "GNU sed"; then
        # Using GNU sed on macOS, so no need for the empty string argument
        SED_INPLACE="sed -i"
    else
        # Default macOS sed requires an argument after -i (can be empty string)
        SED_INPLACE="sed -i ''"
    fi
else
    # Linux doesn't need an argument after -i
    SED_INPLACE="sed -i"
fi

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
            echo "Removing BOM from: $file"
            $SED_INPLACE '1s/^\xEF\xBB\xBF//' "$file"
        fi
    fi
done < <(git ls-files)

echo "BOM removal complete"
