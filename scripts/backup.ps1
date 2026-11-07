$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $env:APPDATA 'Elgato\StreamDeck'
$destination = Join-Path $root ('backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $destination | Out-Null
# Copy, never move. Stream Deck stays running; compare profile hashes to detect concurrent edits.
function Get-ProfileHashes {
    @(Get-ChildItem (Join-Path $source 'ProfilesV3') -File -Recurse -Force | ForEach-Object {
        [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($source, $_.FullName); SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    } | Sort-Object Path)
}
$before = Get-ProfileHashes
Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'StreamDeck') -Recurse -Force
& reg.exe export 'HKCU\Software\Elgato Systems GmbH\StreamDeck' (Join-Path $destination 'StreamDeck-HKCU.reg') /y
if ($LASTEXITCODE -ne 0) { throw 'Registry export failed' }
$after = Get-ProfileHashes
if (Compare-Object $before $after -Property Path,SHA256) { throw "Profiles changed during backup. Retry backup. Partial copy: $destination" }
foreach ($entry in $before) {
    $copy = Join-Path (Join-Path $destination 'StreamDeck') $entry.Path
    if ((Get-FileHash $copy -Algorithm SHA256).Hash -ne $entry.SHA256) { throw "Backup verification failed: $($entry.Path)" }
}
$before | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $destination 'profile-hashes.json') -Encoding utf8
[pscustomobject]@{ Created = (Get-Date).ToString('o'); Source = $source; ProfileFiles = $before.Count; Verified = $true; LiveCopy = $true } |
    ConvertTo-Json | Set-Content (Join-Path $destination 'backup-info.json') -Encoding utf8
Write-Output "Verified backup: $destination ($($before.Count) profile files; application data + HKCU settings)"
