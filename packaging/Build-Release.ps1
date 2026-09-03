param(
    [string] $CoreVersion = '1.13.15',
    [string] $AppVersion = '1.0.0',
    [string] $ConfigPath = 'config.example.json',
    [string] $OutputDirectory = 'artifacts/release',
    [string] $DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$workRoot = Join-Path $artifactsRoot 'build'
$cacheRoot = Join-Path $artifactsRoot 'cache'
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$configurationPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ConfigPath))
$projectPath = Join-Path $repoRoot 'src\RouteFlow\RouteFlow.csproj'
$vendorRoot = Join-Path $repoRoot 'third_party\sing-box'
$tarCommand = if (Get-Command tar.exe -ErrorAction SilentlyContinue) { 'tar.exe' } else { 'tar' }

if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "Configuration file not found: $configurationPath"
}
if (-not (Get-Command $DotNetPath -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK was not found: $DotNetPath"
}

function Get-ReleaseFile([string] $Url, [string] $Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) { return }
    New-Item -ItemType Directory -Force ([System.IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if (-not $curl) { $curl = Get-Command curl -CommandType Application -ErrorAction SilentlyContinue }
    if ($curl) {
        & $curl.Source -fL --retry 3 -o $Destination $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Url" }
        return
    }
    Invoke-WebRequest -Uri $Url -OutFile $Destination
}

function Copy-LinuxText([string] $Source, [string] $Destination) {
    $text = [System.IO.File]::ReadAllText($Source).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllText($Destination, $text, [System.Text.UTF8Encoding]::new($false))
}

if (Test-Path -LiteralPath $workRoot) {
    $resolvedWork = [System.IO.Path]::GetFullPath($workRoot)
    $expectedPrefix = [System.IO.Path]::GetFullPath($artifactsRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedWork.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected work directory: $resolvedWork"
    }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
New-Item -ItemType Directory -Force $workRoot, $cacheRoot, $outputRoot | Out-Null

$vendorWindowsBinary = Join-Path $vendorRoot 'win-x64\sing-box.exe'
$vendorWindowsCronet = Join-Path $vendorRoot 'win-x64\libcronet.dll'
$vendorLinuxBinary = Join-Path $vendorRoot 'linux-x64\sing-box'
$vendorLinuxCronet = Join-Path $vendorRoot 'linux-x64\libcronet.so'
$vendorLicense = Join-Path $vendorRoot 'LICENSE'
$vendorVersionPath = Join-Path $vendorRoot 'VERSION'
$vendorVersion = if (Test-Path -LiteralPath $vendorVersionPath -PathType Leaf) {
    [System.IO.File]::ReadAllText($vendorVersionPath).Trim()
}
$hasVendoredCores = (Test-Path -LiteralPath $vendorWindowsBinary -PathType Leaf) -and
    (Test-Path -LiteralPath $vendorWindowsCronet -PathType Leaf) -and
    (Test-Path -LiteralPath $vendorLinuxBinary -PathType Leaf) -and
    (Test-Path -LiteralPath $vendorLinuxCronet -PathType Leaf) -and
    $vendorVersion -eq $CoreVersion

if ($hasVendoredCores) {
    Write-Output "Using vendored sing-box $CoreVersion cores."
    $windowsCoreBinary = Get-Item -LiteralPath $vendorWindowsBinary
    $windowsCronet = Get-Item -LiteralPath $vendorWindowsCronet
    $linuxCoreBinary = Get-Item -LiteralPath $vendorLinuxBinary
    $linuxCronet = Get-Item -LiteralPath $vendorLinuxCronet
    $linuxLicense = Get-Item -LiteralPath $vendorLicense -ErrorAction SilentlyContinue
}
else {
    $windowsArchive = Join-Path $cacheRoot "sing-box-$CoreVersion-windows-amd64.zip"
    $linuxArchive = Join-Path $cacheRoot "sing-box-$CoreVersion-linux-amd64.tar.gz"
    $releaseBase = "https://github.com/SagerNet/sing-box/releases/download/v$CoreVersion"
    Get-ReleaseFile "$releaseBase/sing-box-$CoreVersion-windows-amd64.zip" $windowsArchive
    Get-ReleaseFile "$releaseBase/sing-box-$CoreVersion-linux-amd64.tar.gz" $linuxArchive

    $windowsCore = Join-Path $workRoot 'core\windows'
    $linuxCore = Join-Path $workRoot 'core\linux'
    New-Item -ItemType Directory -Force $windowsCore, $linuxCore | Out-Null
    Expand-Archive -LiteralPath $windowsArchive -DestinationPath $windowsCore -Force
    & $tarCommand -xzf $linuxArchive -C $linuxCore
    if ($LASTEXITCODE -ne 0) { throw 'Linux core extraction failed.' }

    $windowsCoreBinary = Get-ChildItem -LiteralPath $windowsCore -Recurse -File -Filter 'sing-box.exe' | Select-Object -First 1
    $windowsCronet = Get-ChildItem -LiteralPath $windowsCore -Recurse -File -Filter 'libcronet.dll' | Select-Object -First 1
    $linuxCoreBinary = Get-ChildItem -LiteralPath $linuxCore -Recurse -File -Filter 'sing-box' | Select-Object -First 1
    $linuxCronet = Get-ChildItem -LiteralPath $linuxCore -Recurse -File -Filter 'libcronet.so' | Select-Object -First 1
    $linuxLicense = Get-ChildItem -LiteralPath $linuxCore -Recurse -File -Filter 'LICENSE' | Select-Object -First 1
}
if (-not $windowsCoreBinary -or -not $linuxCoreBinary) { throw 'The sing-box cores are incomplete.' }

$windowsPublish = Join-Path $workRoot 'publish\win-x64\RouteFlow'
$linuxPublish = Join-Path $workRoot 'publish\linux-x64'
& $DotNetPath publish $projectPath -c Release -r win-x64 --self-contained true -p:Version=$AppVersion -o $windowsPublish
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
& $DotNetPath publish $projectPath -c Release -r linux-x64 --self-contained true -p:Version=$AppVersion -o $linuxPublish
if ($LASTEXITCODE -ne 0) { throw 'Linux publish failed.' }

Copy-Item $windowsCoreBinary.FullName (Join-Path $windowsPublish 'sing-box.exe') -Force
if ($windowsCronet) { Copy-Item $windowsCronet.FullName (Join-Path $windowsPublish 'libcronet.dll') -Force }
Copy-Item $configurationPath (Join-Path $windowsPublish 'config.json') -Force
Copy-Item (Join-Path $PSScriptRoot 'windows\Open Relay.cmd') (Join-Path $windowsPublish 'Open Relay.cmd') -Force

$windowsZip = Join-Path $outputRoot "RouteFlow-$AppVersion-win-x64-portable.zip"
if (Test-Path -LiteralPath $windowsZip) { Remove-Item -LiteralPath $windowsZip -Force }
Compress-Archive -Path $windowsPublish -DestinationPath $windowsZip -CompressionLevel Optimal

$debRoot = Join-Path $workRoot 'deb-root'
$debDirectories = @(
    'DEBIAN', 'opt\routeflow', 'usr\bin', 'usr\share\applications',
    'usr\share\icons\hicolor\256x256\apps', 'usr\share\icons\hicolor\scalable\apps',
    'usr\share\routeflow'
)
foreach ($directory in $debDirectories) {
    New-Item -ItemType Directory -Force (Join-Path $debRoot $directory) | Out-Null
}
Copy-Item (Join-Path $linuxPublish '*') (Join-Path $debRoot 'opt\routeflow') -Recurse -Force
Copy-Item $linuxCoreBinary.FullName (Join-Path $debRoot 'opt\routeflow\sing-box') -Force
if ($linuxCronet) { Copy-Item $linuxCronet.FullName (Join-Path $debRoot 'opt\routeflow\libcronet.so') -Force }
if ($linuxLicense) { Copy-Item $linuxLicense.FullName (Join-Path $debRoot 'opt\routeflow\LICENSE.sing-box') -Force }

$controlDestination = Join-Path $debRoot 'DEBIAN\control'
Copy-LinuxText (Join-Path $PSScriptRoot 'linux\control') $controlDestination
$control = [System.IO.File]::ReadAllText($controlDestination)
$control = [System.Text.RegularExpressions.Regex]::Replace($control, '(?m)^Version:.*$', "Version: $AppVersion-1")
[System.IO.File]::WriteAllText($controlDestination, $control, [System.Text.UTF8Encoding]::new($false))
Copy-LinuxText (Join-Path $PSScriptRoot 'linux\routeflow') (Join-Path $debRoot 'usr\bin\routeflow')
Copy-LinuxText (Join-Path $PSScriptRoot 'linux\routeflow.desktop') (Join-Path $debRoot 'usr\share\applications\routeflow.desktop')
Copy-Item (Join-Path $repoRoot 'src\RouteFlow\Assets\routeflow.ico') (Join-Path $debRoot 'usr\share\icons\hicolor\256x256\apps\routeflow.ico') -Force
Copy-Item (Join-Path $repoRoot 'src\RouteFlow\Assets\routeflow.png') (Join-Path $debRoot 'usr\share\icons\hicolor\256x256\apps\routeflow.png') -Force
Copy-Item (Join-Path $repoRoot 'src\RouteFlow\Assets\routeflow.svg') (Join-Path $debRoot 'usr\share\icons\hicolor\scalable\apps\routeflow.svg') -Force
Copy-Item $configurationPath (Join-Path $debRoot 'usr\share\routeflow\config.json') -Force

$debPath = Join-Path $outputRoot "routeflow_$AppVersion-1_amd64.deb"
& (Join-Path $PSScriptRoot 'Build-Deb.ps1') -PackageRoot $debRoot -OutputPath $debPath

if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $selfTest = Start-Process -FilePath (Join-Path $windowsPublish 'RouteFlow.exe') -ArgumentList '--self-test' -WorkingDirectory $windowsPublish -Wait -PassThru
    if ($selfTest.ExitCode -ne 0) { throw "Windows package self-test failed with exit code $($selfTest.ExitCode)." }
}

Write-Output "Created: $windowsZip"
Write-Output "Created: $debPath"
