# tests/s3.ps1
#
# End-to-end test for the Microsoft.MSBuildCache.S3 plugin. It runs the same cold and warm cache builds as
# smoke.ps1, plus a build with the local cache deleted so cache hits have to be served from S3. It also produces
# one output large enough to take the multipart transfer path and verifies it round trips byte for byte.
#
# By default a local moto server is used as the S3 endpoint. moto is a pure-Python AWS mock, so unlike MinIO it
# needs no container runtime, which makes it usable on a Windows build agent. Pass -S3ServiceUrl to test against an
# existing S3-compatible endpoint instead.
#
# To run:
#   .\tests\s3.ps1

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

    # An existing S3-compatible endpoint. When omitted, a local moto server is started and the bucket is created in it.
    [Parameter(Mandatory = $false)]
    [string] $S3ServiceUrl,

    [Parameter(Mandatory = $false)]
    [int] $S3Port = 9099,

    # Size of the additional output used to cover multipart transfers. 0 disables it.
    [Parameter(Mandatory = $false)]
    [int] $LargeOutputMegabytes = 24
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

$CachePackage = "Microsoft.MSBuildCache.S3"

# Recorded on the first build and compared on every later one.
$Script:ExpectedLargeOutputHash = $null

function Test-PortListening
{
    param(
        [Parameter(Mandatory = $true)]
        [int] $Port
    )

    $client = New-Object System.Net.Sockets.TcpClient
    try
    {
        $client.Connect("127.0.0.1", $Port)
        return $true
    }
    catch
    {
        return $false
    }
    finally
    {
        $client.Dispose()
    }
}

