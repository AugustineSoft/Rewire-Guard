<#
.SYNOPSIS
    Builds the Rewire Guard installer and the portable build.

.DESCRIPTION
    Produces two things in dist\:

      RewireGuard-Setup.exe          Installer. Per-user, no admin rights needed.
      RewireGuard-Portable.zip       Unzip and run, no install.

    Both are published self-contained, so neither needs the .NET Desktop Runtime present. That
    costs about 90 MB and removes the single most common "it won't start" failure.

.PARAMETER Version
    Product version, e.g. 1.0.1. Must increase for an upgrade to replace an existing install.

.PARAMETER IncludeModel
    Bundle Models\model.onnx (default). -IncludeModel:$false gives a far smaller build that
    reports the missing model on first run.

.PARAMETER PortableOnly
    Skip the installer. Useful while iterating, since compressing 450 MB into a cab is slow.

.EXAMPLE
    .\installer\build.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [bool]$IncludeModel = $true,
    [switch]$PortableOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$publishDir = Join-Path $root 'bin\publish'
$portableDir = Join-Path $dist 'RewireGuard-Portable'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0, got '$Version'."
}

function Write-Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Get-SizeMb($path) {
    if (-not (Test-Path $path)) { return 0 }
    $item = Get-Item $path
    if ($item.PSIsContainer) {
        return [math]::Round((Get-ChildItem $path -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    }
    return [math]::Round($item.Length / 1MB, 1)
}

# The app locks its own files while running, and the MSI would hit a file-in-use prompt anyway.
if (Get-Process -Name RewireGuard -ErrorAction SilentlyContinue) {
    throw "Rewire Guard is running. Quit it from the tray icon before building."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---------------------------------------------------------------- installer payload
Write-Step "Publishing application (self-contained, v$Version)"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

# Publish the project, never the solution: a solution-level publish drops the test project's
# code-coverage and instrumentation binaries into the same folder, and they end up shipped.
dotnet publish (Join-Path $root 'RewireGuard.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

if (-not (Test-Path (Join-Path $publishDir 'RewireGuard.exe'))) {
    throw "Publish finished but produced no RewireGuard.exe."
}
Write-Host ("  payload: {0} MB across {1} files" -f (Get-SizeMb $publishDir), (Get-ChildItem $publishDir -Recurse -File).Count)

if (-not $IncludeModel) {
    $models = Join-Path $publishDir 'Models'
    if (Test-Path $models) { Remove-Item -Recurse -Force $models }
    Write-Host "  model excluded (-IncludeModel:`$false)"
} elseif (-not (Test-Path (Join-Path $publishDir 'Models\model.onnx'))) {
    Write-Warning "No Models\model.onnx found. The installer will ship without a model."
}

# ---------------------------------------------------------------- portable build
Write-Step "Building portable build"
if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }

# Single file so the portable download really is one executable. The model stays beside it
# rather than inside it: a 330 MB embedded resource would be extracted to a temp folder on every
# launch, and AppContext.BaseDirectory still resolves to the exe's own folder in single-file
# mode, so Models\model.onnx next to the exe is found normally.
dotnet publish (Join-Path $root 'RewireGuard.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $portableDir
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

# The model is copied to the output by the csproj; keep it beside the exe, not inside it.
$portableModel = Join-Path $portableDir 'Models\model.onnx'
if ($IncludeModel -and -not (Test-Path $portableModel)) {
    Write-Warning "Portable build has no Models\model.onnx."
}
if (-not $IncludeModel -and (Test-Path (Join-Path $portableDir 'Models'))) {
    Remove-Item -Recurse -Force (Join-Path $portableDir 'Models')
}

$zip = Join-Path $dist 'RewireGuard-Portable.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("  portable: {0} MB unpacked, {1} MB zipped" -f (Get-SizeMb $portableDir), (Get-SizeMb $zip))

# ---------------------------------------------------------------- installer
if ($PortableOnly) {
    Write-Step "Skipping installer (-PortableOnly)"
} else {
    Write-Step "Building installer (this compresses ~$(Get-SizeMb $publishDir) MB, expect a few minutes)"

    dotnet build (Join-Path $PSScriptRoot 'bundle\RewireGuardBundle.wixproj') `
        -c Release `
        -p:HarvestPath=$publishDir `
        -p:ProductVersion=$Version `
        -p:IncludeModel=$($IncludeModel.ToString().ToLower())
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

    $setup = Get-ChildItem (Join-Path $PSScriptRoot 'bundle\bin') -Recurse -Filter 'RewireGuard-Setup.exe' |
             Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $setup) { throw "Bundle built but RewireGuard-Setup.exe was not found." }

    Copy-Item $setup.FullName (Join-Path $dist 'RewireGuard-Setup.exe') -Force
}

# ---------------------------------------------------------------- summary
Write-Step "Done"
Get-ChildItem $dist -File | ForEach-Object {
    "  {0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
Write-Host "`n  output: $dist" -ForegroundColor Green
Write-Host "  note: unsigned, so SmartScreen will warn on first run (More info -> Run anyway)." -ForegroundColor DarkYellow
