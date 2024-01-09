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

function Run-Test {
    param (
        [Parameter(Mandatory = $true)]
        [string] $TestName,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedCacheMisses
    )

    $LogSubDir = Join-Path $LogDirectory $TestName
    New-Item -ItemType Directory -Path $LogSubDir > $null

    Write-Host "[$TestName] Starting test"

    Write-Host "[$TestName] Cleaning"
    Remove-Item -Path "$ProjectDir\obj" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$ProjectDir\bin" -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "[$TestName] Building"
    $ProcessOptions = @{
        FilePath = $MSBuildPath
        ArgumentList = @(
            "-graph"
            "-reportfileaccesses"
            "-p:MSBuildCachePackage=$CachePackage"
            "-p:MSBuildCacheCacheUniverse=$CacheUniverse"
            "-p:MSBuildCacheLocalCacheRootPath=$TestRoot\MSBuildCache"
            "-p:MSBuildCacheLogDirectory=$LogSubDir\MSBuildCacheLogs"
            "-binaryLogger:$LogSubDir\msbuild.binlog"
        )
        WorkingDirectory = $ProjectDir
        RedirectStandardOutput = "$LogSubDir\stdout.txt"
        RedirectStandardError = "$LogSubDir\stderr.txt"
        PassThru = $true
        NoNewWindow = $true
    }
    Write-Host "[$TestName] Running: ""$($ProcessOptions.FilePath)"" $($ProcessOptions.ArgumentList)"
    $Process = Start-Process @ProcessOptions

    # Not using -Wait because that waits for all children to exit too, which may included the compiler server.
    # But WaitForExit for some reason makes the exit code not work unless we access the process handle.
    # See: https://stackoverflow.com/a/23797762
    $ProcessHandle = $Process.Handle
    $Process.WaitForExit()

    if ($Process.ExitCode -ne 0)
    {
        throw "[$TestName] Build failed with exit code $($Process.ExitCode). See: $LogSubDir\msbuild.binlog"
    }

    Write-Host "[$TestName] Built successfully"

    Write-Host "[$TestName] Checking cache statistics"
    $BuildOutput = Get-Content -Path $ProcessOptions.RedirectStandardOutput -Raw
    if ($BuildOutput -Match "Project cache statistics:")
    {
        Write-Host "[$TestName] Project cache statistics found"
    }
    else
    {
        throw "[$TestName] Could not find cache statistics"
    }

    if ($BuildOutput -Match "Cache Hit Count: (?<CacheHits>\d+)")
    {
        if ($Matches.CacheHits -eq $ExpectedCacheHits)
        {
            Write-Host "[$TestName] Found $($Matches.CacheHits) cache hits"
        }
        else
        {
            throw "[$TestName] Unexpected number of cache hits. Expected $ExpectedCacheHits. Actual: $($Matches.CacheHits)"
        }
    }
    else
    {
        throw "[$TestName] Could not find cache hit count"
    }

    if ($BuildOutput -Match "Cache Miss Count: (?<CacheMisses>\d+)")
    {
        if ($Matches.CacheMisses -eq $ExpectedCacheMisses)
        {
            Write-Host "[$TestName] Found $($Matches.CacheMisses) cache misses"
        }
        else
        {
            throw "[$TestName] Unexpected number of cache misses. Expected $ExpectedCacheMisses. Actual: $($Matches.CacheMisses)"
        }
    }
    else
    {
        throw "[$TestName] Could not find cache miss count"
    }

    $ExpectedCacheHitRatio = "{0:P1}" -f (($ExpectedCacheHits) / ($ExpectedCacheHits + $ExpectedCacheMisses))
    if ($BuildOutput -Match "Cache Hit Ratio: (?<CacheHitRatio>\d+\.\d+%)")
    {
        if ($Matches.CacheHitRatio -eq $ExpectedCacheHitRatio)
        {
            Write-Host "[$TestName] Found $($Matches.CacheHitRatio) cache hit ratio"
        }
        else
        {
            throw "[$TestName] Unexpected cache hit ratio. Expected $ExpectedCacheHitRatio. Actual: $($Matches.CacheHitRatio)"
        }
    }
    else
    {
        throw "[$TestName] Could not find cache hit ratio"
    }

    Write-Host "[$TestName] Cache statistics validated successfully"

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
Copy-Item -Path (Join-Path $PSScriptRoot "TestProject") -Destination $ProjectDir -Recurse

# Create a new git repo with an initial commit so hashing works properly
Write-Host "Creating Git repo in $ProjectDir"
Push-Location $ProjectDir
& git init
& git config user.email "$Env:UserName@microsoft.com"
& git config user.name "$Env:UserName"
& git add .
& git commit -m "Dummy"
Pop-Location

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
