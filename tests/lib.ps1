function New-MSBuildCacheTestProject
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectDir,

        [Parameter(Mandatory = $false)]
        [string] $GitUserName = "msbuildcache-test",

        [Parameter(Mandatory = $false)]
        [string] $GitUserEmail = "msbuildcache-test@local"
    )

    Copy-Item -Path (Join-Path $PSScriptRoot "TestProject") -Destination $ProjectDir -Recurse

    Push-Location $ProjectDir
    try
    {
        & git init *> $null
        & git config user.email $GitUserEmail
        & git config user.name $GitUserName
        & git add . *> $null
        & git commit -m "init" *> $null
    }
    finally
    {
        Pop-Location
    }
}

function ConvertTo-MSBuildCommandLinePropertyValue
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Value
    )

    # MSBuild uses semicolons to separate assignments within a /p switch. Escape both the
    # separator and the escape marker so the property receives the original value.
    return $Value.Replace("%", "%25").Replace(";", "%3B")
}

function Invoke-MSBuildCacheBuild
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $MSBuildPath,

        [Parameter(Mandatory = $true)]
        [string] $ProjectDir,

        [Parameter(Mandatory = $true)]
        [string] $LogDirectory,

        [Parameter(Mandatory = $true)]
        [string] $CachePackage,

        [Parameter(Mandatory = $true)]
        [string] $CacheUniverse,

        [Parameter(Mandatory = $true)]
        [string] $CacheRoot,

        [Parameter(Mandatory = $false)]
        [hashtable] $ExtraProperties = @{},

        [Parameter(Mandatory = $false)]
        [string] $Context = "MSBuildCache test"
    )

    New-Item -ItemType Directory -Path $LogDirectory -Force > $null

    $arguments = @(
        "-graph",
        "-reportfileaccesses",
        "-p:MSBuildCachePackage=$CachePackage",
        "-p:MSBuildCacheCacheUniverse=$CacheUniverse",
        "-p:MSBuildCacheLocalCacheRootPath=$CacheRoot",
        "-p:MSBuildCacheLogDirectory=$LogDirectory\MSBuildCacheLogs",
        "-binaryLogger:$LogDirectory\msbuild.binlog"
    )

    foreach ($key in $ExtraProperties.Keys)
    {
        $value = ConvertTo-MSBuildCommandLinePropertyValue ([string] $ExtraProperties[$key])
        $arguments += "-p:$key=$value"
    }

    $stdout = Join-Path $LogDirectory "stdout.txt"
    $stderr = Join-Path $LogDirectory "stderr.txt"
    $process = Start-Process -FilePath $MSBuildPath -ArgumentList $arguments `
        -WorkingDirectory $ProjectDir `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru -NoNewWindow

    # Start-Process only populates ExitCode reliably after the handle has been accessed.
    $null = $process.Handle
    $process.WaitForExit()
    if ($process.ExitCode -ne 0)
    {
        Get-Content $stdout -ErrorAction SilentlyContinue | Select-Object -Last 40 | Write-Host
        Get-Content $stderr -ErrorAction SilentlyContinue | Select-Object -Last 40 | Write-Host
        throw "[$Context] build failed (exit=$($process.ExitCode); see $stdout)."
    }

    $output = Get-Content $stdout -Raw
    $hitMatch = [regex]::Match($output, 'Cache Hit Count: (?<Value>\d+)')
    $missMatch = [regex]::Match($output, 'Cache Miss Count: (?<Value>\d+)')
    $ratioMatch = [regex]::Match($output, 'Cache Hit Ratio: (?<Value>\d+\.\d+%)')
    if (-not ($hitMatch.Success -and $missMatch.Success -and $ratioMatch.Success))
    {
        throw "[$Context] could not parse cache statistics from $stdout."
    }

    return [pscustomobject]@{
        Hits     = [int] $hitMatch.Groups['Value'].Value
        Misses   = [int] $missMatch.Groups['Value'].Value
        HitRatio = $ratioMatch.Groups['Value'].Value
        LogDir   = $LogDirectory
    }
}

function Assert-CacheStats
{
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Result,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedHits,

        [Parameter(Mandatory = $true)]
        [int] $ExpectedMisses,

        [Parameter(Mandatory = $false)]
        [string] $Context,

        [Parameter(Mandatory = $false)]
        [string] $ScenarioName,

        [Parameter(Mandatory = $false)]
        [string] $Step
    )

    if (-not $Context)
    {
        $Context = "$ScenarioName :: $Step"
    }

    $expectedRatio = "{0:P1}" -f ($ExpectedHits / ($ExpectedHits + $ExpectedMisses))
    $matches = $Result.Hits -eq $ExpectedHits `
        -and $Result.Misses -eq $ExpectedMisses `
        -and $Result.HitRatio -eq $expectedRatio

    $marker = if ($matches) { "PASS" } else { "FAIL" }
    Write-Host ("  [{0,4}] {1}  hits={2} misses={3} ratio={4}  (expected hits={5} misses={6} ratio={7})" `
        -f $marker, $Context, $Result.Hits, $Result.Misses, $Result.HitRatio, $ExpectedHits, $ExpectedMisses, $expectedRatio)

    if (-not $matches)
    {
        throw "[$Context] cache stats mismatch."
    }
}
