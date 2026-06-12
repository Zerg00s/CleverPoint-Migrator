# Requires: Azure CLI logged in (az login), Artifact Signing Client Tools installed
# Install: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
#
# Usage:
#   .\Sign-App.ps1                                  # publish + sign, version auto-incremented
#   .\Sign-App.ps1 -Version 1.0.0                  # publish + sign, explicit version
#   .\Sign-App.ps1 -Release                         # auto-increment + publish + sign + push GitHub release
#   .\Sign-App.ps1 -Version 1.0.0 -SkipBuild        # sign only (already published)
#   .\Sign-App.ps1 -Version 1.0.0 -Release -Draft    # push as draft release
#
# When -Version is omitted, the patch number is bumped from the highest existing
# "vX.Y.Z" git tag (or the csproj <Version> if there are no tags yet).

param(
    [string]$Version,
    [switch]$SkipBuild,
    [switch]$Release,
    [switch]$Draft
)

$ErrorActionPreference = "Stop"

$projectDir   = $PSScriptRoot
$publishDir   = "$projectDir\src\CleverPoint.Migrator.App\bin\Release\net8.0-windows\win-x64\publish"
$outputExe    = "$publishDir\CleverPoint.Migrator.App.exe"
$metadataJson = "$PSScriptRoot\signing-metadata.json"

# Resolve the version. If -Version was not supplied, auto-increment the patch
# number from the highest existing "vMAJOR.MINOR.PATCH" git tag, falling back to
# the csproj <Version> when there are no tags yet.
if (-not $Version) {
    $semverRx = '^v(\d+)\.(\d+)\.(\d+)$'

    $tags = @()
    try {
        $tags = @(git -C $projectDir tag --list 2>$null) | Where-Object { $_ -match $semverRx }
    } catch {
        # git unavailable or not a repo - fall through to the csproj fallback.
    }

    if ($tags.Count -gt 0) {
        $latest = $tags |
            Sort-Object { [version]($_ -replace '^v','') } |
            Select-Object -Last 1
        if ($latest -match $semverRx) {
            $Version = "{0}.{1}.{2}" -f $Matches[1], $Matches[2], ([int]$Matches[3] + 1)
            Write-Host "Auto-incremented version from latest tag '$latest' -> $Version" -ForegroundColor Cyan
        }
    }

    if (-not $Version) {
        $csproj = "$projectDir\src\CleverPoint.Migrator.App\CleverPoint.Migrator.App.csproj"
        $m = [regex]::Match((Get-Content $csproj -Raw), '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
        if (-not $m.Success) { throw "Could not determine a version: no git tags and no <Version> in csproj." }
        $Version = "{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, ([int]$m.Groups[3].Value + 1)
        Write-Host "Auto-incremented version from csproj <Version> -> $Version" -ForegroundColor Cyan
    }
}

$tag          = "v$Version"

# Step 1: Publish self-contained single-file exe
if (-not $SkipBuild) {
    # Stop any running instance launched from this repo, otherwise the build
    # cannot overwrite the locked CleverPoint.Migrator.App.exe (MSB3026).
    $running = Get-Process -Name "SharePoint-Online-Manager" -ErrorAction SilentlyContinue |
               Where-Object { $_.Path -and $_.Path -like "$projectDir\*" }
    foreach ($proc in $running) {
        Write-Host "Stopping running instance (PID $($proc.Id)) locking the output exe..." -ForegroundColor Yellow
        $proc | Stop-Process -Force
    }
    if ($running) { Start-Sleep -Milliseconds 500 }

    Write-Host "Publishing Release v$Version (self-contained, single file)..." -ForegroundColor Cyan
    dotnet publish "$projectDir\src\CleverPoint.Migrator.App" -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
}

# Step 2: Verify exe exists
if (-not (Test-Path $outputExe)) { throw "EXE not found: $outputExe" }

Write-Host "Published exe: $outputExe ($('{0:N1} MB' -f ((Get-Item $outputExe).Length / 1MB)))" -ForegroundColor Gray

# Step 3: Sign with Azure Artifact Signing
Write-Host "Signing $outputExe ..." -ForegroundColor Cyan

# SignTool path (installed with Artifact Signing Client Tools or Windows SDK)
$signToolPaths = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe",
    "${env:ProgramFiles}\Azure Code Signing\signtool.exe"
)
$signTool = $signToolPaths | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
            Sort-Object FullName -Descending | Select-Object -First 1

if (-not $signTool) { throw "SignTool.exe not found. Install Windows SDK or Artifact Signing Client Tools." }

# Dlib path (installed with Artifact Signing Client Tools or NuGet)
$dlibPaths = @(
    "C:\trash\SigningTools\Microsoft.Trusted.Signing.Client.*\bin\x64\Azure.CodeSigning.Dlib.dll",
    "${env:ProgramFiles}\Azure Code Signing\Azure.CodeSigning.Dlib.dll",
    "$PSScriptRoot\packages\Microsoft.ArtifactSigning.Client\*\bin\x64\Azure.CodeSigning.Dlib.dll"
)
$dlib = $dlibPaths | ForEach-Object { Get-Item $_ -ErrorAction SilentlyContinue } |
        Sort-Object FullName -Descending | Select-Object -First 1

if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found. Install: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools" }

& $signTool.FullName sign /v /debug /fd SHA256 `
    /tr "http://timestamp.acs.microsoft.com" /td SHA256 `
    /dlib $dlib.FullName `
    /dmdf $metadataJson `
    $outputExe

if ($LASTEXITCODE -ne 0) { throw "Signing failed" }

# Step 4: Verify signature
Write-Host "Verifying signature..." -ForegroundColor Cyan
& $signTool.FullName verify /pa /v $outputExe

Write-Host "`nSigned exe: $outputExe" -ForegroundColor Green

# Step 5: Push GitHub release (if -Release flag)
if ($Release) {
    Write-Host "`nCreating GitHub release $tag ..." -ForegroundColor Cyan

    # Verify gh CLI is available
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) not found. Install: winget install -e --id GitHub.cli"
    }

    $repoRoot = (Resolve-Path "$projectDir\..").Path
    Push-Location $repoRoot

    try {
        $ghArgs = @("release", "create", $tag, $outputExe, "--title", "SharePoint Online Manager $tag", "--generate-notes")

        if ($Draft) {
            $ghArgs += "--draft"
        }

        & gh @ghArgs
        if ($LASTEXITCODE -ne 0) { throw "GitHub release failed" }

        Write-Host "`nGitHub release $tag created!" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
