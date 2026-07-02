# Requires: Azure CLI logged in (az login), Artifact Signing Client Tools installed
# Install: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
#
# Publishes and signs the Windows build of CleverPoint Migrator
# (CleverPoint.Migrator.Ux.exe). The app loads wwwroot/ from beside the exe, so it
# MUST be published as a self-contained FOLDER, never single-file. This script signs
# the exe plus our own CleverPoint.* binaries in that folder, then (optionally) zips
# the folder and pushes a GitHub release.
#
# Usage:
#   .\Sign-App.ps1                                  # publish + sign, version auto-incremented
#   .\Sign-App.ps1 -Version 1.0.0                  # publish + sign, explicit version
#   .\Sign-App.ps1 -Release                         # auto-increment + publish + sign + push GitHub release (zip)
#   .\Sign-App.ps1 -Version 1.0.0 -SkipBuild        # sign only (already published)
#   .\Sign-App.ps1 -Version 1.0.0 -Release -Draft    # push as draft release
#
# When -Version is omitted, the patch number is bumped from the highest existing
# "vX.Y.Z" git tag (or the csproj <Version> if there are no tags yet, or 1.0.0 if
# neither exists).

param(
    [string]$Version,
    [switch]$SkipBuild,
    [switch]$Release,
    [switch]$Draft
)

$ErrorActionPreference = "Stop"

$projectDir   = $PSScriptRoot
$uxProject    = "$projectDir\src\CleverPoint.Migrator.Ux"
# Publish into the Ux project's (gitignored) publish folder so build output never
# lands in source control.
$publishDir   = "$uxProject\publish\windows"
$outputExe    = "$publishDir\CleverPoint.Migrator.Ux.exe"
$metadataJson = "$PSScriptRoot\signing-metadata.json"

# Resolve the version. If -Version was not supplied, auto-increment the patch
# number from the highest existing "vMAJOR.MINOR.PATCH" git tag, falling back to
# the csproj <Version>, then to 1.0.0 for a first release.
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
        $csproj = "$uxProject\CleverPoint.Migrator.Ux.csproj"
        $m = [regex]::Match((Get-Content $csproj -Raw), '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
        if ($m.Success) {
            $Version = "{0}.{1}.{2}" -f $m.Groups[1].Value, $m.Groups[2].Value, ([int]$m.Groups[3].Value + 1)
            Write-Host "Auto-incremented version from csproj <Version> -> $Version" -ForegroundColor Cyan
        } else {
            $Version = "1.0.0"
            Write-Host "No git tags and no <Version> in csproj; defaulting to $Version" -ForegroundColor Cyan
        }
    }
}

$tag          = "v$Version"

# Step 1: Publish self-contained FOLDER (Photino requires wwwroot beside the exe;
# single-file is intentionally blocked by the csproj).
if (-not $SkipBuild) {
    # Stop any running instance launched from this repo, otherwise the build cannot
    # overwrite the locked exe (MSB3026). The app and its sign-in helper both lock files.
    $procNames = @("CleverPoint.Migrator.Ux", "CleverPoint.Migrator.SignInHelper")
    $running = Get-Process -Name $procNames -ErrorAction SilentlyContinue |
               Where-Object { $_.Path -and $_.Path -like "$projectDir\*" }
    foreach ($proc in $running) {
        Write-Host "Stopping running instance (PID $($proc.Id)) locking the output..." -ForegroundColor Yellow
        $proc | Stop-Process -Force
    }
    if ($running) { Start-Sleep -Milliseconds 500 }

    Write-Host "Publishing Release v$Version (self-contained folder, win-x64)..." -ForegroundColor Cyan
    dotnet publish $uxProject -c Release -r win-x64 --self-contained true `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

    # Browser sign-in launches a separate WebView2 helper as its own process, so the
    # helper exe MUST ship next to the app under SignInHelper\ (BrowserSignIn probes
    # AppContext.BaseDirectory\SignInHelper\...). The MSBuild bundle target only copies
    # it into the dev build output, never the publish folder, so publish it explicitly
    # here. Self-contained so it runs even without .NET installed on the user's machine.
    Write-Host "Publishing the browser sign-in helper into SignInHelper\ ..." -ForegroundColor Cyan
    dotnet publish "$projectDir\src\CleverPoint.Migrator.SignInHelper" -c Release -r win-x64 --self-contained true `
        -o "$publishDir\SignInHelper"
    if ($LASTEXITCODE -ne 0) { throw "Sign-in helper publish failed" }
    if (-not (Test-Path "$publishDir\SignInHelper\CleverPoint.Migrator.SignInHelper.exe")) {
        throw "Sign-in helper exe missing after publish (browser auth would fail)"
    }
}

# Step 2: Verify exe exists
if (-not (Test-Path $outputExe)) { throw "EXE not found: $outputExe" }

Write-Host "Published exe: $outputExe ($('{0:N1} MB' -f ((Get-Item $outputExe).Length / 1MB)))" -ForegroundColor Gray

# Step 3: Sign with Azure Artifact Signing
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

# Sign the exe plus all of our own binaries shipped in the folder (the main exe, the
# Core/UX/SignInHelper assemblies). Third-party DLLs are already signed by their authors.
$toSign = Get-ChildItem -Path $publishDir -Recurse -File -Include 'CleverPoint.Migrator.*.exe','CleverPoint.Migrator.*.dll' |
          Select-Object -ExpandProperty FullName
if (-not $toSign) { throw "No CleverPoint.* binaries found to sign in $publishDir" }

Write-Host "Signing $($toSign.Count) file(s) in $publishDir ..." -ForegroundColor Cyan
& $signTool.FullName sign /v /debug /fd SHA256 `
    /tr "http://timestamp.acs.microsoft.com" /td SHA256 `
    /dlib $dlib.FullName `
    /dmdf $metadataJson `
    @toSign

if ($LASTEXITCODE -ne 0) { throw "Signing failed" }

# Step 4: Verify the main exe signature
Write-Host "Verifying signature..." -ForegroundColor Cyan
& $signTool.FullName verify /pa /v $outputExe

Write-Host "`nSigned exe: $outputExe" -ForegroundColor Green

# Step 5: Push GitHub release (if -Release flag). The app ships as a folder, so the
# release asset is a zip of the whole publish folder.
if ($Release) {
    Write-Host "`nPackaging release zip..." -ForegroundColor Cyan
    $zipPath = "$uxProject\publish\CleverPoint.Migrator-win-x64-$Version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath
    Write-Host "Zip: $zipPath ($('{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)))" -ForegroundColor Gray

    Write-Host "Creating GitHub release $tag ..." -ForegroundColor Cyan

    # Verify gh CLI is available
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) not found. Install: winget install -e --id GitHub.cli"
    }

    # The git repository is this folder (not its parent).
    Push-Location $projectDir
    try {
        $ghArgs = @("release", "create", $tag, $zipPath, "--title", "CleverPoint Migrator $tag", "--generate-notes")

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
