<#
.SYNOPSIS
Stages the Android Remote Commands module package.

.DESCRIPTION
Builds the module adapter (RemoteCommands.Android) and, when it exists, the phone surface
(MyPowerTools.MobileRemoteCommands), copies the declarative package documents from package/, and
verifies every file listed in manifest/android-package-manifest.json exists before it swaps the
result into artifacts/package/remote-commands-android.

The staged package is what the Android app embeds as APK assets and what the desktop-side module
catalog mirrors into modules/remote-commands-android; a failed build therefore never leaves a
half-populated module behind for the app or the release scripts to pick up.

Requirements: the SDK pinned by global.json. SSH.NET is restored from nuget.org (or from a
configured mirror); pass -NoRestore when the packages are already restored locally, e.g. offline
verification builds.

.EXAMPLE
pwsh tools/remote-commands/android-integration/build.ps1

.EXAMPLE
# Offline backend-only verification (the phone surface is a separate deliverable).
pwsh tools/remote-commands/android-integration/build.ps1 -SkipSurface -NoMirror -NoRestore -Configuration Debug
#>
[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot = '',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Skip mirroring the metadata documents into <repo>/modules/remote-commands-android.
    [switch]$NoMirror,

    [switch]$NoRestore,

    # Backend-only verification: build and stage the module plus the metadata, and skip the phone
    # surface (which lives in its own deliverable). The resulting package is explicitly incomplete
    # and the script says so; a strict run still requires the surface assembly.
    [switch]$SkipSurface
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$integrationRoot = $PSScriptRoot
$repo = if ($MyPowerToolsRepoRoot) {
    [IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
} else {
    # android-integration -> remote-commands -> tools -> repository root
    [IO.Path]::GetFullPath((Join-Path $integrationRoot '../../..'))
}

$moduleProject = Join-Path $integrationRoot 'src/RemoteCommands.Android/RemoteCommands.Android.csproj'
$surfaceProject = Join-Path $repo 'src/MyPowerTools.MobileRemoteCommands/MyPowerTools.MobileRemoteCommands.csproj'
$template = Join-Path $integrationRoot 'package'
$manifestPath = Join-Path $integrationRoot 'manifest/android-package-manifest.json'
$packageRoot = Join-Path $integrationRoot 'artifacts/package/remote-commands-android'
$staging = Join-Path $integrationRoot 'artifacts/package.staging'
$surfaceStage = Join-Path $staging 'ui/surface'
$moduleMirror = Join-Path $repo 'modules/remote-commands-android'

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $HOME '.dotnet/dotnet' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'The dotnet host was not found. Add it to PATH, or set DOTNET_ROOT.'
}

if (-not (Test-Path -LiteralPath $moduleProject -PathType Leaf)) {
    throw "Project was not found: $moduleProject"
}

$surfaceAvailable = -not $SkipSurface -and (Test-Path -LiteralPath $surfaceProject -PathType Leaf)
if (-not $SkipSurface -and -not $surfaceAvailable) {
    throw "Project was not found: $surfaceProject. The phone surface (MyPowerTools.MobileRemoteCommands) is a separate deliverable; pass -SkipSurface only for a backend-only verification package."
}

if (-not $surfaceAvailable) {
    Write-Warning 'Staging without the phone surface; the resulting package is INCOMPLETE and must not be shipped.'
}

# Only the disposable staging directory is cleared up front; the published package is replaced after
# a successful build, so a failed build never destroys a package a running dev install still uses.
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $surfaceStage -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $template) {
    Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
}

# Explicitly typed: PowerShell unrolls a single-element array literal into a string, which would then
# be splatted character by character.
[string[]]$restoreArgument = @()
if ($NoRestore) { $restoreArgument = @('--no-restore') }

# CopyLocalLockFileAssemblies is what makes a class library stage its NuGet closure (Renci.SshNet,
# BouncyCastle.Cryptography, Microsoft.Extensions.*). Without it the stage would contain only the
# adapter and the manifest check below would fail on the missing SSH client.
& $dotnet build $moduleProject -c $Configuration --nologo -o $staging @restoreArgument "-p:MyPowerToolsRepoRoot=$repo" -p:CopyLocalLockFileAssemblies=true
if ($LASTEXITCODE -ne 0) { throw 'RemoteCommands.Android build failed.' }

if ($surfaceAvailable) {
    & $dotnet build $surfaceProject -c $Configuration --nologo -o $surfaceStage @restoreArgument "-p:MyPowerToolsRepoRoot=$repo"
    if ($LASTEXITCODE -ne 0) { throw 'MyPowerTools.MobileRemoteCommands build failed.' }
}

# The in-proc module host resolves dependencies from the package directory, not from a deps.json.
Get-ChildItem -LiteralPath $staging -Filter '*.deps.json' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

# The host resolves Abstractions / Platform.Abstractions / AvaloniaSdk from its own load context, and
# the Android app compiles the shared contracts from source. Shipping a second copy next to the
# module would load a duplicate contract assembly, so they are removed from the stage.
#
# Deliberately NON-recursive: the tool surface ui/surface/MyPowerTools.MobileRemoteCommands.dll also
# starts with "MyPowerTools." and must stay in the package.
$hostProvided = @(
    'MyPowerTools.Abstractions.dll',
    'MyPowerTools.Platform.Abstractions.dll',
    'MyPowerTools.AvaloniaSdk.dll',
    'Avalonia*.dll'
)
foreach ($pattern in $hostProvided) {
    Get-ChildItem -LiteralPath $staging -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $surfaceStage -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Debug symbols are not part of the shipped package.
Get-ChildItem -LiteralPath $staging -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.pdb' } |
    Remove-Item -Force

# The surface directory ships exactly one file: the factory assembly named by ui/tool.json. Anything
# the surface build copied next to it (Avalonia, SDK contracts, symbols) is dropped.
Get-ChildItem -LiteralPath $surfaceStage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'MyPowerTools.MobileRemoteCommands.dll' } |
    Remove-Item -Force

# Fail before publishing a catalog that points at a missing factory or a missing SSH client.
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$missing = @()
foreach ($entry in $manifest.files) {
    $candidate = Join-Path $staging ($entry.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        continue
    }

    if (-not $entry.required) {
        Write-Warning "Optional package file is absent: $($entry.path)"
        continue
    }

    if (-not $surfaceAvailable -and $entry.path -like 'ui/surface/*') {
        Write-Warning "Skipped surface assembly (backend-only staging): $($entry.path)"
        continue
    }

    $missing += $entry.path
}

if ($missing.Count -gt 0) {
    throw ("The Remote Commands Android package is incomplete; missing: " + ($missing -join ', '))
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path -Parent $packageRoot) -Force | Out-Null
Move-Item -LiteralPath $staging -Destination $packageRoot

if (-not $NoMirror) {
    # Desktop-side catalog mirror: the same shape tools/file-transfer uses, so the module can be
    # discovered by the normal modules/** scan without pointing the scanner at tool artifacts.
    if (Test-Path -LiteralPath $moduleMirror) {
        Remove-Item -LiteralPath $moduleMirror -Recurse -Force
    }
    New-Item -ItemType Directory -Path $moduleMirror -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $packageRoot) {
        Copy-Item -LiteralPath $item.FullName -Destination $moduleMirror -Recurse -Force
    }
}

Write-Output "Remote Commands (Android) staged at $packageRoot"
