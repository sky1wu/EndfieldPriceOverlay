[CmdletBinding()]
param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\EndfieldPriceOverlay\EndfieldPriceOverlay.csproj"
$output = Join-Path $root "artifacts\publish\$Runtime"

dotnet publish $project `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $output

Write-Host "发布完成：$output"
