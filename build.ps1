param([string]$DotnetPath = "dotnet")
$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot ".build/dotnet"
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $PSScriptRoot ".build/packages" }
if (!$env:NUGET_HTTP_CACHE_PATH) { $env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot ".build/http" }
Push-Location $PSScriptRoot
try {
    & $DotnetPath run --project Scout.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    & $DotnetPath publish Scout.Desktop -c Release -r win-x64 --self-contained true -o artifacts/win-x64 --nologo -m:1
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
} finally { Pop-Location }
