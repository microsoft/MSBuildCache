param
(
    [Parameter(Mandatory = $false)]
    [string] $LogDirectory = $env:LogDirectory,

    [Parameter(Mandatory = $false)]
    [string] $LocalPackageDir = $env:LocalPackageDir,

    [Parameter(Mandatory = $false)]
    [string] $TestRoot,

    [Parameter(Mandatory = $false)]
    [string] $CachePackage = "Microsoft.MSBuildCache.Local",

    [Parameter(Mandatory = $false)]
    [string] $MSBuildPath = $null,

    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Debug"
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

function Run-Test {
    param (
        [Parameter(Mandatory = $true)]
        [string] $TestName,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheMisses
    )

    Write-Host "[$TestName] Starting test"

    Write-Host "[$TestName] Cleaning"
    Push-Location $ProjectDir
    & git clean -fdx
    Pop-Location

    Write-Host "[$TestName] Building"
    $result = Invoke-MSBuildCacheBuild `
        -MSBuildPath $MSBuildPath `
        -ProjectDir $ProjectDir `
        -LogDirectory (Join-Path $LogDirectory $TestName) `
        -CachePackage $CachePackage `
        -CacheUniverse $CacheUniverse `
        -CacheRoot "$TestRoot\MSBuildCache" `
        -Context $TestName

    Assert-CacheStats `
        -Result $result `
        -ExpectedHits $ExpectedCacheHits `
        -ExpectedMisses $ExpectedCacheMisses `
        -Context $TestName

    Write-Host "[$TestName] Test complete"
}

Push-Location (Join-Path $PSScriptRoot "..")
$RepoRoot = "$PWD"
Pop-Location

if (-not $LocalPackageDir)
{
    $LocalPackageDir = Join-Path $RepoRoot "artifacts\$Configuration\packages"
}

if (-not $LogDirectory)
{
    $LogDirectory = Join-Path $RepoRoot "logs\Tests"
}

if (-not $TestRoot)
{
    $TestRoot = Join-Path $RepoRoot "TestResult\$CachePackage"
}

if (-not $MSBuildPath)
{
    # Find it on the PATH
    $MSBuildPath = (Get-Command "msbuild").Path
}
# Use a unique cache universe for every test run
$CacheUniverse = (New-Guid).ToString()

$env:LocalPackageDir = $LocalPackageDir

Write-Host "Log Directory: $LogDirectory"
Remove-Item -Path $LogDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $LogDirectory > $null

# set up original run
Write-Host "Running test in $TestRoot"

$env:NUGET_PACKAGES="$TestRoot\.nuget"
$ProjectDir = Join-Path $TestRoot "src"

Remove-Item -Path $TestRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Creating Git repo in $ProjectDir"
New-MSBuildCacheTestProject `
    -ProjectDir $ProjectDir `
    -GitUserName $Env:UserName `
    -GitUserEmail "$Env:UserName@microsoft.com"

Run-Test `
    -TestName "ColdCache" `
    -ExpectedCacheHits 0 `
    -ExpectedCacheMisses 1

Run-Test `
    -TestName "WarmCache" `
    -ExpectedCacheHits 1 `
    -ExpectedCacheMisses 0

# set up junction run
try {
    cmd /c mklink /J "$RepoRoot-OtherPath" "$RepoRoot"
    $TestRoot = $TestRoot.Replace($RepoRoot, "$RepoRoot-OtherPath")
    Write-Host "Running test in $TestRoot"

    $env:NUGET_PACKAGES="$TestRoot\.nuget"
    $ProjectDir = Join-Path $TestRoot "src"

    Run-Test `
        -TestName "WarmCacheOtherRoot" `
        -ExpectedCacheHits 1 `
        -ExpectedCacheMisses 0
}
finally  {
    # weird way to delete a junction in PowerShell
    (Get-Item "$RepoRoot-OtherPath").Delete()
}