function Start-MotoServer
{
    param(
        [Parameter(Mandatory = $true)]
        [int] $Port,

        [Parameter(Mandatory = $true)]
        [string] $LogDirectory
    )

    if (Test-PortListening -Port $Port)
    {
        throw "Port $Port is already in use. Pass -S3Port to select another port, or -S3ServiceUrl to use an existing endpoint."
    }

    # Prefer uv, which runs moto from an ephemeral environment without installing anything.
    if (Get-Command "uv" -ErrorAction SilentlyContinue)
    {
        $filePath = "uv"
        $argumentList = @("run", "--no-project", "--with", "moto[s3,server]==5.2.2", "moto_server", "-p", "$Port")
    }
    elseif (Get-Command "moto_server" -ErrorAction SilentlyContinue)
    {
        $filePath = "moto_server"
        $argumentList = @("-p", "$Port")
    }
    else
    {
        throw "Could not find 'uv' or 'moto_server'. Install uv (https://docs.astral.sh/uv), or run 'pip install moto[s3,server]', or pass -S3ServiceUrl to use an existing endpoint."
    }

    Write-Host "Starting S3 server: $filePath $argumentList"
    $process = Start-Process -FilePath $filePath -ArgumentList $argumentList `
        -RedirectStandardOutput (Join-Path $LogDirectory "moto-stdout.txt") `
        -RedirectStandardError (Join-Path $LogDirectory "moto-stderr.txt") `
        -PassThru -NoNewWindow

    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-PortListening -Port $Port))
    {
        if ($process.HasExited)
        {
            throw "S3 server exited with code $($process.ExitCode). See $LogDirectory\moto-stderr.txt."
        }

        if ((Get-Date) -gt $deadline)
        {
            throw "S3 server did not start listening on port $Port in time. See $LogDirectory\moto-stderr.txt."
        }

        Start-Sleep -Milliseconds 500
    }

    Write-Host "S3 server listening on port $Port (pid $($process.Id))"
    return $process
}

function Assert-LargeOutput
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectDir,

        [Parameter(Mandatory = $true)]
        [string] $Context
    )

    $largeOutput = Get-ChildItem -Path $ProjectDir -Recurse -File -Filter "large.bin" | Select-Object -First 1
    if (-not $largeOutput)
    {
        throw "[$Context] the large output was not produced."
    }

    $expectedSize = $LargeOutputMegabytes * 1MB
    if ($largeOutput.Length -ne $expectedSize)
    {
        throw "[$Context] the large output has $($largeOutput.Length) bytes, expected $expectedSize."
    }

    # A transfer assembled from parts can produce a file of the right length but the wrong content, so compare against
    # what the build originally produced.
    $hash = (Get-FileHash -Path $largeOutput.FullName -Algorithm SHA256).Hash
    if (-not $Script:ExpectedLargeOutputHash)
    {
        $Script:ExpectedLargeOutputHash = $hash
    }
    elseif ($hash -ne $Script:ExpectedLargeOutputHash)
    {
        throw "[$Context] the large output was restored with different content. Expected SHA256 $($Script:ExpectedLargeOutputHash), actual $hash."
    }

    Write-Host "  [PASS] $Context  large output verified ($($largeOutput.Length) bytes, SHA256 $hash)"
}

function Run-Test
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $TestName,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheMisses,

        # Deletes the local cache before building, so that cache hits have to come from S3.
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
        Write-Host "[$TestName] Clearing the local cache"
        Remove-Item -Path $CacheRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    $extraProperties = @{
        "MSBuildCacheS3BucketName" = $BucketName
        "MSBuildCacheS3ServiceUrl" = $S3ServiceUrl
        "MSBuildCacheS3ForcePathStyle" = "true"
    }

    if ($LargeOutputMegabytes -gt 0)
    {
        $extraProperties["LargeOutputSizeInMegabytes"] = $LargeOutputMegabytes

        # Put the large output on the multipart path while leaving the small outputs on the single-request path, so
        # that one run covers both.
        $extraProperties["MSBuildCacheS3MultipartThresholdBytes"] = 8MB
        $extraProperties["MSBuildCacheS3MultipartPartSizeBytes"] = 5MB
    }

    Write-Host "[$TestName] Building"
    $result = Invoke-MSBuildCacheBuild `
        -MSBuildPath $MSBuildPath `
        -ProjectDir $ProjectDir `
        -LogDirectory (Join-Path $LogDirectory $TestName) `
        -CachePackage $CachePackage `
        -CacheUniverse $CacheUniverse `
        -CacheRoot $CacheRoot `
        -ExtraProperties $extraProperties `
        -Context $TestName

    Assert-CacheStats `
        -Result $result `
        -ExpectedHits $ExpectedCacheHits `
        -ExpectedMisses $ExpectedCacheMisses `
        -Context $TestName

    if ($LargeOutputMegabytes -gt 0)
    {
        Assert-LargeOutput -ProjectDir $ProjectDir -Context $TestName
    }

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
$CacheRoot = Join-Path $TestRoot "MSBuildCache"

$env:LocalPackageDir = $LocalPackageDir

Write-Host "Log Directory: $LogDirectory"
Remove-Item -Path $LogDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $LogDirectory > $null

# The AWS SDK requires credentials in order to sign requests, even against an endpoint which does not verify them.
if (-not $env:AWS_ACCESS_KEY_ID)
{
    $env:AWS_ACCESS_KEY_ID = "testonly"
    $env:AWS_SECRET_ACCESS_KEY = "testonly"
}

$MotoServer = $null
try
{
    if (-not $S3ServiceUrl)
    {
        $MotoServer = Start-MotoServer -Port $S3Port -LogDirectory $LogDirectory
        $S3ServiceUrl = "http://127.0.0.1:$S3Port"

        # moto does not verify signatures, so an unauthenticated create is enough. An existing endpoint passed via
        # -S3ServiceUrl is expected to already have the bucket.
        Invoke-WebRequest -Method Put -Uri "$S3ServiceUrl/$BucketName" -UseBasicParsing > $null
        Write-Host "Created bucket $BucketName"
    }

    Write-Host "Using S3 endpoint $S3ServiceUrl and bucket $BucketName"
    Write-Host "Running test in $TestRoot"

    $env:NUGET_PACKAGES = "$TestRoot\.nuget"
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

    # Without the local cache, a hit proves the metadata and content round tripped through S3.
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
        Write-Host "Stopping S3 server (pid $($MotoServer.Id))"
        $MotoServer.Kill($true)
        $MotoServer.WaitForExit()
    }
}
