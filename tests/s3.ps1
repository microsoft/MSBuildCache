param
(
    [Parameter(Mandatory = $false)]
    [string] $LogDirectory = $env:LogDirectory,

    [Parameter(Mandatory = $false)]
    [string] $LocalPackageDir = $env:LocalPackageDir,

    [Parameter(Mandatory = $false)]
    [string] $TestRoot,

    [Parameter(Mandatory = $false)]
    [string] $MSBuildPath = $null,

    [Parameter(Mandatory = $false)]
    [string] $Configuration = "Debug",

    [Parameter(Mandatory = $false)]
    [string] $BucketName = "msbuildcache-test",

    # An existing S3-compatible endpoint with the bucket already created. If not provided, a local moto server is used.
    [Parameter(Mandatory = $false)]
    [string] $S3ServiceUrl,

    [Parameter(Mandatory = $false)]
    [int] $S3Port = 9099
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$CachePackage = "Microsoft.MSBuildCache.S3"

function Run-Test {
    param (
        [Parameter(Mandatory = $true)]
        [string] $TestName,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheMisses,

        [Parameter(Mandatory = $false)]
        [switch] $ClearLocalCache
    )

    Write-Host "[$TestName] Starting test"

    Write-Host "[$TestName] Cleaning"
    Push-Location $ProjectDir
    & git clean -fdx
    Pop-Location

    if ($ClearLocalCache)
    {
        Remove-Item -Path "$TestRoot\MSBuildCache" -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "[$TestName] Building"
    $result = Invoke-MSBuildCacheBuild `
        -MSBuildPath $MSBuildPath `
        -ProjectDir $ProjectDir `
        -LogDirectory (Join-Path $LogDirectory $TestName) `
        -CachePackage $CachePackage `
        -CacheUniverse $CacheUniverse `
        -CacheRoot "$TestRoot\MSBuildCache" `
        -ExtraProperties @{
            "MSBuildCacheS3BucketName" = $BucketName
            "MSBuildCacheS3ServiceUrl" = $S3ServiceUrl
        } `
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

$MotoServer = $null
try
{
    if (-not $S3ServiceUrl)
    {
        # moto doesn't verify credentials, but the AWS SDK needs some to sign requests.
        if (-not $env:AWS_ACCESS_KEY_ID)
        {
            $env:AWS_ACCESS_KEY_ID = "testonly"
            $env:AWS_SECRET_ACCESS_KEY = "testonly"
        }

        Write-Host "Starting moto on port $S3Port"
        $MotoServer = Start-Process -FilePath "uv" `
            -ArgumentList @("run", "--no-project", "--with", "moto[s3,server]==5.2.2", "moto_server", "-p", "$S3Port") `
            -RedirectStandardOutput (Join-Path $LogDirectory "moto-stdout.txt") `
            -RedirectStandardError (Join-Path $LogDirectory "moto-stderr.txt") `
            -PassThru -NoNewWindow
        $S3ServiceUrl = "http://127.0.0.1:$S3Port"

        $deadline = (Get-Date).AddSeconds(120)
        while ($true)
        {
            try
            {
                Invoke-WebRequest -Method Put -Uri "$S3ServiceUrl/$BucketName" -UseBasicParsing > $null
                break
            }
            catch
            {
                if ($MotoServer.HasExited -or (Get-Date) -gt $deadline)
                {
                    throw "moto did not start. See $LogDirectory\moto-stderr.txt."
                }

                Start-Sleep -Milliseconds 500
            }
        }
    }

    Write-Host "Running test in $TestRoot against $S3ServiceUrl"

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

    Run-Test `
        -TestName "WarmCacheRemoteOnly" `
        -ExpectedCacheHits 1 `
        -ExpectedCacheMisses 0 `
        -ClearLocalCache
}
finally
{
    if ($MotoServer -and -not $MotoServer.HasExited)
    {
        $MotoServer.Kill($true)
        $MotoServer.WaitForExit()
    }
}
