# CodeCargo.NatsDistributedCache

## Updating Packages

Use the `dotnet outdated` tool to update packages in `.csproj` files. When updating packages in a project with lock
files, always use the `-n` flag to prevent automatic restoration. To update tools themselves, edit
`.config/dotnet-tools.json`.

```bash
# all updates
# view
dotnet outdated
# apply all (with -n to prevent automatic restore)
dotnet outdated -n -u
# prompt
dotnet outdated -n -u:prompt

# minor updates only
# view
dotnet outdated -vl Major
# apply all (with -n to prevent automatic restore)
dotnet outdated -n -vl Major -u
# prompt
dotnet outdated -n -vl Major -u:prompt

After updating dependencies, you must update the lock files for all supported platforms by running the update script (see next section).

## Updating NuGet Lock Files

This project uses NuGet package lock files for reproducible builds across different platforms. When packages are updated, the lock files need to be regenerated for all supported platforms.

Use the provided script to update all platform-specific lock files:

```bash
./dev/update-nuget-lockfiles.sh
```

This will generate lock files for:

- Linux x64: `packages.linux-x64.lock.json`
- macOS ARM64: `packages.osx-arm64.lock.json`
- Windows x64: `packages.win-x64.lock.json`

These lock files are used automatically based on the runtime identifier specified during build/restore.

**Important**: Always run this script after updating package dependencies to ensure all platform-specific lock files are
properly regenerated.