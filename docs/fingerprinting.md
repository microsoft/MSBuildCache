# Fingerprinting and observed inputs

MSBuildCache reuses project execution results when the inputs that produced them still match. Those inputs include both the build request and dependencies discovered while tools run. Graph construction and evaluation remain MSBuild's responsibility.

See [host capability detection](dependencies.md#host-capability-detection) for runtime availability of observation tracking.

## Two-stage fingerprints

Not every input can be identified before execution. Input prediction provides a starting set, but tools can discover additional files or make decisions based on filesystem state. A two-stage lookup lets the cache use those observations without first rerunning the project to discover them.

The **weak fingerprint** groups candidates using information available before project execution: project configuration, requested targets, toolchain identity, relevant cache settings, predicted input hashes, and dependency output hashes. Including dependency results makes changes flow through the build graph, rather than relying solely on files a consumer was observed reading.

Each candidate **selector** identifies a [PathSet](../src/Common/Fingerprinting/PathSet.cs) of additional observed inputs and a **strong fingerprint** representing their state. The cache can retain multiple selectors under one weak fingerprint for different observed input sets or filesystem states. Predicted inputs are already covered by the weak fingerprint and are not recorded again in the PathSet.

A weak-fingerprint match only finds candidates. Where additional inputs were observed, lookup validates them against the current workspace and checks the strong fingerprint. A matching entry must also supply the cached result and output content. This separation is implemented by [FingerprintFactory](../src/Common/Fingerprinting/FingerprintFactory.cs) and [CacheClient](../src/Common/Caching/CacheClient.cs).

## Filesystem state is more than file contents

Reading a file is only one way a build can depend on the filesystem:

| Observation | Dependency |
| --- | --- |
| Content read | The contents of a file. |
| Existing-path probe | A path exists, without necessarily reading its contents. |
| Absent-path probe | A path does not exist. |
| Directory enumeration | The names matching a search pattern in a directory. |

For example, creating a previously absent optional configuration file can change a build without modifying any file it read previously. Similarly, adding a source file matched by `*.cs` changes an enumeration even though the old source files are unchanged. A nonmatching file does not change that enumeration's membership.

Content dependencies are validated through current input hashes. Probes and directory memberships are checked explicitly against the current filesystem before accepting a selector. Their recorded state also contributes to the strong fingerprint, but hashing that record again cannot establish that the state still holds.

Enumerations use the [native filesystem's search semantics](../src/Common/Fingerprinting/DirectoryEnumerationReader.cs), not a general-purpose glob matcher. Reproducing the pattern the tool used is essential: on Windows, even `*.*` can match extensionless names.

## Separating inputs from project side effects

A project can read or probe files it has just generated. Treating its own side effects as external inputs would make a cached build depend on whether those outputs were already present, causing misses when moving between clean and incremental workspaces.

[Observation filtering](../src/Common/FileAccess/FileAccessRepository.cs) excludes reads of the project's outputs and probes of its own writes and affected directories. The write history includes transient files, not just surviving outputs. This distinction is project-local: another project's outputs can still be genuine inputs.

Directory enumerations need more care because one directory can contain both external inputs and the project's own outputs. The observation records those groups separately. Lookup subtracts the recorded self-written names before comparing external membership, so outputs left by a previous build do not themselves invalidate the result. This assumes deterministic execution would write the same names.

## Portable and stable observations

[Path normalization](../src/Common/PathNormalizer.cs) represents repository and NuGet package paths relative to their roots, allowing cache entries to be reused from different checkout and package locations. Observations use consistent ordering and comparison rules so incidental access order does not change their identity.

Canonicalization must preserve meaningful distinctions: different enumeration patterns can represent different dependencies, and a missing directory is not equivalent to an existing empty directory. These distinctions belong to the observation model even when their storage representation changes.

## Scope and assumptions

The default input hasher covers source-control-known files and NuGet package files. Content observations contribute only where the configured hasher can supply hashes; probes and enumerations are limited to repository and package roots. This is not a complete model of arbitrary machine state, environment variables, registry values, or network responses.

Ignoring an input deliberately removes it from the validity check. Ignore patterns therefore express an assumption that the excluded state cannot affect the result, rather than simply reducing lookup work.

Inputs are expected to remain stable during a build; hash reuse and observation validation do not provide an atomic filesystem snapshot. Validating inputs also does not establish a complete output set: an incremental invocation can write only a delta, and publication records observed surviving outputs rather than synthesizing everything a clean build would have produced.
