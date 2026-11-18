$ErrorActionPreference = 'Stop'
$env:PSModulePath = (Join-Path $PSHOME 'Modules') + ';' + $env:PSModulePath
$version = '8.0.1'
$expectedHash = 'e2aaeaa0fdbc397d4794828086424d4aaa2102cef1fb6874f6ffd29c0b88b673'
$cache = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools'
$target = Join-Path $cache 'ffmpeg'
$files = @('ffmpeg.exe', 'ffprobe.exe', 'LICENSE.txt', 'README.txt')
$marker = Join-Path $target 'archive.sha256'
if ((Test-Path -LiteralPath $marker) -and (Get-Content -LiteralPath $marker -Raw).Trim() -eq $expectedHash -and
    -not ($files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $target $_)) })) { return }

$null = New-Item -ItemType Directory -Force -Path $cache, $target
$archive = Join-Path $cache "ffmpeg-$version.zip"
$asset = "ffmpeg-$version-essentials_build.zip"
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $expectedHash) {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing "https://github.com/GyanD/codexffmpeg/releases/download/$version/$asset" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $expectedHash) { throw 'FFmpeg archive checksum mismatch.' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in $files) {
        $sourceName = if ($name -eq 'LICENSE.txt') { 'LICENSE' } else { $name }
        $entry = $zip.Entries | Where-Object { $_.Name -eq $sourceName } | Select-Object -First 1
        if (-not $entry) { throw "Missing $name in FFmpeg archive." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target $name), $true)
    }
} finally { $zip.Dispose() }
Set-Content -LiteralPath $marker -Value $expectedHash -Encoding ascii
