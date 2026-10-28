param([string]$DotnetPath = "dotnet", [switch]$SkipBuild)
$ErrorActionPreference = "Stop"
if (!$SkipBuild) { & (Join-Path $PSScriptRoot "build.ps1") -DotnetPath $DotnetPath }
[xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$release = Join-Path $PSScriptRoot "artifacts/win-x64"
if (!(Test-Path -LiteralPath (Join-Path $release "Scout.exe"))) { throw "Build Scout first." }
$out = Join-Path $PSScriptRoot "artifacts/releases"
[IO.Directory]::CreateDirectory($out) | Out-Null
$zipPath = Join-Path $out ("Scout-" + $version + "-Windows.zip")
if (Test-Path -LiteralPath $zipPath) { throw "Package already exists: $zipPath. Preserve or remove it before repackaging." }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.md") -Destination $release
$dotnetExe = (Get-Command $DotnetPath -ErrorAction Stop).Source
foreach ($notice in @("LICENSE.txt","ThirdPartyNotices.txt")) {
    $path = Join-Path (Split-Path $dotnetExe) $notice
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $release ("DOTNET-" + $notice)) }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse) {
        if ($file.Extension -eq ".pdb") { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, [IO.Path]::GetRelativePath($release, $file.FullName), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash | Set-Content -LiteralPath ($zipPath + ".sha256")
Get-Item -LiteralPath $zipPath
