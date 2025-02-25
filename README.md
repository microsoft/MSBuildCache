# Microsoft.MSBuildCache
[![Build Status](https://dev.azure.com/msbuildcache/public/_apis/build/status%2FMicrosoft.MSBuildCache?repoName=microsoft%2FMSBuildCache&branchName=main)](https://dev.azure.com/msbuildcache/public/_build/latest?definitionId=1&repoName=microsoft%2FMSBuildCache&branchName=main)

This project provides plugin implementations for the experimental [MSBuild Project Cache](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md), which enables project-level caching within MSBuild.

> [!IMPORTANT]
> Currently MSBuildCache assumes that the build is running in a clean repo. Incremental builds, e.g. local developer builds, are not supported. Target scenarios include PR builds and CI builds.

## Usage

This feature requires Visual Studio 17.9 or later.

To enable caching, simply add a `<PackageReference>` for the desired cache implementation and set various properties to configure it.

For repos which build C# code, add a `<PackageReference>` to `Microsoft.MSBuildCache.SharedCompilation` for shared compilation support.

Here is an example if you're using NuGet's [Central Package Management](https://learn.microsoft.com/en-us/nuget/consume-packages/Central-Package-Management) and Azure Pipelines:

`Directory.Packages.props`:
```xml
  <PropertyGroup>
    <!-- In Azure pipelines, use Pipeline Caching as the cache storage backend. Otherwise, use the local cache. -->
    <MSBuildCachePackageName Condition="'$(TF_BUILD)' != ''">Microsoft.MSBuildCache.AzurePipelines</MSBuildCachePackageName>
    <MSBuildCachePackageName Condition="'$(MSBuildCachePackageName)' == ''">Microsoft.MSBuildCache.Local</MSBuildCachePackageName>

    <!-- Replace this with the latest version -->
    <MSBuildCachePackageVersion>...</MSBuildCachePackageVersion>
  </PropertyGroup>
  <ItemGroup>
    <GlobalPackageReference Include="$(MSBuildCachePackageName)" Version="$(MSBuildCachePackageVersion)" />
    <GlobalPackageReference Include="Microsoft.MSBuildCache.SharedCompilation" Version="$(MSBuildCachePackageVersion)" />
  </ItemGroup>
```

For repos using C++, you will need to add the projects to a packages.config and import the props/targets files directly.

`Directory.Build.props`:
```xml
  <PropertyGroup>
    <PackagesConfigFile>packages.config</PackagesConfigFile>
    <PackagesConfigContents>$([System.IO.File]::ReadAllText("$(PackagesConfigFile)"))</PackagesConfigContents>
    <MSBuildCachePackageVersion>$([System.Text.RegularExpressions.Regex]::Match($(PackagesConfigContents), 'Microsoft.MSBuildCache.*?version="(.*?)"').Groups[1].Value)</MSBuildCachePackageVersion>
    <MSBuildCachePackageRoot>$(NugetPackageDirectory)\$(MSBuildCachePackageName).$(MSBuildCachePackageVersion)</MSBuildCachePackageRoot>
  </PropertyGroup>
  <Import Project="$(MSBuildCachePackageRoot)\build\$(MSBuildCachePackageName).props" />
```

`Directory.Build.targets`:
```xml
  <Import Project="$(MSBuildCachePackageRoot)\build\$(MSBuildCachePackageName).targets" />
```

### Common Settings

These settings are common across all plugins, although different implementations may ignore or handle them differently. Refer to the specific plugin documentation for details.

| MSBuild Property Name | Setting Type | Default value | Description |
| ------------- | ------------ | ------------- | ----------- |
| `$(MSBuildCacheLogDirectory)` | `string` | "MSBuildCacheLogs" | Base directory to use for logging. If a relative path, it's assumed relative to the repo root. |
| `$(MSBuildCacheCacheUniverse)` | `string` | "default" | The cache universe is used to isolate the cache. This can be used to bust the cache, or to isolate some types of builds from other types. |
| `$(MSBuildCacheMaxConcurrentCacheContentOperations)` | `int` | 64 | The maximum number of cache operations to perform concurrently |
| `$(MSBuildCacheLocalCacheRootPath)` | `string` | "\MSBuildCache" (in repo's drive root) | Base directory to use for the local cache. |
| `$(MSBuildCacheLocalCacheSizeInMegabytes)` | `int` | 102400 (100 GB) | The maximum size in megabytes of the local cache |
| `$(MSBuildCacheIgnoredInputPatterns)` | `Glob[]` |  | Files which are part of the repo which should be ignored for cache invalidation |
| `$(MSBuildCacheIgnoredOutputPatterns)` | `Glob[]` | `*.assets.cache; *assemblyreference.cache` | Files to ignore for output collection into the cache. Note that if output are ignored, the replayed cache entry will not have these files. This should be used for intermediate outputs which are not properly portable |
| `$(MSBuildCacheIdenticalDuplicateOutputPatterns)` | `Glob[]` | | Files to allow duplicate outputs, with identical content, across projects. Projects which produce files at the same path with differing content will still produce an error. Note: this setting should be used sparingly as it impacts performance |
| `$(MSBuildCacheRemoteCacheIsReadOnly)` | `bool` | false | Whether the remote cache is read-only. This can be useful for scenarios where the remote cache should only be read from but not produced to. |
| `$(MSBuildCacheAsyncCachePublishing)` | `bool` | true | Whether files are published asynchronously to the cache as opposed to delaying the completion signal to MSBuild until publishing is complete. Note: Publishing will be awaited before the overall build completes. |
| `$(MSBuildCacheAsyncCacheMaterialization)` | `bool` | true | Whether files are materialized on disk asynchronously from the cache as opposed to delaying the completion signal to MSBuild until publishing is complete. Note: Materialization will be awaited when a depending project requires the files and before the overall build completes. |
| `$(MSBuildCacheAllowFileAccessAfterProjectFinishProcessPatterns)` | `Glob[]` | `\**\vctip.exe` | Processes to allow file accesses after the project which launched it completes, ie accesses by a detached process. Note: these accesses will not be considered for caching. |
| `$(MSBuildCacheAllowFileAccessAfterProjectFinishFilePatterns)` | `Glob[]` | | Files to allow to be accessed by a process launched by a project after the project completes, ie accesses by a detached process. Note: these accesses will not be considered for caching. |
| `$(MSBuildCacheAllowProcessCloseAfterProjectFinishProcessPatterns)` | `Glob[]` | `\**\mspdbsrv.exe` | Processes to allow to exit after the project which launched it completes, ie detached processes. |
| `$(MSBuildCacheGlobalPropertiesToIgnore)` | `string[]` | `CurrentSolutionConfigurationContents; ShouldUnsetParentConfigurationAndPlatform; BuildingInsideVisualStudio; BuildingSolutionFile; SolutionDir; SolutionExt; SolutionFileName; SolutionName; SolutionPath; _MSDeployUserAgent`, as well as all proeprties related to plugin settings | The list of global properties to exclude from consideration by the cache |
| `$(MSBuildCacheGetResultsForUnqueriedDependencies)` | `bool` | false | Whether to try and query the cache for dependencies if they have not previously been requested. This option can help in cases where the build isn't done in graph order, or if some projects are skipped. |
| `$(MSBuildCacheTargetsToIgnore)` | `string[]` | `GetTargetFrameworks;GetNativeManifest;GetCopyToOutputDirectoryItems;GetTargetFrameworksWithPlatformForSingleTargetFramework` | The list of targets to ignore when determining if a build request matches a cache entry. This is intended for "information gathering" targets which do not have side-effect. eg. a build with `/t:Build` and `/t:Build;GetTargetFrameworks` should be considered to have equivalent results. Note: This only works "one-way" in that the build request is allowed to have missing targets, while the cache entry is not. This is to avoid a situation where a build request recieves a cache hit with missing target results, where a cache hit with extra target results is acceptable. |
| `$(MSBuildCacheSkipUnchangedOutputFiles)` | `bool` | false | Whether to avoid writing output files on cache hit if the file is unchanged, which can improve performance for incremental builds. A file is considered unchanged if it exists, the previously placed file and file to be placed have the same hash, and the the previously placed file and current file on disk have the same timestamp and file size. |
| `$(MSBuildCacheIgnoreDotNetSdkPatchVersion)` | `bool` | false | Whether to ignore the patch version when doing cache lookups. This trades off some correctness for the sake of getting cache hits when the SDK version isn't exactly the same. The default behavior is to consider the exact SDK version, eg. "8.0.404". With this setting set to true, it will instead use something like "8.0.4XX". Note that the major version, minor version, and feature bands are still considered. |

When configuring settings which are list types, you should always append to the existing value to avoid overriding the defaults:

```xml
<PropertyGroup>
  <MSBuildCacheIdenticalDuplicateOutputPatterns>$(MSBuildCacheIdenticalDuplicateOutputPatterns);**\foo.txt</MSBuildCacheIdenticalDuplicateOutputPatterns>
</PropertyGroup>
```

### Execution cached builds

Once configured, to execute builds with caching, simply run:

```
msbuild /graph /m /reportfileaccesses
```

It's also recommended to set some parameters like `/p:MSBuildCacheLogDirectory=$(LogDirectory)\MSBuildCache` as part of the MSBuild call, as opposed to with MSBuild properties in your `Directory.Build.props` for example.

### Caching test execution

Arbitrary MSBuild targets can be cached, and with [`Microsoft.Build.RunVSTest`](https://github.com/microsoft/MSBuildSdks/tree/main/src/RunTests), you can attach running vstest-based unit tests with the "Test" target.

Once `Microsoft.Build.RunVSTest`, or some other target hooked to the Test target, you can get cache hits for tests by adding (`/t:Build;Test`), eg:

```
msbuild /graph /m /reportfileaccesses /t:Build;Test
```

This not only provides the benefits of caching unit test execution, but also executes tests concurrently with other, unrelated, projects in the graph.

## Plugins

### Microsoft.MSBuildCache.AzurePipelines
[![NuGet Version](https://img.shields.io/nuget/v/Microsoft.MSBuildCache.AzurePipelines.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.AzurePipelines)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Microsoft.MSBuildCache.AzurePipelines.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.AzurePipelines)

This implementation uses [Azure Pipeline Caching](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/caching?view=azure-devops) as the cache storage, which is ideal for repos using [Azure Pipelines](https://learn.microsoft.com/en-us/azure/devops/pipelines) for their builds.

Please refer to the [cache isolation and security](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/caching?view=azure-devops#cache-isolation-and-security) pipeline caching provides to understand the security around this cache implementation.

It is expected that this plugin is used within an Azure Pipeline. The `SYSTEM_ACCESSTOKEN` environment variable will need to be explicitly passed to the task calling MSBuild since it's considered a secret. In classic pipelines this is done by checking "Allow scripts to access the OAuth token" in the job. In yml pipelines, this is done by passing the env var explicitly using the `$(System.AccessToken)` variable:

```yml
- script: MSBuild /graph /restore /reportfileaccesses /bl:$(Build.ArtifactStagingDirectory)\Logs\msbuild.binlog /p:Configuration=Release
  displayName: Build
  env:
    SYSTEM_ACCESSTOKEN: $(System.AccessToken)
```

`$(System.AccessToken)` by default does not have the scope relevant to Pipeline Caching. The scope can be enabled by setting the `EnablePipelineCache` pipeline variable. Additionally, the scope will get automatically added if the [Cache task](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/caching?view=azure-devops#cache-task-how-it-works) is used somewhere in the pipeline.

### Microsoft.MSBuildCache.Local
[![NuGet Version](https://img.shields.io/nuget/v/Microsoft.MSBuildCache.Local.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.Local)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Microsoft.MSBuildCache.Local.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.Local)

This implementation uses the local file system for the cache. In particular, it uses [BuildXL](https://github.com/microsoft/BuildXL)'s `LocalCache`. This is recommended for locally testing and debugging caching for your repo. This implementation can also be useful if you have stateful build agents where the cache can be reused across builds.

### Microsoft.MSBuildCache.AzureBlobStorage
[![NuGet Version](https://img.shields.io/nuget/v/Microsoft.MSBuildCache.AzureBlobStorage.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.AzureBlobStorage)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Microsoft.MSBuildCache.AzureBlobStorage.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.AzureBlobStorage)

This implementation uses [Azure Blob Storage](https://azure.microsoft.com/en-us/products/storage/blobs/) as the cache storage.

> [!WARNING]
> This implementation does not yet have a robust security model. All builds using this will need write access to the storage resource, so for example an external contributor could send a PR which would write/overwrite arbitrary content which could then be used by CI builds. Builds using this plugin must be restricted to trusted team members. Use at your own risk.

These settings are available in addition to the [Common Settings](#common-settings):

| MSBuild Property Name | Setting Type | Default value | Description |
| ------------- | ------------ | ------------- | ----------- |
| `$(MSBuildCacheBlobUri)` | `Uri` | | Specifies the uri of the Azure Storage Blob. |
| `$(MSBuildCacheManagedIdentityClientId)` | `string` | | Specifies the managed identity client id when using the "ManagedIdentity" credential type |
| `$(MSBuildCacheAllowInteractiveAuth)` | `bool` | true, except when running in Azure Pipelines or GitHub Actions | Whether interactive auth is allowed |
| `$(MSBuildCacheInteractiveAuthTokenDirectory)` | `string` | "%LOCALAPPDATA%\MSBuildCache\AuthTokenCache" | Specifies a token cache directory when using the "Interactive" credential type |
| `$(MSBuildCacheBuildCacheConfigurationFile)` | `string` | | The path to the 1ES Build Cache configuration file when. This scenario is typically when running in a 1ES Hosted Pool |
| `$(MSBuildCacheBuildCacheResourceId)` | `string` | | The 1ES Build Cache resource ID. This scenario is typically when consuming 1ES Build Cache from a developer machine. |

Either `$(MSBuildCacheBlobUri)`, `$(MSBuildCacheBuildCacheConfigurationFile)`, or `$(MSBuildCacheBuildCacheResourceId)` must be provided.

The various methods of authenticating are as follows and attempted in priority order:
1. If `$(MSBuildCacheBuildCacheConfigurationFile)` is set, it will be used for both the blob endpoints and authentication.
2. If `$(MSBuildCacheBuildCacheResourceId)` is set, an Azure credential will be used to authenticate to the resource (see below).
3. If the `MSBCACHE_CONNECTIONSTRING` environment variable is set, its value will be used as a connection string to connect. This connection string needs both read and write access to the resource. This is not recommended as it is difficult to propertly secure and should only be used for prototyping.
4. If `$(MSBuildCacheBlobUri)` is set, an Azure credential will be used to authenticate to the blob (see below).

In the cases where an Azure credential is acquired, the following methods will be used:
1. If the `MSBCACHE_ACCESSTOKEN` environment variable is set, its value will be used directly.
2. If a [`TokenCredential`](https://learn.microsoft.com/en-us/dotnet/api/azure.core.tokencredential?view=azure-dotnet) is provided directly in the plugin's constructor, it will be used. This only applies when using the programmatic project cache API.
3. If `$(MSBuildCacheManagedIdentityClientId)` is set, it will be used as a user-assigned [managed identity](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview). This is recommended for non-interactive scenarios.
4. If `$(MSBuildCacheAllowInteractiveAuth)` is true, credentials will be obtained interactively. This is recommended for developer scenarios.

## Other Packages

### Microsoft.MSBuildCache.SharedCompilation
[![NuGet Version](https://img.shields.io/nuget/v/Microsoft.MSBuildCache.SharedCompilation.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.SharedCompilation)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Microsoft.MSBuildCache.SharedCompilation.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.SharedCompilation)

This package enables accurate file access reporting for the Roslyn [Compiler Server](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md). Because the compiler server (`vbcscompiler`) launches as a detached process, its file accesses are not observed by MSBuild. This package manually reports these files accesses to the plugin.

In the future, this feature will be directly supported by Roslyn, at which point this package will no longer be needed.

### Microsoft.MSBuildCache.Common
[![NuGet Version](https://img.shields.io/nuget/v/Microsoft.MSBuildCache.Common.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.Common)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Microsoft.MSBuildCache.Common.svg)](https://www.nuget.org/packages/Microsoft.MSBuildCache.Common)

This package contains much of the shared logic for the plugins, including handling file accesses and cache fingerprinting. For those wanting to use a different cache backend than the ones associated with the available plugins, referencing this package can be a good option to avoid having to duplicate the rest of the logic involved with caching.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md)

## License

Microsoft.MSBuildCache is licensed under the [MIT license](LICENSE).

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
