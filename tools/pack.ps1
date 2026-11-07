# Baut NX-MIX (UI + eigenständiger NxMix.Daemon) self-contained für win-x64 und schnürt
# kanalgerechte Velopack-Artefakte für GitHub Releases (Stable oder Beta).
#
# Beispiele:
#   powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 2.0.0 -Channel stable
#   powershell -ExecutionPolicy Bypass -File tools/pack.ps1 -Version 2.1.0-beta.1 -Channel beta
#
# Benötigt Velopack CLI passend zum NuGet-Paket:
#   dotnet tool install -g vpk --version 1.2.161

param(
    [Parameter(Mandatory)]
    [string]$Version,

    [ValidateSet('auto', 'stable', 'beta')]
    [string]$Channel = 'auto',

    [string]$OutputDir = 'Releases'
)

$ErrorActionPreference = 'Stop'
$cleanVersion = $Version.Trim().TrimStart('v', 'V')

$stablePattern = '^\d+\.\d+\.\d+$'
$betaPattern   = '^\d+\.\d+\.\d+-(beta|rc|alpha)(\.\d+)?$'

$resolvedChannel = $Channel.ToLowerInvariant()
if ($resolvedChannel -eq 'auto') {
    if ($cleanVersion -match $stablePattern) {
        $resolvedChannel = 'stable'
    }
    elseif ($cleanVersion -match $betaPattern) {
        $resolvedChannel = 'beta'
    }
    else {
        throw "Ungültiges Versionsformat '$cleanVersion'. Erwartet: 'X.Y.Z' (Stable) oder 'X.Y.Z-beta.N' (Beta)."
    }
}

if ($resolvedChannel -eq 'stable') {
    if ($cleanVersion -notmatch $stablePattern) {
        throw "SICHERHEITSSPERRE: Version '$cleanVersion' ist keine reine Stable-Version (X.Y.Z). Vorabversionen dürfen niemals im Stable-Kanal veröffentlicht werden!"
    }
    $vpkChannel   = 'win'
    $packTitle    = 'NX-MIX'
    $setupExeName = 'NX-MIX-Setup.exe'
    $portableName = 'NX-MIX-Portable.zip'
}
else {
    if ($cleanVersion -notmatch $betaPattern) {
        throw "SICHERHEITSSPERRE: Beta-Kanal erfordert ein Prerelease-Suffix wie '2.1.0-beta.1' (erhalten: '$cleanVersion')."
    }
    $vpkChannel   = 'win-beta'
    $packTitle    = 'NX-MIX (Beta)'
    $setupExeName = 'NX-MIX-Beta-Setup.exe'
    $portableName = 'NX-MIX-Beta-Portable.zip'
}

$root    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publish = Join-Path $root "artifacts\publish-$resolvedChannel"
$outPath = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $root $OutputDir }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

Write-Host "==> [1/4] Publish NxMix.App ($cleanVersion, Kanal=$resolvedChannel/$vpkChannel) ..."
dotnet publish (Join-Path $root 'src\NxMix.App') -c Release -r win-x64 --self-contained -p:Version=$cleanVersion -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (NxMix.App) fehlgeschlagen' }

# Wichtig: NxMix.Daemon NACH NxMix.App publizieren, damit die self-contained runtimeconfig/deps von NxMix.Daemon erhalten bleiben.
Write-Host "==> [2/4] Publish NxMix.Daemon ($cleanVersion, self-contained) ..."
dotnet publish (Join-Path $root 'src\NxMix.Daemon') -c Release -r win-x64 --self-contained -p:Version=$cleanVersion -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (NxMix.Daemon) fehlgeschlagen' }

$appExe    = Join-Path $publish 'NX-MIX.exe'
$daemonExe = Join-Path $publish 'NxMix.Daemon.exe'
$daemonCfg = Join-Path $publish 'NxMix.Daemon.runtimeconfig.json'

if (-not (Test-Path $appExe))    { throw "NX-MIX.exe fehlt im Publish-Verzeichnis: $publish" }
if (-not (Test-Path $daemonExe)) { throw "NxMix.Daemon.exe fehlt im Publish-Verzeichnis: $publish" }
$rtJson = Get-Content -Raw -Path $daemonCfg
if ($rtJson -notmatch '"includedFrameworks"') {
    throw "NxMix.Daemon.runtimeconfig.json ist nicht self-contained!"
}

