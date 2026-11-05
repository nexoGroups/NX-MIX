# Baut NX-MIX lokal nach .\app (framework-abhaengig, braucht die .NET 10 Desktop Runtime) und testet den Start.
# Nur fuer die Entwicklung. Smart App Control blockiert gelegentlich frisch gebaute, unsignierte Programme:
# dann wird neu gebaut (-p:Deterministic=false ergibt jedes Mal eine andere Datei).
# Mit -Shortcut wird eine Desktop-Verknuepfung "NX-MIX.lnk" angelegt.
param([switch]$Shortcut)

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out  = Join-Path $root 'app'
$exe  = Join-Path $out 'NX-MIX.exe'
$scratch = Join-Path ([IO.Path]::GetTempPath()) 'nxmix-devbuild-data'

$running = Get-Process 'NX-MIX' -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }

$ok = $false
for ($attempt = 1; $attempt -le 10 -and -not $ok; $attempt++) {
    $det = if ($attempt -eq 1) { 'true' } else { 'false' }
    dotnet publish "$root\src\NxMix.App" -c Release -r win-x64 --self-contained false -p:DebugType=none -p:DebugSymbols=false "-p:Deterministic=$det" -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Host 'Build fehlgeschlagen.'; exit 1 }
    # Start-Test mit eigenem Datenordner (beruehrt dein echtes Projekt nicht), beendet sich selbst
    try { $p = Start-Process $exe -ArgumentList '--data-dir', "`"$scratch`"", '--exit-after', '1500' -Wait -PassThru -ErrorAction Stop; $ok = ($p.ExitCode -eq 0) } catch { $ok = $false }
    if (-not $ok) { Write-Host "Versuch ${attempt}: Windows hat die neue Exe blockiert oder sie ist abgestuerzt - baue erneut ..."; Start-Sleep -Seconds 1 }
}
if (-not $ok) {
    Write-Host ('Die Exe wurde von Windows blockiert. Ausweg: dotnet "' + (Join-Path $out 'NX-MIX.dll') + '"')
    exit 1
}
Write-Host "Fertig: $exe"

if ($Shortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NX-MIX.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $l = $sh.CreateShortcut($lnk)
    $l.TargetPath = $exe
    $l.WorkingDirectory = $out
    $l.IconLocation = "$exe,0"
    $l.Save()
    Write-Host "Verknuepfung: $lnk"
}
