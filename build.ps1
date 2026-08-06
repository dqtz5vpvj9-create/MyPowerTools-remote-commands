[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ToolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $ToolRoot '..\..'))
    if (Test-Path -LiteralPath (Join-Path $candidate 'src\MyPowerTools.Abstractions\MyPowerTools.Abstractions.csproj')) {
        $MyPowerToolsRepoRoot = $candidate
    }
}
if ([string]::IsNullOrWhiteSpace($MyPowerToolsRepoRoot)) {
    throw 'Pass -MyPowerToolsRepoRoot with the MyPowerTools checkout path.'
}
$MyPowerToolsRepoRoot = [System.IO.Path]::GetFullPath($MyPowerToolsRepoRoot)

$surfaceProject = Join-Path $ToolRoot 'current-integration\src\RemoteCommands.Surface\RemoteCommands.Surface.csproj'
$surfaceBuildOut = Join-Path $ToolRoot "current-integration\src\RemoteCommands.Surface\bin\$Configuration\net10.0"
$surfacePackOut = Join-Path $ToolRoot 'artifacts\surface'
$sharedSuite = Join-Path $MyPowerToolsRepoRoot 'tools\remote-notifications\artifacts\package\android-tools-suite'
$ownPackage = Join-Path $ToolRoot 'artifacts\package\android-tools-suite'

$dotnet = Get-Command 'dotnet' -CommandType Application -ErrorAction Stop

if (-not (Test-Path -LiteralPath $surfaceProject -PathType Leaf)) {
    throw "Remote Commands Surface project was not found: $surfaceProject"
}

& $dotnet.Source @(
    'build', $surfaceProject,
    '--configuration', $Configuration,
    '--nologo',
    '--maxcpucount',
    "-p:MyPowerToolsRepoRoot=$MyPowerToolsRepoRoot"
)
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    throw "Remote Commands Surface build failed with exit code $buildExitCode."
}

if (Test-Path -LiteralPath $surfacePackOut) {
    $resolvedPack = [System.IO.Path]::GetFullPath($surfacePackOut)
    $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $ToolRoot 'artifacts'))
    if (-not $resolvedPack.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Surface pack output escaped the tool artifacts root: $resolvedPack"
    }
    Remove-Item -LiteralPath $resolvedPack -Recurse -Force
}
New-Item -ItemType Directory -Path $surfacePackOut -Force | Out-Null
& $dotnet.Source @(
    'pack', $surfaceProject,
    '--configuration', $Configuration,
    '--nologo',
    '--no-build',
    '-o', $surfacePackOut,
    "-p:MyPowerToolsRepoRoot=$MyPowerToolsRepoRoot"
)
$packExitCode = $LASTEXITCODE
if ($packExitCode -ne 0) {
    throw "Remote Commands Surface pack failed with exit code $packExitCode."
}

$expectedSurfaceAssembly = Join-Path $surfaceBuildOut 'RemoteCommands.Surface.dll'
if (-not (Test-Path -LiteralPath $expectedSurfaceAssembly -PathType Leaf)) {
    throw "Remote Commands Surface assembly was not built: $expectedSurfaceAssembly"
}

# Stage into the shared android-tools-suite package when the owner (remote-notifications)
# has already built it. build-all-tools runs the tools in registry order, so a full suite
# build always has the shared package present by the time this script runs.
if (Test-Path -LiteralPath $sharedSuite -PathType Container) {
    $sharedSurfaceTarget = Join-Path $sharedSuite 'modules\remote-commands\ui\surface'
    New-Item -ItemType Directory -Path $sharedSurfaceTarget -Force | Out-Null
    foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
        Get-ChildItem -LiteralPath $surfaceBuildOut -File -Filter $extension |
            Copy-Item -Destination $sharedSurfaceTarget -Force
    }
    Write-Host "Staged Remote Commands Surface into $sharedSurfaceTarget"
} else {
    Write-Warning "Shared android-tools-suite package was not found at $sharedSuite; run remote-notifications build first or build both tools through build-all-tools.ps1."
}

# Maintain a standalone package snapshot under this tool's own artifacts so the
# current-integration modules tree stays independently reviewable.
New-Item -ItemType Directory -Path $ownPackage -Force | Out-Null
$modulesSource = Join-Path $ToolRoot 'current-integration\modules\android-tools-suite'
foreach ($item in Get-ChildItem -LiteralPath $modulesSource -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $ownPackage -Recurse -Force
}
$ownSurfaceTarget = Join-Path $ownPackage 'modules\remote-commands\ui\surface'
New-Item -ItemType Directory -Path $ownSurfaceTarget -Force | Out-Null
foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
    Get-ChildItem -LiteralPath $surfaceBuildOut -File -Filter $extension |
        Copy-Item -Destination $ownSurfaceTarget -Force
}

Write-Output "Remote Commands Surface staged at $surfaceBuildOut"
