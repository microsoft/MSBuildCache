# Contributing

This project welcomes contributions and suggestions. Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information, see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

For design context, see [fingerprinting](docs/fingerprinting.md) and [MSBuild hosting and dependencies](docs/dependencies.md).

## Building

### Building MSBuildCache

You can build `MSBuildCache.sln` and/or open it in VS.

### Using a custom MSBuild (optional)

Clone the [MSBuild repo](https://github.com/dotnet/msbuild) adjacent to the MSBuildCache repo.

The remaining steps assume the `cwd` is the root of the MSBuildCache repo. You may need to adjust the paths if this is not the case.

Build the msbuild repo:
```
..\msbuild\build.cmd /p:CreateBootstrap=true /p:Configuration=Release
```
Note, MSBuild only needs to be built every time you update MSBuild, not every time you want to build MSBuildCache.

The path to MSBuild is: `..\msbuild\artifacts\bin\bootstrap\net472\MSBuild\Current\Bin\amd64\MSBuild.exe`.

To also compile MSBuildCache against custom MSBuild assemblies, set `MSBuildLibraries` to their directory. This changes compile references; invoke the custom MSBuild executable explicitly when testing the plugin.

Note: when using a locally built MSBuild, many scenario may not work properly, for example C++ builds.

## Testing

### Running

Ensure you've built MSBuildCache as described above.

Now add the following package source to your test repo's `NuGet.config`:

```xml
<add key="MSBuildCache" value="..\MSBuildCache\artifacts\Debug\Microsoft.MSBuildCache" />
```

You may need to adjust the above path based on the paths to your test repo and MSBuildCache.

Finally, add a `PackageReference` to MSBuildCache to your test repo with version `*-*` to use the latest MSBuildCache version. This may look different from repo to repo, but here is an example:

```xml
<PackageReference Include="Microsoft.MSBuildCache" VersionOverride="*-*" IncludeAssets="Analyzers;Build;BuildMultitargeting;BuildTransitive" PrivateAssets="All" />
```

**NOTE!** Because you're using a locally built package, you may need to clear it from your package cache after each iteration via a command like `rmdir /S /Q %NUGET_PACKAGES%\Microsoft.MSBuildCache` (if you set `%NUGET_PACKAGES%`) or `rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache` if you're using the dfault package cache location. Additionally, to ensure you're not using the head version of the package, you may need to create a branch and dummy commit locally to ensure the version is higher.

To enable file reporting via detours in MSBuild, ensure `/graph` and `/reportfileaccesses` are used.

Example of a set of commands to test MSBuildCache e2e in some repo:

```
rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache
git clean -fdx
<path-to-msbuild> /restore /graph /m /nr:false /reportfileaccesses
```

Example of a set of commands to test MSBuildCache e2e in a subdirectory of some repo:

```
rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache
pushd <repo-root>
git clean -fdx
popd
<path-to-msbuild> /restore /graph /m /nr:false /reportfileaccesses
```

### Build-level scenarios

After building the local packages, run the existing build-level scenarios from the repository root in PowerShell 7 (`pwsh`):

```powershell
.\tests\scenarios.ps1 -MSBuildPath 'C:\path\to\MSBuild.exe' `
    -Configuration Debug -RequireEnumerationCapability $true
```

Use a Windows x64 MSBuild host that exposes `FileAccessData.EnumeratePattern`; see the [custom MSBuild instructions](#using-a-custom-msbuild-optional). `-RequireEnumerationCapability $true` makes a missing capability fail rather than skip. Without it, unsupported hosts skip the scenarios with a warning.

Use `-Scenarios` to select individual cases, or omit it to run the full suite. `-LocalPackageDir` selects the local packages, and `-TestRoot` and `-LogDirectory` select the scenario and log locations. The script recreates logs and scenario sandboxes, so use only disposable locations.

### Settings

There is a built-in mechanism to passing settings to a plugin, which is to add metadata to the `ProjectCachePlugin` item. This is then exposed to the plugin via the `CacheContext` during initialialization.

To add additional settings, add it to [`PluginSettings`](src\Common\PluginSettings.cs) and [`Microsoft.MSBuildCache.Common.targets`](src\Common\build\Microsoft.MSBuildCache.Common.targets). By convention the MSBuild property name should be the setting name prefixed by "MSBuildCache".
