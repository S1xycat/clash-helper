$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = "0.1.1"
$releaseDir = Join-Path $root "release"
$packageDir = Join-Path $releaseDir "clash-helper-v$version"
$zipPath = Join-Path $releaseDir "clash-helper-v$version.zip"

& (Join-Path $root "build.ps1")

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $root "bin\ClashHelper.exe") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "release-notes.md") -Destination $packageDir

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Host "Release package: $zipPath"
