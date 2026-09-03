param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

function Set-TarModes([string] $ArchivePath) {
    $compressed = [System.IO.File]::OpenRead($ArchivePath)
    $tar = [System.IO.MemoryStream]::new()
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($compressed, [System.IO.Compression.CompressionMode]::Decompress)
        try { $gzip.CopyTo($tar) } finally { $gzip.Dispose() }
    }
    finally {
        $compressed.Dispose()
    }

    $bytes = $tar.ToArray()
    $ascii = [System.Text.Encoding]::ASCII
    $executables = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    @(
        './usr/bin/routeflow',
        './opt/routeflow/RouteFlow',
        './opt/routeflow/sing-box',
        './opt/routeflow/createdump'
    ) | ForEach-Object { [void] $executables.Add($_) }

    for ($offset = 0; $offset + 512 -le $bytes.Length;) {
        $isEmpty = $true
        for ($index = 0; $index -lt 512; $index++) {
            if ($bytes[$offset + $index] -ne 0) { $isEmpty = $false; break }
        }
        if ($isEmpty) { break }

        $name = $ascii.GetString($bytes, $offset, 100).Trim([char]0)
        $prefix = $ascii.GetString($bytes, $offset + 345, 155).Trim([char]0)
        if ($prefix) { $name = "$prefix/$name" }
        $isDirectory = $bytes[$offset + 156] -eq [byte][char]'5' -or $name.EndsWith('/')
        $mode = if ($isDirectory -or $executables.Contains($name)) { '0000755' } else { '0000644' }
        $modeBytes = $ascii.GetBytes($mode + [char]0)
        [Array]::Copy($modeBytes, 0, $bytes, $offset + 100, 8)

        for ($index = 148; $index -lt 156; $index++) { $bytes[$offset + $index] = 32 }
        $checksum = 0
        for ($index = 0; $index -lt 512; $index++) { $checksum += $bytes[$offset + $index] }
        $checksumBytes = $ascii.GetBytes([Convert]::ToString($checksum, 8).PadLeft(6, '0') + [char]0 + ' ')
        [Array]::Copy($checksumBytes, 0, $bytes, $offset + 148, 8)

        $sizeText = $ascii.GetString($bytes, $offset + 124, 12).Trim([char]0, ' ')
        $size = if ($sizeText) { [Convert]::ToInt64($sizeText, 8) } else { 0 }
        $offset += 512 + ([Math]::Ceiling($size / 512.0) * 512)
    }

    $output = [System.IO.File]::Create($ArchivePath)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($output, [System.IO.Compression.CompressionLevel]::Optimal)
        try { $gzip.Write($bytes, 0, $bytes.Length) } finally { $gzip.Dispose() }
    }
    finally {
        $output.Dispose()
    }
}

$packageRootPath = (Resolve-Path -LiteralPath $PackageRoot).Path
$outputFullPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($outputFullPath)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$workPath = Join-Path ([System.IO.Path]::GetDirectoryName($outputFullPath)) 'deb-archive'
$tarCommand = if (Get-Command tar.exe -ErrorAction SilentlyContinue) { 'tar.exe' } else { 'tar' }

New-Item -ItemType Directory -Force -Path $workPath | Out-Null
$debianBinary = Join-Path $workPath 'debian-binary'
$controlArchive = Join-Path $workPath 'control.tar.gz'
$dataArchive = Join-Path $workPath 'data.tar.gz'
[System.IO.File]::WriteAllText($debianBinary, "2.0`n", [System.Text.Encoding]::ASCII)

& $tarCommand -czf $controlArchive --format ustar -C (Join-Path $packageRootPath 'DEBIAN') .
if ($LASTEXITCODE -ne 0) { throw 'Failed to create control.tar.gz.' }
Set-TarModes $controlArchive
& $tarCommand -czf $dataArchive --format ustar --exclude './DEBIAN' -C $packageRootPath .
if ($LASTEXITCODE -ne 0) { throw 'Failed to create data.tar.gz.' }
Set-TarModes $dataArchive

$stream = [System.IO.File]::Create($outputFullPath)
try {
    $ascii = [System.Text.Encoding]::ASCII
    $magic = $ascii.GetBytes("!<arch>`n")
    $stream.Write($magic, 0, $magic.Length)

    foreach ($entry in @($debianBinary, $controlArchive, $dataArchive)) {
        $info = Get-Item -LiteralPath $entry
        $name = ($info.Name + '/').PadRight(16)
        $modified = [DateTimeOffset]::new($info.LastWriteTimeUtc).ToUnixTimeSeconds().ToString().PadRight(12)
        $headerText = $name + $modified + '0'.PadRight(6) + '0'.PadRight(6) + '100644'.PadRight(8) + $info.Length.ToString().PadRight(10) + [char]96 + [char]10
        $header = $ascii.GetBytes($headerText)
        if ($header.Length -ne 60) { throw "Invalid ar header for $($info.Name)." }
        $stream.Write($header, 0, $header.Length)

        $input = [System.IO.File]::OpenRead($info.FullName)
        try { $input.CopyTo($stream) } finally { $input.Dispose() }
        if (($info.Length % 2) -ne 0) { $stream.WriteByte(10) }
    }
}
finally {
    $stream.Dispose()
}
