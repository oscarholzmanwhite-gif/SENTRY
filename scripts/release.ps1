<#
.SYNOPSIS
    Bump the SENTRY version everywhere, build Release, and package SENTRY-vX.X.X.zip.

.EXAMPLE
    pwsh scripts/release.ps1 -Version 2.0.1
    pwsh scripts/release.ps1 -Version 2.0.1 -SkipBuild   # re-zip only, reuse the DLL already in the repo

What it does, in order (mirrors the release checklist in CLAUDE.md):
  1. Writes the version into the four places it lives:
       SENTRY.version, GameData/SENTRY/SENTRY.version, the live (fresh) install's
       GameData/SENTRY/SENTRY.version, and src/AssemblyInfo.cs.
  2. dotnet build -c Release  (the csproj writes SENTRY.dll/.pdb into the fresh KSP install).
  3. Copies SENTRY.dll + SENTRY.pdb from the fresh install into the repo's GameData/SENTRY/Plugins/.
  4. Zips the repo's GameData/ folder to dist/SENTRY-vX.X.X.zip, with GameData/SENTRY/... inside,
     leaving out the per-install prefs PluginData/advanced.cfg and PluginData/ui.cfg, and adding
     the repo-root README.md as GameData/SENTRY/README.md, next to LICENSE.

It does NOT commit, tag, push, or publish anything.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo     = Split-Path -Parent $PSScriptRoot
$liveRoot = Join-Path (Split-Path -Parent $repo) 'Kerbal Space Program_fresh'
$major, $minor, $patch = $Version.Split('.') | ForEach-Object { [int]$_ }

# --- 1. Version bump ---------------------------------------------------------------------------

# KSP-AVC .version files: only the VERSION line changes (KSP_VERSION must stay untouched).
$versionFiles = @(
    (Join-Path $repo 'SENTRY.version'),
    (Join-Path $repo 'GameData\SENTRY\SENTRY.version'),
    (Join-Path $liveRoot 'GameData\SENTRY\SENTRY.version')
)
$newVersionLine = "`"VERSION`": { `"MAJOR`": $major, `"MINOR`": $minor, `"PATCH`": $patch }"
$versionRegex   = '"VERSION":\s*\{[^}]*\}'

foreach ($file in $versionFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        Write-Warning "Skipping missing file: $file"
        continue
    }
    $text = [IO.File]::ReadAllText($file)
    if ($text -notmatch $versionRegex) { throw "No VERSION line found in $file" }
    [IO.File]::WriteAllText($file, [regex]::Replace($text, $versionRegex, $newVersionLine))
    Write-Host "Version -> $Version : $file"
}

$asmInfo = Join-Path $repo 'src\AssemblyInfo.cs'
$text = [IO.File]::ReadAllText($asmInfo)
$text = $text -replace 'AssemblyVersion\("[\d.]+"\)',     "AssemblyVersion(`"$Version.0`")"
$text = $text -replace 'AssemblyFileVersion\("[\d.]+"\)', "AssemblyFileVersion(`"$Version.0`")"
$text = $text -replace 'KSPAssembly\("SENTRY",\s*\d+,\s*\d+,\s*\d+\)', "KSPAssembly(`"SENTRY`", $major, $minor, $patch)"
[IO.File]::WriteAllText($asmInfo, $text)
Write-Host "Version -> $Version : $asmInfo"

# --- 2 & 3. Build and copy the DLL into the repo -----------------------------------------------

$builtDir = Join-Path $liveRoot 'GameData\SENTRY\Plugins'
$repoPlugins = Join-Path $repo 'GameData\SENTRY\Plugins'

if (-not $SkipBuild) {
    Push-Location $repo
    try {
        dotnet build -c Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }
    } finally { Pop-Location }

    New-Item -ItemType Directory -Force $repoPlugins | Out-Null
    foreach ($name in 'SENTRY.dll', 'SENTRY.pdb') {
        Copy-Item -LiteralPath (Join-Path $builtDir $name) -Destination $repoPlugins -Force
    }
    Write-Host "Copied SENTRY.dll/.pdb -> $repoPlugins"
}

# --- 4. Zip ------------------------------------------------------------------------------------

$dist    = Join-Path $repo 'dist'
$zipPath = Join-Path $dist "SENTRY-v$Version.zip"
New-Item -ItemType Directory -Force $dist | Out-Null
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath }

# Per-install prefs players edit locally; shipping ours would overwrite theirs on upgrade.
$excluded = @('GameData/SENTRY/PluginData/advanced.cfg', 'GameData/SENTRY/PluginData/ui.cfg')

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$gameData = Join-Path $repo 'GameData'
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    Get-ChildItem -LiteralPath $gameData -Recurse -File | ForEach-Object {
        # Entry names must use forward slashes and start with "GameData/".
        $entry = 'GameData/' + $_.FullName.Substring($gameData.Length + 1).Replace('\', '/')
        if ($excluded -contains $entry) { return }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, 'Optimal')
    }

    # The README lives at the repo root, not under GameData/, so add it from there. It sits beside
    # LICENSE so the README's relative [LICENSE](LICENSE) link resolves.
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, (Join-Path $repo 'README.md'), 'GameData/SENTRY/README.md', 'Optimal')
} finally { $zip.Dispose() }

Write-Host ""
Write-Host "Created $zipPath"
$check = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $check.Entries | ForEach-Object { '  ' + $_.FullName }
} finally { $check.Dispose() }
