<#
.SYNOPSIS
    Builds the Rewire Guard installer and the portable build.

.DESCRIPTION
    Produces in dist\:

      RewireGuard-Setup.exe          Installer. Per-user, no admin rights needed.
      RewireGuard-Portable.zip       Unzip and run, no install.

    Both are published self-contained, so neither needs the .NET Desktop Runtime present.

    Both also carry more than one inference runtime. Only one ONNX Runtime native library can be
    loaded at a time -- OpenVINO and DirectML each ship their own onnxruntime.dll built with
    different execution providers -- so the extra sets live under Accelerators\ and the app copies
    the selected one next to its executable at startup. That is what lets Settings offer a choice
    between the NPU and the GPU without a separate download per accelerator.

.PARAMETER Version
    Product version, e.g. 1.0.1. Must increase for an upgrade to replace an existing install.

.PARAMETER IncludeModel
    Bundle Models\model.onnx (default). -IncludeModel:$false gives a far smaller build that
    downloads the model on first run instead.

.PARAMETER PortableOnly
    Skip the installer. Useful while iterating, since compressing ~650 MB into a cab is slow.

.PARAMETER Runtimes
    Which inference runtimes to ship. Defaults to OpenVINO and DirectML, which between them cover
    Intel NPUs, every DirectX 12 GPU, and CPU fallback (both include the CPU provider). Pass a
    single runtime for a smaller build with no accelerator choice.

.EXAMPLE
    .\installer\build.ps1 -Version 1.0.1

.EXAMPLE
    .\installer\build.ps1 -Version 1.0.1 -Runtimes OpenVINO -PortableOnly
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [bool]$IncludeModel = $true,
    [switch]$PortableOnly,
    [ValidateSet('OpenVINO', 'DirectML', 'CPU')]
    [string[]]$Runtimes = @('OpenVINO', 'DirectML')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$publishDir = Join-Path $root 'bin\publish'
$portableDir = Join-Path $dist 'RewireGuard-Portable'
$stageRoot = Join-Path $root 'bin\accel-stage'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.0.0, got '$Version'."
}

# The first runtime is the one placed next to the executable at build time, so the app works
# before it has ever run the swap.
$defaultRuntime = $Runtimes[0]

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

# ---------------------------------------------------------------- publish each runtime
Write-Step "Publishing $($Runtimes.Count) runtime variant(s), v$Version"
if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }

