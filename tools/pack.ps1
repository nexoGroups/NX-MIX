# Publishes the app self-contained and packs it with Velopack into Releases/ (Setup.exe, full package, update feed).
# The release workflow calls this too, so a local run produces exactly what a release contains.
#   powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 0.1.0
# Needs the Velopack CLI matching the NuGet package: dotnet tool install -g vpk --version 1.2.161
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$OutputDir = 'Releases'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'artifacts\publish'

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

dotnet publish (Join-Path $root 'src\NxMix.App') -c Release -r win-x64 --self-contained -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Shown on the release page (and carried in the update feed) when docs/release-notes/<version>.md exists.
$notes = Join-Path $root "docs\release-notes\$Version.md"
$notesArgs = if (Test-Path $notes) { @('--releaseNotes', $notes) } else { @() }

vpk pack @notesArgs `
    --packId NxMix `
    --packVersion $Version `
    --packDir $publish `
    --mainExe 'NX-MIX.exe' `
    --packTitle 'NX-MIX' `
    --packAuthors 'nexoGroups' `
    --icon (Join-Path $root 'src\NxMix.App\Assets\app.ico') `
    --outputDir (Join-Path $root $OutputDir)
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
