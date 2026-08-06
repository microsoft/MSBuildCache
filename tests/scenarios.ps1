# tests/scenarios.ps1
#
# End-to-end scenario tests for the probe and enumeration fingerprinting feature.
# Each scenario sets up a fresh TestProject copy, runs MSBuild builds with state mutations
# between them, and asserts the expected cache hit/miss profile. The scenarios collectively
# pin the headline behaviors of the feature so they're regression-protected:
#
#   GlobAddCsFile             — Adding a new .cs file the SDK glob now matches must miss
#   GlobUnchangedHit          — Same source between builds must hit (continuity)
#   InScopeMarkerProbeMiss    — A previously-absent in-scope marker that now exists must miss
#   InScopeMarkerStableHit    — Same marker state across builds must hit
#   OutOfScopeMarkerHit       — A marker outside repo + NuGet scope must NOT invalidate
#   IgnoredObservationHit     — A noisy in-scope path matched by IgnoredInputPatterns
#                                 must NOT invalidate even when its existence flips
#   EnumerateOutputDirCycleHit— A project that enumerates a directory it also writes into must
#                                 still hit, across both clean and incremental rebuilds
#   SourceFileChangeMiss      — Editing a source file must miss, and rebuilding must then hit
#   ThirdBuildAllHits         — Three builds, no change: builds 2 AND 3 must both be 100% hits
#
# These exercise the scenarios at the build-system level (the unit tests pin the
# strong-FP behavior in isolation; these prove the feature delivers in real MSBuild builds).
#
# NOTE: these require an MSBuild that reports FileAccessData.EnumeratePattern. On an older
# MSBuild the feature self-disables and the suite skips with a warning rather than failing.
#
# To run:
#   pwsh -NoProfile -File tests/scenarios.ps1

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
    [string] $Configuration = "Debug",

    [Parameter(Mandatory = $false)]
    [string[]] $Scenarios = @(),

    # Set by callers that supply an MSBuild which is expected to support the feature (the
    # tip-of-MSBuild-repo pipeline job). Turns the capability skip into a hard failure so that
    # coverage can't silently disappear if the bootstrap layout or the API changes.
    [Parameter(Mandatory = $false)]
    [bool] $RequireEnumerationCapability = $false
)

Set-StrictMode -Version latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib.ps1")

Push-Location (Join-Path $PSScriptRoot "..")
$RepoRoot = "$PWD"
Pop-Location

if (-not $LocalPackageDir) {
    $LocalPackageDir = Join-Path $RepoRoot "artifacts\$Configuration\packages"
}

if (-not $LogDirectory) {
    $LogDirectory = Join-Path $RepoRoot "logs\Scenarios"
}

if (-not $TestRoot) {
    $TestRoot = Join-Path $RepoRoot "TestResult\Scenarios"
}

if (-not $MSBuildPath) {
    $MSBuildPath = (Get-Command "msbuild").Path
}

$env:LocalPackageDir = $LocalPackageDir

Write-Host "Log directory:    $LogDirectory"
Write-Host "Test root:        $TestRoot"
Write-Host "MSBuild path:     $MSBuildPath"
Write-Host "Cache package:    $CachePackage"
Write-Host ""