$variantDirs = @{}
foreach ($runtime in $Runtimes) {
    $out = Join-Path $stageRoot $runtime

    # Publish the project, never the solution: a solution-level publish drops the test project's
    # code-coverage and instrumentation binaries into the same folder, and they get shipped.
    dotnet publish (Join-Path $root 'RewireGuard.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Version=$Version `
        -p:Accelerator=$runtime `
        -o $out | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $runtime." }
    if (-not (Test-Path (Join-Path $out 'RewireGuard.exe'))) {
        throw "Publish for $runtime produced no RewireGuard.exe."
    }

    $variantDirs[$runtime] = $out
    Write-Host ("  {0,-9} {1,7:N1} MB" -f $runtime, (Get-SizeMb $out))
}

<#
    Split the publishes into a shared payload plus per-runtime native sets.

    Derived by comparing the variants rather than hardcoding filenames, so a future ONNX Runtime
    version that adds or renames a native library is handled without editing this script. A file
    belongs to a runtime if it appears in only one variant, or appears in several with different
    contents. RewireGuard.* is excluded: the managed assembly and its deps.json differ between
    builds for reasons unrelated to the runtime (non-deterministic module ids, differing package
    references) and must stay in the shared payload.
#>
function Get-RelativeFiles($dir) {
    Get-ChildItem $dir -Recurse -File | ForEach-Object { $_.FullName.Substring($dir.Length).TrimStart('\') }
}

$reference = $variantDirs[$defaultRuntime]
$runtimeSpecific = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

if ($Runtimes.Count -gt 1) {
    foreach ($runtime in $Runtimes) {
        foreach ($rel in Get-RelativeFiles $variantDirs[$runtime]) {
            if ($rel -like 'RewireGuard.*') { continue }

            $isSpecific = $false
            foreach ($other in $Runtimes) {
                if ($other -eq $runtime) { continue }
                $otherPath = Join-Path $variantDirs[$other] $rel
                if (-not (Test-Path $otherPath)) { $isSpecific = $true; break }
                if ((Get-FileHash (Join-Path $variantDirs[$runtime] $rel)).Hash -ne (Get-FileHash $otherPath).Hash) {
                    $isSpecific = $true; break
                }
            }
            if ($isSpecific) { [void]$runtimeSpecific.Add($rel) }
        }
    }
}

# Debug layers and link-time artifacts are never loaded at runtime and cost tens of megabytes.
$excluded = @('*.lib', 'DirectML.Debug.*', '*.pdb')

function Build-Payload($destination) {
    if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    # Shared payload, taken from the default runtime's publish.
    foreach ($rel in Get-RelativeFiles $reference) {
        if ($runtimeSpecific.Contains($rel)) { continue }
        $target = Join-Path $destination $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item (Join-Path $reference $rel) $target -Force
    }

    if ($Runtimes.Count -le 1) { return 0 }

    # Per-runtime native sets only. The active runtime is NOT also duplicated next to the
    # executable: that would ship the default set twice and cost ~123 MB for OpenVINO. The app
    # copies the set it needs on first launch, before it touches ONNX Runtime.
    #
    # No .active marker is written either, precisely so the first launch always performs a copy
    # and the executable is never left without a native runtime beside it.
    $accelRoot = Join-Path $destination 'Accelerators'
    $total = 0
    foreach ($runtime in $Runtimes) {
        $set = Join-Path $accelRoot $runtime
        New-Item -ItemType Directory -Force -Path $set | Out-Null

        foreach ($rel in $runtimeSpecific) {
            $source = Join-Path $variantDirs[$runtime] $rel
            if (-not (Test-Path $source)) { continue }

            $name = Split-Path -Leaf $rel
            if ($excluded | Where-Object { $name -like $_ }) { continue }

            Copy-Item $source (Join-Path $set $name) -Force
            $total++
        }
    }

    return $total
}

Write-Step "Assembling payload"
$copied = Build-Payload $publishDir
Write-Host ("  shared + {0} runtime files -> {1} MB across {2} files" -f `
    $copied, (Get-SizeMb $publishDir), (Get-ChildItem $publishDir -Recurse -File).Count)
if ($Runtimes.Count -gt 1) {
    foreach ($runtime in $Runtimes) {
        Write-Host ("    Accelerators\{0,-9} {1,6:N1} MB" -f $runtime, (Get-SizeMb (Join-Path $publishDir "Accelerators\$runtime")))
    }
}

if (-not $IncludeModel) {
    $models = Join-Path $publishDir 'Models'
    if (Test-Path $models) { Remove-Item -Recurse -Force $models }
    Write-Host "  model excluded (-IncludeModel:`$false)"
} elseif (-not (Test-Path (Join-Path $publishDir 'Models\model.onnx'))) {
    Write-Warning "No Models\model.onnx found; the build will download it on first run."
}

# ---------------------------------------------------------------- portable build
Write-Step "Building portable build"

# Single-file so the portable download really is one executable. The model and the accelerator
# sets stay beside it: AppContext.BaseDirectory resolves to the exe's own folder in single-file
# mode, so both are found normally, and a 330 MB embedded model would otherwise be extracted to
# a temp folder on every launch.
$portableExeDir = Join-Path $root 'bin\portable-exe'
if (Test-Path $portableExeDir) { Remove-Item -Recurse -Force $portableExeDir }

dotnet publish (Join-Path $root 'RewireGuard.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -p:Accelerator=$defaultRuntime `
    -o $portableExeDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed." }

if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null

# Deliberately no Accelerators\ folder here. Bundling native libraries into a single-file exe
# extracts them to a temp directory at launch and loads them from there, so a runtime copied next
# to the exe would never take effect -- the swap only works in the unpacked installer layout.
# The portable build therefore carries one runtime, baked in, and the Settings picker hides
# itself when it finds no Accelerators folder.
Copy-Item (Join-Path $portableExeDir 'RewireGuard.exe') $portableDir -Force
foreach ($extra in @('appsettings.json', 'Models')) {
    $source = Join-Path $publishDir $extra
    if (Test-Path $source) { Copy-Item $source $portableDir -Recurse -Force }
}

$zip = Join-Path $dist 'RewireGuard-Portable.zip'
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("  portable: {0} MB unpacked, {1} MB zipped ({2} runtime, no accelerator choice)" -f `
    (Get-SizeMb $portableDir), (Get-SizeMb $zip), $defaultRuntime)

# ---------------------------------------------------------------- installer
if ($PortableOnly) {
    Write-Step "Skipping installer (-PortableOnly)"
} else {
    # WiX's <Files> harvester has no Exclude attribute, so the payload is filtered here instead.
    # A hardlinked staging tree keeps this near-instant rather than copying 650 MB.
    Write-Step "Staging installer payload"
    $staging = Join-Path $root 'bin\installer-staging'
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    foreach ($file in Get-ChildItem $publishDir -Recurse -File -Force) {
        $relative = $file.FullName.Substring($publishDir.Length).TrimStart('\')

        # Never package appsettings.json: the app writes defaults on first run, so an upgrade
        # cannot clobber settings the user changed.
        if ($relative -eq 'appsettings.json') { continue }

        $target = Join-Path $staging $relative
        $targetDir = Split-Path -Parent $target
        if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }

        try { New-Item -ItemType HardLink -Path $target -Target $file.FullName -ErrorAction Stop | Out-Null }
        catch { Copy-Item $file.FullName $target -Force }
    }
    Write-Host ("  staged: {0} MB across {1} files" -f (Get-SizeMb $staging), (Get-ChildItem $staging -Recurse -File -Force).Count)

    # WiX's incremental build tracks the .wxs sources but NOT the contents of the harvested
    # folder. Change the payload without touching a .wxs and MSBuild declares everything
    # up-to-date and silently reuses the previous MSI -- which is how you ship an installer
    # containing the wrong files while the build reports success. Always start clean.
    Write-Step "Clearing previous installer output"
    foreach ($stale in @('msi\bin', 'msi\obj', 'bundle\bin', 'bundle\obj')) {
        $path = Join-Path $PSScriptRoot $stale
        if (Test-Path $path) { Remove-Item -Recurse -Force $path }
    }

    Write-Step "Building installer (compressing ~$(Get-SizeMb $staging) MB, expect a few minutes)"

    dotnet build (Join-Path $PSScriptRoot 'bundle\RewireGuardBundle.wixproj') `
        -c Release `
        -p:HarvestPath=$staging `
        -p:ProductVersion=$Version
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

    $setup = Get-ChildItem (Join-Path $PSScriptRoot 'bundle\bin') -Recurse -Filter 'RewireGuard-Setup.exe' |
             Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $setup) { throw "Bundle built but RewireGuard-Setup.exe was not found." }

    # Guard against shipping a stale or truncated installer. The payload compresses to roughly
    # 55-75% of its uncompressed size, so anything under a third of it means the wrong content
    # got packaged, however cheerfully the build reported success.
    $setupMb = [math]::Round($setup.Length / 1MB, 1)
    $floorMb = [math]::Round((Get-SizeMb $staging) / 3, 1)
    if ($setupMb -lt $floorMb) {
        throw "RewireGuard-Setup.exe is only $setupMb MB but the payload was $(Get-SizeMb $staging) MB. " +
              "That means a stale or partial package was produced. Delete installer\msi\bin, " +
              "installer\msi\obj, installer\bundle\bin and installer\bundle\obj, then rebuild."
    }

    Copy-Item $setup.FullName (Join-Path $dist 'RewireGuard-Setup.exe') -Force
    Write-Host "  installer: $setupMb MB"
}

# ---------------------------------------------------------------- summary
Write-Step "Done"
Get-ChildItem $dist -File | ForEach-Object {
    "  {0,-34} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
Write-Host "`n  runtimes shipped: $($Runtimes -join ', ') (default $defaultRuntime)" -ForegroundColor Green
Write-Host "  output: $dist" -ForegroundColor Green
Write-Host "  note: unsigned, so SmartScreen will warn on first run (More info -> Run anyway)." -ForegroundColor DarkYellow
