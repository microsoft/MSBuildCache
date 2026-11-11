param
(
    [Parameter(Mandatory = $true)]
    [string] $LocalPackageDir,

    [Parameter(Mandatory = $true)]
    [string] $MSBuildPath,

    [Parameter(Mandatory = $true)]
    [string] $TestRoot
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$graphRoot = Join-Path $TestRoot ([Guid]::NewGuid().ToString())
$projectDir = Join-Path $graphRoot "src"
$previousLocalPackageDir = $env:LocalPackageDir
$previousNugetPackages = $env:NUGET_PACKAGES
try
{
    New-Item -ItemType Directory -Path $graphRoot -Force > $null
    Copy-Item (Join-Path $PSScriptRoot "..\nuget.config") $graphRoot
    New-MSBuildCacheTestProject -ProjectDir $projectDir
    $env:LocalPackageDir = $LocalPackageDir
    $env:NUGET_PACKAGES = Join-Path $graphRoot "nuget"

    Push-Location $projectDir
    try
    {
        $output = & dotnet $MSBuildPath -noAutoResponse -nologo -verbosity:minimal -graph -restore `
            "-p:UseSharedCompilation=false" `
            "-p:MSBuildCachePackage=Microsoft.MSBuildCache.Local" `
            "-p:MSBuildCacheLocalCacheRootPath=$graphRoot\cache" `
            "-p:MSBuildCacheLogDirectory=$graphRoot\logs" 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }

    $output | Write-Host
    if ($exitCode -ne 0 -or -not ($output | Select-String -SimpleMatch "Loading the following project cache plugin: MSBuildCacheLocalPlugin"))
    {
        throw "Core graph build did not load the local cache plugin using $MSBuildPath."
    }
}
finally
{
    $env:LocalPackageDir = $previousLocalPackageDir
    $env:NUGET_PACKAGES = $previousNugetPackages
    if (Test-Path $graphRoot)
    {
        Remove-Item -Path $graphRoot -Recurse -Force
    }
}
