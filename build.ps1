[CmdletBinding()]
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sourceDirectory = Join-Path $PSScriptRoot 'src'
$assetDirectory = Join-Path $PSScriptRoot 'assets'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $destination = Join-Path $PSScriptRoot 'SteelSeries GG DPI Switcher.exe'
} elseif ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $destination = [System.IO.Path]::GetFullPath($OutputPath)
} else {
    $destination = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputPath))
}
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null

$sourceFiles = @('AppInfo.cs', 'Program.cs', 'AeroxDevice.cs', 'DpiPreferences.cs', 'DpiHotkeys.cs', 'DpiToast.cs') |
    ForEach-Object { Join-Path $sourceDirectory $_ }
$compilerArguments = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/langversion:5',
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    "/win32manifest:$assetDirectory\app.manifest", "/win32icon:$assetDirectory\app.ico", "/out:$destination"
) + $sourceFiles
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $destination
