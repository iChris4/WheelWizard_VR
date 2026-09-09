[CmdletBinding()]
param([string]$Tag, [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup[0].Version
if ($Tag -and $Tag -cne "v$version") { throw "Release tag must be v$version, got $Tag" }
if ($ExecutablePath) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $ExecutablePath))
    if (($info.ProductVersion -split '\+')[0] -cne $version -or $info.ProductName -cne 'WheelWizard VR') {
        throw 'Launcher executable identity/version mismatch.'
    }
}
Write-Output "Verified WheelWizard VR $version"