if (Test-Path $LogDirectory) {
    Remove-Item -Path $LogDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $LogDirectory > $null

# ----------------------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------------------

function Test-EnumerationCapability
{
    <#
        .SYNOPSIS
        Whether the MSBuild under test reports FileAccessData.EnumeratePattern.

        .DESCRIPTION
        The probe and enumeration fingerprinting feature self-disables when the host MSBuild does
        not report enumeration patterns, so on an older MSBuild these scenarios would assert misses
        that legitimately cannot happen. Inspecting the assembly mirrors what the plugin itself
        does, rather than guessing from a version number. The load happens in a child process so
        the probed assembly is not pinned into this session.
    #>
    param(
        [Parameter(Mandatory = $true)] [string] $MSBuildPath
    )

    $dll = Join-Path (Split-Path -Parent $MSBuildPath) "Microsoft.Build.dll"
    if (-not (Test-Path $dll)) {
        return $false
    }

    $probe = @"
try {
    `$assembly = [System.Reflection.Assembly]::LoadFrom('$dll')
    `$type = `$assembly.GetType('Microsoft.Build.Experimental.FileAccess.FileAccessData')
    if (`$type -and `$type.GetProperty('EnumeratePattern')) { 'YES' } else { 'NO' }
}
catch { 'NO' }
"@

    return ((& pwsh -NoProfile -Command $probe) -eq 'YES')
}

function New-ScenarioSandbox
{
    param(
        [Parameter(Mandatory = $true)] [string] $ScenarioName
    )

    $sandboxRoot = Join-Path $TestRoot $ScenarioName
    if (Test-Path $sandboxRoot) {
        Remove-Item -Path $sandboxRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $sandboxRoot > $null

    $projectDir = Join-Path $sandboxRoot "src"
    New-MSBuildCacheTestProject `
        -ProjectDir $projectDir `
        -GitUserName "scenario" `
        -GitUserEmail "scenario-test@local"

    $env:NUGET_PACKAGES = Join-Path $sandboxRoot ".nuget"

    return [pscustomobject]@{
        Name        = $ScenarioName
        SandboxRoot = $sandboxRoot
        ProjectDir  = $projectDir
        CacheRoot   = Join-Path $sandboxRoot "MSBuildCache"
        Universe    = (New-Guid).ToString()
        LogRoot     = Join-Path $LogDirectory $ScenarioName
    }
}

function Invoke-ScenarioBuild
{
    param(
        [Parameter(Mandatory = $true)] [pscustomobject] $Sandbox,
        [Parameter(Mandatory = $true)] [string] $Step,
        [Parameter(Mandatory = $false)] [hashtable] $ExtraProperties = @{}
    )

    $stepLogDir = Join-Path $Sandbox.LogRoot $Step
    New-Item -ItemType Directory -Path $stepLogDir -Force > $null

    $lateFileAccessPatterns = @(
        "**\ApplicationInsights.config"
        "$env:LOCALAPPDATA\Microsoft\VSApplicationInsights\**"
        "$env:LOCALAPPDATA\Microsoft\Windows\INetCache\**"
        "$env:SystemRoot\**"
    ) -join ";"

    $properties = @{
        # Skip writing outputs that match the cache — avoids cache-replay overwriting existing
        # outputs from a previous build in the same sandbox.
        MSBuildCacheSkipUnchangedOutputFiles = "true"
        # Windows services and Visual Studio telemetry can access machine-local state after the
        # project completes. These locations cannot enter the PathSet, and matches remain visible
        # through the late-access diagnostic.
        MSBuildCacheAllowFileAccessAfterProjectFinishFilePatterns = $lateFileAccessPatterns
    }

    foreach ($key in $ExtraProperties.Keys) {
        $properties[$key] = $ExtraProperties[$key]
    }

    return Invoke-MSBuildCacheBuild `
        -MSBuildPath $MSBuildPath `
        -ProjectDir $Sandbox.ProjectDir `
        -LogDirectory $stepLogDir `
        -CachePackage $CachePackage `
        -CacheUniverse $Sandbox.Universe `
        -CacheRoot $Sandbox.CacheRoot `
        -ExtraProperties $properties `
        -Context "$($Sandbox.Name) :: $Step"
}

function Reset-OutputDirectories
{
    # Wipes bin\ and obj\ in the project directory so a subsequent build can write fresh
    # outputs without colliding with the cold build's read-only cache materializations.
    # Optionally preserves a list of paths under those directories (e.g., scenario markers).
    param(
        [Parameter(Mandatory = $true)] [string] $ProjectDir,
        [Parameter(Mandatory = $false)] [string[]] $PreservePaths = @()
    )

    $preserved = @{}
    foreach ($relPath in $PreservePaths) {
        $full = Join-Path $ProjectDir $relPath
        if (Test-Path $full) {
            $preserved[$relPath] = Get-Content -Path $full -Raw -ErrorAction SilentlyContinue
        }
    }

    foreach ($subdir in @('bin', 'obj')) {
        $path = Join-Path $ProjectDir $subdir
        if (Test-Path $path) {
            # Cache materializations are read-only; clear that flag before delete.
            Get-ChildItem -Path $path -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                if (-not $_.PSIsContainer) {
                    try { $_.IsReadOnly = $false } catch { }
                }
            }
            Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    foreach ($relPath in $preserved.Keys) {
        $full = Join-Path $ProjectDir $relPath
        $parent = Split-Path -Parent $full
        if (-not (Test-Path $parent)) {
            New-Item -ItemType Directory -Path $parent -Force > $null
        }
        Set-Content -Path $full -Value $preserved[$relPath] -NoNewline
    }
}

function Add-MarkerProbeTarget
{
    # Writes a Directory.Build.targets fragment that probes for a marker file. The probe is
    # via MSBuild's Exists() condition, which goes through Detours and therefore shows up in
    # the sandbox file-access log as an ExistingProbe / AbsentPathProbe. Used by scenarios
    # that exercise the probe channel.
    param(
        [Parameter(Mandatory = $true)] [string] $ProjectDir,
        [Parameter(Mandatory = $true)] [string] $MarkerPath
    )

    $targetsContent = @"
<Project>
    <Target Name="ScenarioProbeMarker" BeforeTargets="CoreCompile">
        <PropertyGroup Condition="Exists('$MarkerPath')">
            <_MarkerProbeFound>true</_MarkerProbeFound>
        </PropertyGroup>
    </Target>
</Project>
"@

    Set-Content -Path (Join-Path $ProjectDir "Directory.Build.targets") -Value $targetsContent -NoNewline
}

function Add-DynamicGlobTarget
{
    # Writes a Directory.Build.targets fragment that adds <Compile> items via a target-time
    # dynamic glob over a subdir under obj\ . Two reasons to put the source under obj\:
    #
    #   1) The SDK's DefaultExcludesInProjectFolder excludes obj\ from `<Compile Include="**/*.cs"/>`,
    #      so the SDK's evaluation-time predictor doesn't see these files. That's the canonical
    #      "MSBuildPrediction didn't predict it" condition we want to exercise.
    #   2) Files added at target-time via `<ItemGroup>` go through MSBuild's glob expander,
    #      which calls Directory.GetFiles — Detoured by reportfileaccesses — so the dynamic-glob
    #      directory shows up as a DirectoryEnumeration entry in the PathSet.
    #
    # The target also explicitly removes any matching items from the SDK glob's evaluation-time
    # set as a defense in depth (in case the SDK glob is ever updated to peek into obj\).
    param(
        [Parameter(Mandatory = $true)] [string] $ProjectDir,
        [Parameter(Mandatory = $true)] [string] $DynamicSubdir
    )

    $targetsContent = @"
<Project>
    <ItemGroup>
        <Compile Remove="$DynamicSubdir\**\*.cs" />
    </ItemGroup>
    <Target Name="ScenarioDynamicGlob" BeforeTargets="CoreCompile">
        <ItemGroup>
            <Compile Include="`$(MSBuildThisFileDirectory)$DynamicSubdir\**\*.cs" />
        </ItemGroup>
    </Target>
</Project>
"@

    Set-Content -Path (Join-Path $ProjectDir "Directory.Build.targets") -Value $targetsContent -NoNewline
}

# ----------------------------------------------------------------------------------------
# Scenarios
# ----------------------------------------------------------------------------------------

$ScenarioFunctions = [ordered]@{}

# Scenario A — Adding a new .cs file matched by a TARGET-TIME dynamic glob must produce a
# cache miss. This is the headline value of the feature. Pre-Phase-3 the cache would hit
# because:
#   - MSBuildPrediction's evaluation-time predictors don't see target-time `<ItemGroup>` adds,
#     so the new file isn't in the predicted-input set (no weak-FP change).
#   - Without probe/enumeration fingerprinting, there's nothing in the cached PathSet that
#     references the dynamic glob's directory, so re-observation doesn't notice the new file.
#
# With probe/enumeration fingerprinting, the target-time glob's `Directory.GetFiles` call is
# Detoured, captured as a DirectoryEnumeration entry on the dynamic\ subdir; adding a file
# changes the member-hash and produces a correct miss.
$ScenarioFunctions['GlobAddCsFile'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'GlobAddCsFile'
    $dynamicRel = 'obj\dynamic'
    $dynamicDir = Join-Path $sandbox.ProjectDir $dynamicRel
    New-Item -ItemType Directory -Path $dynamicDir -Force > $null
    Set-Content -Path (Join-Path $dynamicDir 'Initial.cs') -NoNewline -Value @"
public static class Initial { public static int Value => 1; }
"@

    Add-DynamicGlobTarget -ProjectDir $sandbox.ProjectDir -DynamicSubdir $dynamicRel

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add dynamic glob target + initial source" *> $null
    Pop-Location

    # Build 1: cold cache, dynamic\ contains only Initial.cs.
    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'GlobAddCsFile' -Step 'cold'

    # Mutate: add a new file to the dynamic-glob directory. Reset bin/obj to clear cold's
    # read-only cache outputs, but PRESERVE the dynamic source files (which live under obj\).
    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir -PreservePaths @(
        "$dynamicRel\Initial.cs"
    )
    Set-Content -Path (Join-Path $dynamicDir 'Added.cs') -NoNewline -Value @"
public static class Added { public static int Value => 2; }
"@

    # Build 2: dynamic glob's directory now has 2 files. The cached DirectoryEnumeration entry
    # re-observes against the current FS, finds a different member-hash, produces a different
    # strong FP, and we correctly miss. (Without the feature, this silently HITs — the bug.)
    $afterAdd = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'after-add'
    Assert-CacheStats -Result $afterAdd -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'GlobAddCsFile' -Step 'after-add'
}

# Scenario B — Same source between builds must produce a cache HIT. Regression check that
# the new fingerprinting doesn't false-miss on stable inputs (i.e., that noise-reduction
# filters are doing their job).
$ScenarioFunctions['GlobUnchangedHit'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'GlobUnchangedHit'
    $dynamicRel = 'obj\dynamic'
    $dynamicDir = Join-Path $sandbox.ProjectDir $dynamicRel
    New-Item -ItemType Directory -Path $dynamicDir -Force > $null
    Set-Content -Path (Join-Path $dynamicDir 'Initial.cs') -NoNewline -Value @"
public static class Initial { public static int Value => 1; }
"@

    Add-DynamicGlobTarget -ProjectDir $sandbox.ProjectDir -DynamicSubdir $dynamicRel

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add dynamic glob target" *> $null
    Pop-Location

    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'GlobUnchangedHit' -Step 'cold'

    # Reset outputs but preserve the dynamic source.
    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir -PreservePaths @(
        "$dynamicRel\Initial.cs"
    )

    # No source change; the dynamic-glob directory's member-hash is unchanged; expect HIT.
    $warm = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'warm'
    Assert-CacheStats -Result $warm -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'GlobUnchangedHit' -Step 'warm'
}

# Scenario C — A previously-absent in-scope marker file that now exists must produce a MISS.
# Pins the probe-channel clean→dirty→miss fix: the AbsentPathProbe in the cached PathSet is
# re-observed at lookup, finds the marker present, and the strong fingerprint differs.
#
# We place the marker under obj\ so the SDK's default <None> item glob doesn't pick it up as
# a predicted input. Otherwise the marker would also change the WEAK fingerprint, and the
# scenario wouldn't isolate that the probe-channel fix is what's firing — the test could
# pass for the wrong reason.
$ScenarioFunctions['InScopeMarkerProbeMiss'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'InScopeMarkerProbeMiss'
    $objDir = Join-Path $sandbox.ProjectDir 'obj'
    New-Item -ItemType Directory -Path $objDir -Force > $null
    $markerPath = Join-Path $objDir 'feature-flag.marker'
    Add-MarkerProbeTarget -ProjectDir $sandbox.ProjectDir -MarkerPath $markerPath

    # Re-init git so the new Directory.Build.targets is part of the source-control state.
    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add probe target" *> $null
    Pop-Location

    # Build 1: marker absent. PathSet captures AbsentPathProbe of the marker.
    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'InScopeMarkerProbeMiss' -Step 'cold'

    # Mutate: create the marker (NOT a project output, just an external action).
    New-Item -ItemType Directory -Path $objDir -Force > $null
    Set-Content -Path $markerPath -NoNewline -Value 'on'

    # Don't `git clean` — that would wipe obj\, including our marker. The cache plugin runs
    # before MSBuild's incremental-skip logic, so leftover obj/ contents don't interfere with
    # the cache lookup.

    # Build 2: marker now present. Re-observation should promote AbsentPathProbe → ExistingProbe
    # for the marker path; strong FP differs; expect MISS. This is the clean→dirty→miss fix
    # firing through the probe channel.
    $afterCreate = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'after-create-marker'
    Assert-CacheStats -Result $afterCreate -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'InScopeMarkerProbeMiss' -Step 'after-create-marker'
}

# Scenario D — Marker state stable across builds must HIT. Companion to scenario C: confirms
# the probe channel doesn't false-miss when the probed state is unchanged.
$ScenarioFunctions['InScopeMarkerStableHit'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'InScopeMarkerStableHit'
    $objDir = Join-Path $sandbox.ProjectDir 'obj'
    New-Item -ItemType Directory -Path $objDir -Force > $null
    $markerPath = Join-Path $objDir 'feature-flag.marker'
    Add-MarkerProbeTarget -ProjectDir $sandbox.ProjectDir -MarkerPath $markerPath
    Set-Content -Path $markerPath -NoNewline -Value 'on'

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add probe target" *> $null
    Pop-Location

    # Build 1: marker present. PathSet captures ExistingProbe of the marker.
    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'InScopeMarkerStableHit' -Step 'cold'

    # Don't `git clean` — preserves the marker for the second build.

    # Build 2: marker still present. Re-observation finds ExistingProbe still satisfied; strong
    # FP matches; expect HIT.
    $warm = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'warm'
    Assert-CacheStats -Result $warm -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'InScopeMarkerStableHit' -Step 'warm'
}

# Scenario E — A marker outside the repo + NuGet scope must NOT invalidate the cache when its
# existence flips. This pins the scope-based exclusion: system / out-of-scope state changes
# are ignored. Without scope filtering, the warm cache would miss.
$ScenarioFunctions['OutOfScopeMarkerHit'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'OutOfScopeMarkerHit'
    $oosMarker = Join-Path ([System.IO.Path]::GetTempPath()) "msbuildcache-oos-$([guid]::NewGuid().ToString('N')).marker"
    Add-MarkerProbeTarget -ProjectDir $sandbox.ProjectDir -MarkerPath $oosMarker

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add out-of-scope probe target" *> $null
    Pop-Location

    # Make sure the marker doesn't exist initially.
    if (Test-Path $oosMarker) { Remove-Item -Path $oosMarker -Force }

    try {
        # Build 1: marker absent. PathSet does NOT capture this probe (filtered by scope).
        $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
        Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'OutOfScopeMarkerHit' -Step 'cold'

        # Mutate: create the marker outside repo + NuGet scope.
        Set-Content -Path $oosMarker -NoNewline -Value 'on'

        Push-Location $sandbox.ProjectDir
        & git clean -fdx *> $null
        Pop-Location

        # Build 2: marker now present, but it's out of scope; cache should still HIT.
        $warm = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'after-create-oos-marker'
        Assert-CacheStats -Result $warm -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'OutOfScopeMarkerHit' -Step 'after-create-oos-marker'
    }
    finally {
        if (Test-Path $oosMarker) { Remove-Item -Path $oosMarker -Force -ErrorAction SilentlyContinue }
    }
}

# Scenario F — An in-scope path matched by IgnoredInputPatterns must NOT invalidate the
# cache when its existence flips. Pins the configurable noise filter; the inverse of scenario C.
#
# We place the marker under obj\ so the SDK's default <None Include="**" /> doesn't pick it
# up as a predicted input (the SDK's DefaultExcludesInProjectFolder excludes obj/ and bin/).
# Otherwise the marker would change the WEAK fingerprint regardless of any observation filter.
$ScenarioFunctions['IgnoredObservationHit'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'IgnoredObservationHit'
    $objDir = Join-Path $sandbox.ProjectDir 'obj'
    New-Item -ItemType Directory -Path $objDir -Force > $null
    $markerPath = Join-Path $objDir 'noisy.marker'
    Add-MarkerProbeTarget -ProjectDir $sandbox.ProjectDir -MarkerPath $markerPath

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add probe target" *> $null
    Pop-Location

    $ignorePatterns = @{ MSBuildCacheIgnoredInputPatterns = '**\noisy.marker' }

    # Build 1: marker absent; pattern excludes the probe from the strong fingerprint.
    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold' -ExtraProperties $ignorePatterns
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'IgnoredObservationHit' -Step 'cold'

    # Mutate: create the marker file under obj\ (which the SDK's default item exclusion list
    # ignores, so it doesn't enter the predicted-input set and doesn't change the weak FP).
    New-Item -ItemType Directory -Path $objDir -Force > $null
    Set-Content -Path $markerPath -NoNewline -Value 'on'

    # Don't `git clean` — that would wipe obj\, including our marker. Just rebuild.

    # Build 2: marker now present, but the probe is filtered by IgnoredInputPatterns;
    # strong FP unchanged; cache HIT.
    $warm = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'after-create-marker' -ExtraProperties $ignorePatterns
    Assert-CacheStats -Result $warm -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'IgnoredObservationHit' -Step 'after-create-marker'
}

function Add-EnumerateOutputDirTarget
{
    # Writes a Directory.Build.targets fragment that:
    #   1) Writes a generated file into an existing directory before CoreCompile.
    #   2) Causes MSBuild to enumerate that directory via a glob, so the target
    #      produces a self-output in the enumerated directory.
    #
    # Together these reproduce the canonical "project enumerates a directory it also writes
    # into" pattern (e.g., a CI build that copies its outputs to a staging dir and then
    # enumerates the dir to assemble a manifest). Without the schema-driven self-output filter,
    # the populate-time member hash includes the generated file, and a clean rebuild's
    # re-observation would see an empty directory and produce a false miss.
    #
    # The scenario keeps a tracked member in obj\generated\ so the directory exists in clean
    # state while remaining outside the SDK's default item globs.
    param(
        [Parameter(Mandatory = $true)] [string] $ProjectDir,
        [Parameter(Mandatory = $true)] [string] $EnumeratedSubdir
    )

    $targetsContent = @"
<Project>
    <Target Name="ScenarioGenerateAndEnumerate" BeforeTargets="CoreCompile">
        <MakeDir Directories="`$(MSBuildThisFileDirectory)$EnumeratedSubdir" />
        <WriteLinesToFile
            File="`$(MSBuildThisFileDirectory)$EnumeratedSubdir\Generated.gen"
            Lines="generated-`$([System.DateTime]::UtcNow.Ticks)"
            Overwrite="true" />
        <ItemGroup>
            <_GeneratedDirContents Include="`$(MSBuildThisFileDirectory)$EnumeratedSubdir\**\*" />
            <_GeneratedDirCount Include="@(_GeneratedDirContents->Count())" />
        </ItemGroup>
    </Target>
</Project>
"@

    Set-Content -Path (Join-Path $ProjectDir "Directory.Build.targets") -Value $targetsContent -NoNewline
}

# Scenario G — The headline self-output enumeration cycle. A project enumerates a directory it
# also writes into. Build clean (cache populates with a partitioned member list); rebuild after
# git clean (only the tracked external member remains); the cache must HIT because the only
# removed member is the project's own output, which the schema cancels out.
#
# Without the schema-driven self-output filter, the populate-time member hash would include
# the generated file, the lookup-time re-observation would see only the tracked member, and
# the cache would falsely miss — even though the project's external view is unchanged.
$ScenarioFunctions['EnumerateOutputDirCycleHit'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'EnumerateOutputDirCycleHit'
    $enumeratedRel = 'obj\generated'
    $enumeratedDir = Join-Path $sandbox.ProjectDir $enumeratedRel
    $trackedMember = Join-Path $enumeratedDir 'Tracked.input'

    New-Item -ItemType Directory -Path $enumeratedDir -Force > $null
    Set-Content -Path $trackedMember -NoNewline -Value 'tracked external member'

    Add-EnumerateOutputDirTarget -ProjectDir $sandbox.ProjectDir -EnumeratedSubdir $enumeratedRel

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git add -f $trackedMember *> $null
    & git commit -m "add enumerate-output-dir target" *> $null
    Pop-Location

    # Build 1: cold. Project writes Generated.gen and enumerates the existing directory.
    # End-of-build: PathSet captures Members=[Tracked.input] (external) and
    # WrittenMembers=[Generated.gen] (the project's self-output).
    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'EnumerateOutputDirCycleHit' -Step 'cold'

    # Build 2: clean state. git clean removes Generated.gen but preserves the tracked member,
    # so the directory remains enumerable:
    #   - cached.Members = [Tracked.input], cached.WrittenMembers = [Generated.gen]
    #   - lookup re-enumerates obj\generated\ → currentMembers = [Tracked.input]
    #   - effective = [Tracked.input] - [Generated.gen] = [Tracked.input]
    #   - matches cached.Members → HIT ✓
    Push-Location $sandbox.ProjectDir
    & git clean -fdx *> $null
    Pop-Location

    $cleanRebuild = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'clean-rebuild'
    Assert-CacheStats -Result $cleanRebuild -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'EnumerateOutputDirCycleHit' -Step 'clean-rebuild'

    # Build 3: incremental rebuild (no clean, leftover obj\ contents from cache replay).
    # Re-enumeration finds Tracked.input and Generated.gen (replayed from cache); subtract
    # cached.WrittenMembers → [Tracked.input];
    # matches cached.Members → HIT.
    $incrementalRebuild = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'incremental-rebuild'
    Assert-CacheStats -Result $incrementalRebuild -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'EnumerateOutputDirCycleHit' -Step 'incremental-rebuild'
}

# Scenario H — Editing an existing source file must MISS, and rebuilding with that edit in
# place must then HIT. This is the most basic correctness contract of the cache and is
# deliberately independent of the probe/enumeration channels: the edited file is a predicted
# input, so the WEAK fingerprint changes. It is here as a control — if this ever fails, the
# failure is in the cache fundamentals rather than in this feature, which makes triaging the
# other scenarios much faster.
$ScenarioFunctions['SourceFileChangeMiss'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'SourceFileChangeMiss'
    $programPath = Join-Path $sandbox.ProjectDir 'Program.cs'

    $cold = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'cold'
    Assert-CacheStats -Result $cold -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'SourceFileChangeMiss' -Step 'cold'

    # Edit an existing, predicted source file.
    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir
    Add-Content -Path $programPath -Value @"

public static class ScenarioEdit { public static int Value => 42; }
"@

    $afterEdit = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'after-edit'
    Assert-CacheStats -Result $afterEdit -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'SourceFileChangeMiss' -Step 'after-edit'

    # Same content as the previous build — the edit is now the cached state, so expect a HIT.
    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir
    $rebuild = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'rebuild-after-edit'
    Assert-CacheStats -Result $rebuild -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'SourceFileChangeMiss' -Step 'rebuild-after-edit'
}

# Scenario I — Three consecutive builds with no source change: the first populates, and both
# subsequent builds must be 100% hits. The third build is the one that matters. A feature that
# records an observation whose re-validation depends on state the *replay* itself produces can
# still pass a two-build test — build 2 runs against the populate build's leftovers — and then
# fail on build 3, which runs against cache-replayed outputs instead. This pins that the
# observation set is stable across repeated replay rather than merely correct once.
$ScenarioFunctions['ThirdBuildAllHits'] = {
    $sandbox = New-ScenarioSandbox -ScenarioName 'ThirdBuildAllHits'
    $dynamicRel = 'obj\dynamic'
    $dynamicDir = Join-Path $sandbox.ProjectDir $dynamicRel
    New-Item -ItemType Directory -Path $dynamicDir -Force > $null
    Set-Content -Path (Join-Path $dynamicDir 'Initial.cs') -NoNewline -Value @"
public static class Initial { public static int Value => 1; }
"@

    # Include the enumeration and probe channels so all three observation types are exercised
    # across the repeated replays, not just file content reads.
    Add-DynamicGlobTarget -ProjectDir $sandbox.ProjectDir -DynamicSubdir $dynamicRel

    Push-Location $sandbox.ProjectDir
    & git add . *> $null
    & git commit -m "add dynamic glob target" *> $null
    Pop-Location

    $first = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'first'
    Assert-CacheStats -Result $first -ExpectedHits 0 -ExpectedMisses 1 -ScenarioName 'ThirdBuildAllHits' -Step 'first'

    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir -PreservePaths @("$dynamicRel\Initial.cs")
    $second = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'second'
    Assert-CacheStats -Result $second -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'ThirdBuildAllHits' -Step 'second'

    # Build 3 replays over build 2's replayed outputs. Any observation that is only stable
    # against a real build's outputs, rather than a replayed one, fails here.
    Reset-OutputDirectories -ProjectDir $sandbox.ProjectDir -PreservePaths @("$dynamicRel\Initial.cs")
    $third = Invoke-ScenarioBuild -Sandbox $sandbox -Step 'third'
    Assert-CacheStats -Result $third -ExpectedHits 1 -ExpectedMisses 0 -ScenarioName 'ThirdBuildAllHits' -Step 'third'
}

# ----------------------------------------------------------------------------------------
# Run scenarios
# ----------------------------------------------------------------------------------------

# Wrap in @() so a single -Scenarios value stays an array; PowerShell unrolls a one-element
# array returned from `if` to a scalar, and $toRun.Count would then fail under StrictMode.
$toRun = @(if ($Scenarios -and $Scenarios.Count -gt 0) { $Scenarios } else { $ScenarioFunctions.Keys })
$failed = @()

if (-not (Test-EnumerationCapability -MSBuildPath $MSBuildPath)) {
    $message = "MSBuild at '$MSBuildPath' does not report FileAccessData.EnumeratePattern, so probe " +
        "and enumeration fingerprinting self-disables and these $($toRun.Count) scenarios cannot pass."

    if ($RequireEnumerationCapability) {
        # This caller supplies an MSBuild that is supposed to support the feature, so a missing
        # capability means the setup is broken rather than the MSBuild being old. Skipping here would
        # silently drop all end-to-end coverage.
        Write-Host "##vso[task.logissue type=error]$message Expected this MSBuild to support it."
        Write-Host "$message Expected this MSBuild to support it."
        exit 1
    }

    # Otherwise skip rather than fail: released MSBuild doesn't carry the field yet, and failing would
    # block every PR on something no PR can fix. Raised as a pipeline log issue so the skip surfaces in
    # the build summary instead of being buried in log output.
    $message += " SKIPPING. Re-run against an MSBuild that contains the field to actually exercise them."
    Write-Host "##vso[task.logissue type=warning]$message"
    Write-Warning $message
    exit 0
}

foreach ($name in $toRun) {
    if (-not $ScenarioFunctions.Contains($name)) {
        Write-Host "Unknown scenario: $name. Available: $($ScenarioFunctions.Keys -join ', ')"
        $failed += $name
        continue
    }

    Write-Host ""
    Write-Host "================================================================"
    Write-Host "Scenario: $name"
    Write-Host "================================================================"

    try {
        & $ScenarioFunctions[$name]
        Write-Host "[ OK ] $name"
    }
    catch {
        Write-Host "[FAIL] $name -- $($_.Exception.Message)"
        $failed += $name
    }
}

Write-Host ""
Write-Host "================================================================"
if ($failed.Count -eq 0) {
    Write-Host "All $($toRun.Count) scenarios passed."
    exit 0
}
else {
    Write-Host "$($failed.Count) of $($toRun.Count) scenarios failed:"
    foreach ($name in $failed) { Write-Host "  - $name" }
    exit 1
}
