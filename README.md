# Microsoft.MSBuildCache

The overarching effort for MSBuildCache is an evolution of the [MSBuild Project Cache](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md) to enable the "cache add" scenarios. MSBuildCache is a plugin implementation which takes advantage of this.

## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.


### Setup

It is assumed that you are using VS v17.8.0-pre.3.0 or later.

### Building

#### Using a custom MSBuild (optional)

Clone the [MSBuild repo](https://github.com/dotnet/msbuild) as a sibling of the MSBuildCache repo.

Build the msbuild repo:
```
..\msbuild\build.cmd /p:CreateBootstrap=true /p:Configuration=Release
```
Note, MSBuild only needs to built once each time you update MSBuild, not every time you want to build MSBuildCache.

The path to MSBuild is: `..\msbuild\artifacts\bin\bootstrap\net472\MSBuild\Current\Bin\amd64\MSBuild.exe`.

#### Building MSBuildCache

You can build `MSBuildCache.sln` and/or open it in VS.

### Testing

#### Running

Ensure you've built MSBuildCache as described above.

Now add the following in the repo you're testing with in their `NuGet.config`

```xml
<add key="MSBuildCache" value="..\MSBuildCache\artifacts\Debug\Microsoft.MSBuildCache" />
```

The path may need to be adjusted based on the repo and where you have MSBuildCache cloned.

Finally add a `PackageReference` to MSBuildCache to the repo you're testing with version `*-*` to ensure the latest gets picked up. This may look different from repo to repo but here is an example:

```xml
<PackageReference Include="Microsoft.MSBuildCache" VersionOverride="*-*" IncludeAssets="Analyzers;Build;BuildMultitargeting;BuildTransitive" PrivateAssets="All" />
```

**NOTE!** Because you're using a locally built package, you may need to clear it from your package cache after each iteration via a command like `rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache`. Additionally, to ensure you're not using the head version of the package, you may need to create a branch and a dummy commit locally to ensure the version is higher.

**NOTE!** MSBuildCache currently does not handle incremental builds well! The current target scenario is for CI environments, so **it's expected that the repo is always clean before building**. The main reason for this gap is the same as QuickBuild: file probes and directory enumerations are not considered.

To enable file reporting via detours in MSBuild, ensure `/graph` and `/reportfileaccesses` are used. The latter is new as of these unmerged MSBuild changes.

Example of a set of commands to test MSBuildCache e2e in some repo:

```
rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache
git clean -fdx
restore
<path-to-msbuild> /graph /restore:false /m /nr:false /reportfileaccesses
```

Example of a set of commands to test MSBuildCache e2e in a subdirectory of some repo:

```
rmdir /S /Q %USERPROFILE%\.nuget\packages\Microsoft.MSBuildCache
pushd <repo-root>
git clean -fdx
popd
restore
<path-to-msbuild> /graph /restore:false /m /nr:false /reportfileaccesses
```

#### Settings

There is a built-in mechanism to passing settings to a plugin, which is to add metadata to the `ProjectCachePlugin` item. This is then exposed to the plugin via the `CacheContext` during initialialization.

To add additional setting, add it to `PluginSettings` and `src\MSBuildCache\build\MicrosoftMSBuildCache.targets`. For naming convensions, follow what you see but the gist is that the property name should be the setting name prefixed by "MSBuildCache".

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.