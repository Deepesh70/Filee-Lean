<#
.SYNOPSIS
  Builds the production release packages for Filee:
  1. Filee-Setup.exe   (1-click zero-admin Windows installer with Explorer context menu)
  2. Filee-win-x64.zip (Self-contained portable package with install.cmd / uninstall.cmd)
  3. SHA256SUMS.txt    (Cryptographic integrity checksums)

.EXAMPLE
  pwsh build/build-release.ps1
  pwsh build/build-release.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\Releases'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
    $Version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '2.4.0' }
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host " Building Filee v$Version Release Packages" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 1. Run full test suite if not skipped
if (-not $SkipTests) {
    Write-Host "`n[1/5] Running test suite (Filee.slnx)..." -ForegroundColor Yellow
    & dotnet test Filee.slnx -c Release --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "Test suite failed with exit code $LASTEXITCODE" }
    Write-Host "All tests passed successfully." -ForegroundColor Green
} else {
    Write-Host "`n[1/5] Skipping tests (-SkipTests specified)." -ForegroundColor DarkGray
}

# 2. Stage self-contained publish directory
$staging = Join-Path $root 'build\.staging\win-x64'
Write-Host "`n[2/5] Publishing self-contained .NET 10 win-x64 payload..." -ForegroundColor Yellow
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

& dotnet publish src/Filee.Cli/Filee.Cli.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version=$Version -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# Strip symbol files and XML doc comments
Get-ChildItem -Path $staging -Include *.pdb, *.xml -Recurse | Remove-Item -Force

# 3. Include native shell components and companion launcher
Write-Host "`n[3/5] Bundling native shell components and helper scripts..." -ForegroundColor Yellow

$prebuiltDir = Join-Path $root 'publish\win-x64'
$dllSrc = Join-Path $prebuiltDir 'FileeExplorerMenu.dll'
$runSrc = Join-Path $prebuiltDir 'filee-run.exe'

if (Test-Path $dllSrc) {
    Copy-Item $dllSrc (Join-Path $staging 'FileeExplorerMenu.dll') -Force
} else {
    Write-Warning "FileeExplorerMenu.dll not found in $prebuiltDir. Compiling via build-explorer-menu.ps1..."
    & pwsh (Join-Path $PSScriptRoot 'build-explorer-menu.ps1') -Destination $staging -Version $Version
}

if (Test-Path $runSrc) {
    Copy-Item $runSrc (Join-Path $staging 'filee-run.exe') -Force
} else {
    Write-Warning "filee-run.exe not found in $prebuiltDir; using filee-cli.exe as runner."
    Copy-Item (Join-Path $staging 'filee-cli.exe') (Join-Path $staging 'filee-run.exe') -Force
}

# Copy alias Filee.exe
Copy-Item (Join-Path $staging 'filee-cli.exe') (Join-Path $staging 'Filee.exe') -Force

# Pack Windows 11 sparse package FileeExplorerMenu.msix
Write-Host "Packing Windows 11 Sparse MSIX package..." -ForegroundColor Gray
& dotnet run (Join-Path $root 'build/tools/make-explorer-package.cs') -- $staging x64
if ($LASTEXITCODE -ne 0) { throw "make-explorer-package failed with exit code $LASTEXITCODE" }

# Copy portable helper scripts
Copy-Item (Join-Path $root 'build\templates\portable\install.cmd') (Join-Path $staging 'install.cmd') -Force
Copy-Item (Join-Path $root 'build\templates\portable\uninstall.cmd') (Join-Path $staging 'uninstall.cmd') -Force

# 4. Package Releases
Write-Host "`n[4/5] Packaging release artifacts into $OutputDir..." -ForegroundColor Yellow
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }

$portableZip = Join-Path $OutputDir "Filee-win-x64.zip"
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }

Write-Host "Creating portable archive: Filee-win-x64.zip" -ForegroundColor Gray
$stagingWildcard = Join-Path $staging '*'
Compress-Archive -Path $stagingWildcard -DestinationPath $portableZip -CompressionLevel Optimal

# 5. Compile 1-Click Setup Executable (Filee-Setup.exe)
Write-Host "`n[5/5] Compiling 1-click Windows installer (Filee-Setup.exe)..." -ForegroundColor Yellow

$setupDir = Join-Path $root 'src\Filee.Setup'
$payloadTemp = Join-Path $setupDir 'payload.zip'
Copy-Item $portableZip $payloadTemp -Force

$setupRes = Join-Path $root 'build\.staging\setup.res'
$setupExe = Join-Path $OutputDir "Filee-Setup.exe"

try {
    # Compile resource with windres
    Push-Location $setupDir
    & windres -i setup.rc -o $setupRes -O coff
    if ($LASTEXITCODE -ne 0) { throw "windres failed to compile setup.rc" }

    # Link with gcc
    & gcc -O2 -s -mwindows setup.c $setupRes -o $setupExe -lole32 -lshell32
    if ($LASTEXITCODE -ne 0) { throw "gcc failed to compile Filee-Setup.exe" }
}
finally {
    Pop-Location
    if (Test-Path $payloadTemp) { Remove-Item $payloadTemp -Force }
}

# 6. Generate SHA256 Checksums
Write-Host "`nComputing SHA-256 integrity checksums..." -ForegroundColor Gray
$checksumsFile = Join-Path $OutputDir 'SHA256SUMS.txt'
$filesToHash = @($setupExe, $portableZip)
$checksumLines = @()

foreach ($file in $filesToHash) {
    if (Test-Path $file) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $leaf = Split-Path $file -Leaf
        $checksumLines += "$hash  $leaf"
    }
}
Set-Content -Path $checksumsFile -Value $checksumLines -Encoding ascii

Write-Host "`n==================================================" -ForegroundColor Green
Write-Host " Release Build Complete! Output Artifacts:" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
foreach ($file in $filesToHash) {
    $info = Get-Item $file
    $sizeMb = [math]::Round($info.Length / 1MB, 2)
    Write-Host ("  {0,-20} : {1,6} MB" -f $info.Name, $sizeMb) -ForegroundColor Cyan
}
Write-Host ("  {0,-20} : SHA-256 manifest" -f (Split-Path $checksumsFile -Leaf)) -ForegroundColor Cyan
Write-Host ""