# Release Notes ermitteln (docs/release-notes/<version>.md oder strukturierter Fallback)
$notesFile = Join-Path $root "docs\release-notes\$cleanVersion.md"
if (-not (Test-Path $notesFile)) {
    $notesDir = Join-Path $root 'artifacts'
    New-Item -ItemType Directory -Force -Path $notesDir | Out-Null
    $notesFile = Join-Path $notesDir "release-notes-$cleanVersion.md"
    if ($resolvedChannel -eq 'beta') {
        $fallbackNotes = @"
## NX-MIX $cleanVersion (Beta-Vorabversion)

- **Kanal:** Beta (`win-beta` · GitHub Prerelease)
- **Installer:** `$setupExeName`
- Vorabversion mit neuen Funktionen und frühzeitigen Verbesserungen zum Testen.
- Ein Wechsel zurück auf den offiziellen **Stable**-Kanal ist jederzeit direkt in NX-MIX unter **Engine -> Updates** möglich (inkl. automatischer Konfigurationssicherung).
"@
    }
    else {
        $fallbackNotes = @"
## NX-MIX $cleanVersion (Stable Release)

- **Kanal:** Stable (`win` · Offizielles Release für den Live-Betrieb)
- **Installer:** `$setupExeName`
- Geprüfte Version für den dauerhaften Studio-, Streaming- und Live-Betrieb.
"@
    }
    Set-Content -Path $notesFile -Value $fallbackNotes -Encoding UTF8
}

Write-Host "==> [3/4] Velopack Pack ($vpkChannel -> $setupExeName) ..."
vpk --skip-updates pack `
    --channel $vpkChannel `
    --releaseNotes $notesFile `
    --runtime 'win-x64' `
    --packId 'NxMix' `
    --packVersion $cleanVersion `
    --packDir $publish `
    --mainExe 'NX-MIX.exe' `
    --packTitle $packTitle `
    --packAuthors 'nexoGroups' `
    --icon (Join-Path $root 'src\NxMix.App\Assets\app.ico') `
    --outputDir $outPath
if ($LASTEXITCODE -ne 0) { throw 'vpk pack fehlgeschlagen' }

# Kanalgerechte Dateinamen herstellen (NX-MIX-Setup.exe bzw. NX-MIX-Beta-Setup.exe) und Velopack-Asset-Manifest synchronisieren
Write-Host "==> [4/4] Installer-Artefakte benennen & prüfen ..."
$defaultSetup    = Join-Path $outPath "NxMix-$vpkChannel-Setup.exe"
$targetSetup     = Join-Path $outPath $setupExeName
$defaultPortable = Join-Path $outPath "NxMix-$vpkChannel-Portable.zip"
$targetPortable  = Join-Path $outPath $portableName

if (Test-Path $defaultSetup) {
    if (Test-Path $targetSetup) { Remove-Item -Force $targetSetup }
    Move-Item -Force -Path $defaultSetup -Destination $targetSetup
}
if (Test-Path $defaultPortable) {
    if (Test-Path $targetPortable) { Remove-Item -Force $targetPortable }
    Move-Item -Force -Path $defaultPortable -Destination $targetPortable
}

$assetsManifest = Join-Path $outPath "assets.$vpkChannel.json"
if (Test-Path $assetsManifest) {
    $assets = Get-Content -Raw -Path $assetsManifest -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $assets) {
        if ($entry.RelativeFileName -eq "NxMix-$vpkChannel-Setup.exe") {
            $entry.RelativeFileName = $setupExeName
        }
        elseif ($entry.RelativeFileName -eq "NxMix-$vpkChannel-Portable.zip") {
            $entry.RelativeFileName = $portableName
        }
    }
    $updatedJson = ConvertTo-Json -InputObject @($assets) -Compress
    [System.IO.File]::WriteAllText($assetsManifest, $updatedJson, (New-Object System.Text.UTF8Encoding($false)))
}

$releasesFeed = Join-Path $outPath "releases.$vpkChannel.json"
if (-not (Test-Path $targetSetup))  { throw "Erwarteter Installer wurde nicht erzeugt: $targetSetup" }
if (-not (Test-Path $releasesFeed)) { throw "Velopack-Feed fehlt: $releasesFeed" }

$fullNupkg = Get-ChildItem -Path $outPath -Filter "NxMix-$cleanVersion*-full.nupkg" | Select-Object -First 1
if (-not $fullNupkg) { throw "Velopack Full-Nupkg für Version $cleanVersion fehlt in $outPath" }

Write-Host "ERFOLG: Kanal=$resolvedChannel ($vpkChannel) | Version=$cleanVersion | Setup=$setupExeName | Nupkg=$($fullNupkg.Name)"
