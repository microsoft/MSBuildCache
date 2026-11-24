# MSBuild hosting and dependencies

MSBuildCache runs inside the MSBuild process as a [project-cache plugin](https://github.com/dotnet/msbuild/blob/main/documentation/specs/project-cache.md). The host supplies the MSBuild assemblies used at runtime.

See the [README](../README.md#usage) for user setup, [CONTRIBUTING](../CONTRIBUTING.md#building) for builds, and [fingerprinting](fingerprinting.md) for the observation contract.

## Compile-time references and runtime host

The [MSBuild references](../build/MSBuildReference.targets) used at compile time define the APIs the plugin can reference directly. At runtime, it uses the host's MSBuild assemblies, which may be a different version. The compile-time API surface and the capabilities of the running host are therefore separate concerns.

This allows MSBuildCache to compile against an older API surface while taking advantage of newer host capabilities where available. Choosing a custom compile reference and choosing the executable that hosts the plugin are independent; see [Using a custom MSBuild](../CONTRIBUTING.md#using-a-custom-msbuild-optional).

File-access data originates in BuildXL's reporting and reaches the plugin through MSBuild's public `FileAccessData` API. A field available inside BuildXL is useful to MSBuildCache only if the host MSBuild exposes it through that API.

## Dependency bundling and assembly loading

In Release builds, [MSBuildExtensionPackage.targets](../build/MSBuildExtensionPackage.targets) uses ILRepack to merge managed dependencies into each extension DLL. References to merged types then resolve within that DLL, reducing conflicts with dependency versions loaded by MSBuild or other extensions. Debug builds leave those dependencies as separate assemblies.

Assemblies defining types shared between MSBuild and the plugin, including MSBuild itself and `System.Collections.Immutable`, are deliberately excluded from the merge. A type's identity includes its defining assembly. Merging these types into the plugin would give it its own copies that are incompatible with the host's types, even though their names match.

Native libraries, such as RocksDB, cannot be merged and remain separate files in the package.

When normal assembly resolution needs help, the plugin's [fallback resolver](../src/Common/MSBuildCachePluginBase.cs) prefers assemblies already loaded by or available beside the host before looking beside the plugin. This keeps shared dependencies consistent with MSBuild while still allowing private dependencies to travel with the plugin.

## Host capability detection

Optional functionality is gated by the APIs the running host actually provides, rather than an assumed minimum version. This also accommodates capabilities backported to older host versions.

Capability probing checks for the required members and signatures using reflection. Newer APIs can then be accessed without introducing direct references that older hosts cannot satisfy. The accessors are cached so discovering a capability does not add repeated reflection to normal execution.

When a capability is unavailable, the dependent feature needs a safe fallback and a diagnostic explaining the limitation. If the fallback changes cache validity, the effective behavior must contribute to the cache key so results are not reused under incompatible assumptions.

For example, `MSBuildCacheEnableProbeAndEnumerationFingerprinting` depends on the host reporting directory enumeration patterns. [FileAccessDataCapabilities](../src/Common/FileAccess/FileAccessDataCapabilities.cs) detects that support; if unavailable, the feature is disabled with a diagnostic. The effective observation mode contributes to the [weak fingerprint](fingerprinting.md#two-stage-fingerprints), keeping entries produced under different observation models separate.
