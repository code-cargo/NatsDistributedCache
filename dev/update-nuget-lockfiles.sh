#!/bin/bash

cd "$(dirname "$0")/.."
set -e

for runtime in linux-x64 osx-arm64 win-x64; do
    dotnet restore -p:NuGetLockRuntimeIdentifier="$runtime" --force-evaluate
done
