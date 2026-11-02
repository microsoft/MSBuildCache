# Fingerprint retention reproduction

This opt-in driver keeps one factory alive while completing many distinct projects.
Each project enumerates shared synthetic member names and probes its own marker.
No source files or network cache are needed. Each calculation is requested twice,
as happens during publication and diagnostic writing.

From the repository root:

```powershell
dotnet run --project tests/FingerprintMemory --configuration Release --framework net9.0 -- 200 2000 20
```

The three arguments are project count, directory-member count, and reporting batch
size. The default mode forces collections between batches to expose data still
retained by the live factory. Compare the managed-byte slope as completed projects
increase. For .NET Framework, build the project for `net472` and run its x64 EXE.

Run baseline and modified assemblies in separate processes with the same runtime,
arguments and configuration. Record the actual assembly version/hash. The final
fingerprint checksum must match. Increase project count and member count separately;
large baseline runs deliberately retain substantial memory.

For timing, append `--timing`. This disables explicit collections; do not compare
elapsed times from forced-collection runs as ordinary build performance. Use several
fresh processes and report both memory and CPU/time costs. Full build behavior and
diagnostic output are covered separately by the normal test/scenario harnesses.
