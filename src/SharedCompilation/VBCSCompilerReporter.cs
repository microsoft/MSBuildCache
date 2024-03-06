// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
#if NET472
using System.Diagnostics;
#endif
using System.IO;
using System.Linq;
using Microsoft.Build.Experimental.FileAccess;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;

namespace Microsoft.MSBuildCache.SharedCompilation;

/// <summary>
/// Parses command line arguments to report file accesses.
/// </summary>
internal static class VBCSCompilerReporter
{
    /// <summary>
    /// Resolve and report file accesses given the <paramref name="language"/>, <paramref name="commandLineArguments"/>,
    /// and <paramref name="projectFile"/> of a project.
    /// </summary>
    /// <param name="language">The language of the project (either <see cref="LanguageNames.CSharp"/> or <see cref="LanguageNames.VisualBasic"/>).</param>
    /// <param name="commandLineArguments">The command line arguments.</param>
    /// <param name="projectFile">The project file.</param>
    /// <param name="engineServices">The interface through which file accesses will be reported to MSBuild.</param>
    public static void ResolveFileAccesses(
        string language,
        string[] commandLineArguments,
        string projectFile,
        EngineServices engineServices)
    {
        CommandLineArguments parsedCommandLine = CompilerUtilities.GetParsedCommandLineArguments(language, commandLineArguments, projectFile);

        // We fail file access resolution upon discovery of a CS2007 error,
        // representing a bad switch, because the Roslyn parsing classes we
        // are using are older than the version of the compiler this
        // build is using, which means we could be missing
        // switches implying file accesses.
        List<Diagnostic> parsedBadSwitchErrors = parsedCommandLine.Errors.Where(
            diagnostic => diagnostic.Id.
#if NET472
            Contains("2007")
#else
            Contains("2007", StringComparison.Ordinal)
#endif
            ).ToList();
        var badSwitchErrors = new HashSet<Diagnostic>();
        foreach (Diagnostic badSwitch in parsedBadSwitchErrors)
        {
            badSwitchErrors.Add(badSwitch);
        }

        if (badSwitchErrors.Count > 0)
        {
            string allMessages = string.Join(Environment.NewLine, badSwitchErrors.Select(diagnostic => $"[{diagnostic.Id}] {diagnostic.GetMessage()}"));
            throw new InvalidOperationException("Unrecognized switch(es) passed to the compiler. Even though the compiler supports it, using shared compilation in a sandboxed process requires the build engine to understand all compiler switches." +
                $"This probably means the version of the compiler is newer than the version the build engine is aware of. Disabling shared compilation will likely fix the problem. Details: {allMessages}");
        }

        string? baseDirectory = parsedCommandLine.BaseDirectory;
        if (string.IsNullOrEmpty(baseDirectory))
        {
            throw new ArgumentException($"{nameof(baseDirectory)} cannot be null or whitespace.");
        }

        string[] embeddedResourceFilePaths = Array.Empty<string>();

        // Determine the paths to the embedded resources. /resource: parameters end up in CommandLineArguments.ManifestResources,
        // but the returned class drops the file path (and is currently internal anyway).
        // We should be able to remove this if/when this gets resolved: https://github.com/dotnet/roslyn/issues/41372.
        if (parsedCommandLine.ManifestResources.Length > 0)
        {
            IEnumerable<string> embeddedResourcesArgs = commandLineArguments.Where(
            a =>
                a.StartsWith("/resource:", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("/res:", StringComparison.Ordinal)
                || a.StartsWith("/linkresource:", StringComparison.Ordinal)
                || a.StartsWith("/linkres:", StringComparison.Ordinal));

            embeddedResourceFilePaths = CompilerUtilities.GetEmbeddedResourceFilePaths(
                embeddedResourcesArgs,
                baseDirectory!);
        }

        var result = new ParseResult()
        {
            ParsedArguments = parsedCommandLine,
            EmbeddedResourcePaths = embeddedResourceFilePaths
        };

        RegisterAccesses(result, engineServices);
    }

    private static void RegisterAccesses(ParseResult results, EngineServices engineServices)
    {
        CommandLineArguments parsedArguments = results.ParsedArguments!;
        string? baseDirectory = parsedArguments.BaseDirectory;
        ImmutableArray<string> referencePaths = parsedArguments.ReferencePaths;
        ImmutableArray<CommandLineAnalyzerReference> analyzerReferences = parsedArguments.AnalyzerReferences;

        // Even though CommandLineArguments class claims to always report back absolute paths, that's not the case.
        // Use the base directory to resolve relative paths if needed
        // The base directory is what CommandLineArgument claims to be resolving all paths against anyway
        var accessRegister = new AccessRegistrar(baseDirectory!, engineServices);

        // All inputs
        accessRegister.RegisterInputs(analyzerReferences.Select(r => ResolveRelativePathIfNeeded(r.FilePath, baseDirectory!, referencePaths)));
        accessRegister.RegisterInputs(parsedArguments.MetadataReferences!.Select(r => ResolveRelativePathIfNeeded(r.Reference, baseDirectory!, referencePaths)));
        accessRegister.RegisterInputs(parsedArguments.SourceFiles.Select(source => source.Path));
        accessRegister.RegisterInputs(parsedArguments.EmbeddedFiles.Select(embedded => embedded.Path));
        accessRegister.RegisterInput(parsedArguments.Win32ResourceFile);
        accessRegister.RegisterInput(parsedArguments.Win32Icon);
        accessRegister.RegisterInput(parsedArguments.Win32Manifest);
        accessRegister.RegisterInputs(parsedArguments.AdditionalFiles.Select(additional => additional.Path));
        accessRegister.RegisterInput(parsedArguments.AppConfigPath);
        accessRegister.RegisterInput(parsedArguments.RuleSetPath);
        accessRegister.RegisterInput(parsedArguments.SourceLink);
        accessRegister.RegisterInput(parsedArguments.CompilationOptions.CryptoKeyFile);

        // /resource: parameters end up in ParsedArguments.ManifestResources, but the returned class drops the file path. We'll have to get them explicitly.
        // We might be able to simply use ParsedArguments.ManifestResources if this gets resolved: https://github.com/dotnet/roslyn/issues/41372
        accessRegister.RegisterInputs(results.EmbeddedResourcePaths!);

        // All outputs
        accessRegister.RegisterOutput(parsedArguments.TouchedFilesPath?.Insert(parsedArguments.TouchedFilesPath.Length - 1, ".read"));
        accessRegister.RegisterOutput(parsedArguments.TouchedFilesPath?.Insert(parsedArguments.TouchedFilesPath.Length - 1, ".write"));
        accessRegister.RegisterOutput(parsedArguments.DocumentationPath);
        accessRegister.RegisterOutput(parsedArguments.ErrorLogPath);
        accessRegister.RegisterOutput(parsedArguments.OutputRefFilePath);
        string? outputFileName = ComputeOutputFileName(parsedArguments);
        string outputDirectory = parsedArguments.OutputDirectory;
        accessRegister.RegisterOutput(Path.Combine(outputDirectory, outputFileName));
        if (parsedArguments.EmitPdb)
        {
            accessRegister.RegisterOutput(Path.Combine(outputDirectory, parsedArguments.PdbPath ?? Path.ChangeExtension(outputFileName, ".pdb")));
        }
    }

    private static string ComputeOutputFileName(CommandLineArguments args)
    {
        string? outputFileName = args.OutputFileName;

        // If the output filename is not specified, we follow the logic documented for csc.exe
        // https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/out-compiler-option
        // If you do not specify the name of the output file:
        // - An.exe will take its name from the source code file that contains the Main method.
        // - A.dll or .netmodule will take its name from the first source code file.
        // Note: observe however that when invoked through the standard MSBuild csc task, the output file name is always specified
        // and it is based on the first source file (when the original task didn't specify it). So this is just about being conservative.
        if (string.IsNullOrEmpty(outputFileName))
        {
            outputFileName = args.CompilationOptions.OutputKind switch
            {
                // For these cases, the first source file is used
                OutputKind.DynamicallyLinkedLibrary => Path.ChangeExtension(args.SourceFiles[0].Path, ".dll"),
                OutputKind.NetModule => Path.ChangeExtension(args.SourceFiles[0].Path, ".netmodule"),
                OutputKind.WindowsRuntimeMetadata => Path.ChangeExtension(args.SourceFiles[0].Path, ".winmdobj"),
                // For these cases an .exe will be generated based on the source file that contains a Main method.
                // We cannot easily predict this statically, so we bail out for this case.
                OutputKind.ConsoleApplication or OutputKind.WindowsApplication or OutputKind.WindowsRuntimeApplication => throw new InvalidOperationException("The output filename was not specified and it could not be statically predicted. Static predictions are required for managed compilers when shared compilation is enabled. " +
                                            "Please specify the output filename or disable shared compilation by setting 'useManagedSharedCompilation' in Bxl main configuration file."),
                _ => throw new InvalidOperationException($"Unrecognized OutputKind: {args.CompilationOptions.OutputKind}"),
            };
        }

        return outputFileName!;
    }

    /// <summary>
    /// Returns an absolute path based on the given path and base and additional search directories. Null if the path cannot be resolved.
    /// </summary>
    /// <remarks>
    /// This mimics the behavior of the compiler, where in case path is a relative one, tries to compose an absolute path based on the provided
    /// directories (first the base directory, then the additional ones, in order) and returns the first absolute path such that the path exists.
    /// Observe this will cause potential absent file probes that will be observed by detours, which is intentional.
    /// </remarks>
    private static string? ResolveRelativePathIfNeeded(string path, string baseDirectory, IEnumerable<string> additionalSearchDirectories)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // If the path is already an absolute one, just return
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        // So this should be a relative path
        // We first try resolving against the base directory
        string candidate = Path.Combine(baseDirectory, path);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Now try against all the additional search directories
        foreach (string searchDirectory in additionalSearchDirectories)
        {
            candidate = Path.Combine(searchDirectory, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // The path could not be resolved
        return null;
    }

    /// <summary>
    /// Resolves all relative path registrations against a base path.
    /// </summary>
    private sealed class AccessRegistrar
    {
        private delegate void ReportFileAccessFn(FileAccessData fileAccessData);

        private readonly string _basePath;

        private readonly ReportFileAccessFn _reportFileAccess;

        private static readonly uint ProcessId = (uint)
#if NET472
                Process.GetCurrentProcess().Id;
#else
                // More performant than Process.GetCurrentProcess().Id.
                Environment.ProcessId;
#endif

        public AccessRegistrar(string basePath, EngineServices engineServices)
        {
            _basePath = basePath;

            // Use reflection to get the ReportFileAccess method since it's not exposed.
            // TODO: Once there is a proper API for this, use it.
            _reportFileAccess = (ReportFileAccessFn)Delegate.CreateDelegate(typeof(ReportFileAccessFn), engineServices, "ReportFileAccess");
        }

        public void RegisterOutput(string? filePath) => RegisterAccess(filePath, RequestedAccess.Write, DesiredAccess.GENERIC_WRITE);

        public void RegisterInput(string? filePath) => RegisterAccess(filePath, RequestedAccess.Read, DesiredAccess.GENERIC_READ);

        public void RegisterInputs(IEnumerable<string?> filePaths)
        {
#if NETFRAMEWORK
            if (filePaths == null)
            {
                throw new ArgumentNullException(nameof(filePaths));
            }
#else
            ArgumentNullException.ThrowIfNull(filePaths);
#endif

            foreach (string? filePath in filePaths)
            {
                RegisterAccess(filePath, RequestedAccess.Read, DesiredAccess.GENERIC_READ);
            }
        }

        private void RegisterAccess(string? filePath, RequestedAccess requestedAccess, DesiredAccess desiredAccess)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            FileAccessData fileAccessData = new(
                ReportedFileOperation.CreateFile,
                requestedAccess,
                ProcessId,
                id: 0,
                correlationId: 0,
                error: 0,
                desiredAccess,
                FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
                MakeAbsoluteIfNeeded(filePath!),
                processArgs: null!,
                true);

            _reportFileAccess(fileAccessData);
        }

        private string MakeAbsoluteIfNeeded(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_basePath, path);
    }

    /// <summary>
    /// Container for parsing results.
    /// </summary>
    /// <param name="ParsedArguments">The parsed arguments.</param>
    /// <param name="EmbeddedResourcePaths">The embedded resource paths.</param>
    private readonly record struct ParseResult(CommandLineArguments? ParsedArguments, string[]? EmbeddedResourcePaths);
}