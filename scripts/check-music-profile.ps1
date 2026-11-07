$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'dist\YandexMusic.streamDeckProfile'))
function Read-JsonEntry($entry) {
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}
try {
    $entry = @($zip.Entries | Where-Object FullName -match '^[^/]+/manifest.json$')
    if ($entry.Count -ne 1) { throw 'Expected one independent profile' }
    $manifest = Read-JsonEntry $entry[0]
    if ($manifest.Name -ne 'Yandex Music' -or $manifest.Version -ne '3.0') { throw 'Unexpected profile metadata' }
    if ($manifest.Pages.Current -eq $manifest.Pages.Default) { throw 'Default page must be separate' }
    $pageId = $manifest.Pages.Current.ToUpperInvariant()
    $page = Read-JsonEntry ($zip.Entries | Where-Object FullName -like "*/Profiles/$pageId/manifest.json")
    $keys = @(($page.Controllers | Where-Object Type -eq 'Keypad').Actions.PSObject.Properties)
    $dials = @(($page.Controllers | Where-Object Type -eq 'Encoder').Actions.PSObject.Properties)
    if ($keys.Count -ne 6 -or $dials.Count -ne 4) { throw 'Expected six music keys and four preview sections' }
    $expected = @('previous', 'play', 'next', 'like', 'repeat', 'shuffle') | ForEach-Object { "local.tux.appmixer.music-$_" }
    if (Compare-Object ($keys.Value.UUID | Sort-Object) ($expected | Sort-Object)) { throw 'Unexpected music commands' }
    if (@($dials.Value | Where-Object UUID -ne 'local.tux.appmixer.window').Count) { throw 'Unexpected encoder action' }
    if (@(($keys.Value + $dials.Value).ActionID | Select-Object -Unique).Count -ne 10) { throw 'Action IDs are not unique' }
    Write-Output 'PASS: independent Yandex Music profile, six controls, four preview sections, ten unique actions.'
} finally { $zip.Dispose() }
