$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$archive = Join-Path $root 'dist\YandexMusic.streamDeckProfile'
if (Test-Path $archive) { Write-Output "Keeping existing NEW music profile: $archive"; return }
$id = [guid]::NewGuid().ToString().ToUpperInvariant()
$page = [guid]::NewGuid().ToString().ToLowerInvariant()
$common = [guid]::NewGuid().ToString().ToLowerInvariant()
$staging = Join-Path $root "dist\music-profile-staging\$id.sdProfile"
$pageDir = Join-Path $staging "Profiles\$($page.ToUpperInvariant())"
$commonDir = Join-Path $staging "Profiles\$($common.ToUpperInvariant())"
New-Item -ItemType Directory -Path $pageDir, $commonDir -Force | Out-Null
$version = (Get-Content (Join-Path $root 'plugin\manifest.json') -Raw | ConvertFrom-Json).Version
function New-MusicAction($uuid, $name, $settings) {
    @{
        ActionID = [guid]::NewGuid().ToString(); UUID = $uuid; Name = $name
        Plugin = @{ Name = 'Yandex Music'; UUID = 'local.tux.yamusic'; Version = $version }
        LinkedTitle = $true; Settings = $settings; State = 0
        States = @(@{ ShowTitle = $false }); Resources = $null
    }
}
$keys = @{}
$commands = @('previous', 'play', 'next', 'like', 'repeat', 'shuffle')
$names = @('Предыдущий трек', 'Пауза / пуск', 'Следующий трек', 'Лайк', 'Повтор', 'Перемешивание')
for ($i = 0; $i -lt $commands.Count; $i++) {
    $column = $i % 4; $row = [int][Math]::Floor($i / 4)
    $keys["$column,$row"] = New-MusicAction "local.tux.yamusic.music-$($commands[$i])" $names[$i] @{}
}
$dials = @{}
for ($column = 0; $column -lt 4; $column++) {
    $dials["$column,0"] = New-MusicAction 'local.tux.yamusic.window' 'Окно приложения' @{ mode = 'bottom'; span = $true; dialRole = 'auto' }
}
@{ Device = @{ Model = '20GBD9901'; UUID = '' }; Name = 'Yandex Music'; Pages = @{ Current = $page; Default = $common; Pages = @($page) }; Version = '3.0' } |
    ConvertTo-Json -Depth 15 | Set-Content (Join-Path $staging 'manifest.json') -Encoding utf8NoBOM
@{ Controllers = @(@{ Type = 'Keypad'; Actions = $keys }, @{ Type = 'Encoder'; Actions = $dials }); Icon = ''; Name = 'Yandex Music' } |
    ConvertTo-Json -Depth 15 | Set-Content (Join-Path $pageDir 'manifest.json') -Encoding utf8NoBOM
@{ Controllers = @(@{ Type = 'Keypad'; Actions = $null }, @{ Type = 'Encoder'; Actions = $null }); Icon = ''; Name = '' } |
    ConvertTo-Json -Depth 10 | Set-Content (Join-Path $commonDir 'manifest.json') -Encoding utf8NoBOM
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    $zip.CreateEntry("$id.sdProfile/") | Out-Null
    foreach ($directory in (Get-ChildItem $staging -Recurse -Directory)) {
        $entry = [IO.Path]::GetRelativePath((Split-Path $staging -Parent), $directory.FullName).Replace('\', '/') + '/'
        $zip.CreateEntry($entry) | Out-Null
    }
    foreach ($file in (Get-ChildItem $staging -Recurse -File)) {
        $entry = [IO.Path]::GetRelativePath((Split-Path $staging -Parent), $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry) | Out-Null
    }
} finally { $zip.Dispose() }
Write-Output "Created NEW music profile: $archive (UUID $id)"
