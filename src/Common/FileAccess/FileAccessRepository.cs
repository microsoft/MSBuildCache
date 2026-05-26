// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DotNet.Globbing;
using Microsoft.Build.Experimental.FileAccess;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Fingerprinting;

namespace Microsoft.MSBuildCache.FileAccess;

internal sealed class FileAccessRepository : IDisposable
{
    // Case-insensitive to match Windows filesystem semantics. NtQueryDirectoryFile / FindFirstFileEx
    // match patterns case-insensitively, so re-enumeration at FinishProject must too — otherwise a
    // pattern like "*.cs" stored at observation time would fail to match "Foo.CS" on disk.
    private static readonly GlobOptions CaseInsensitiveGlobOptions = new() { Evaluation = { CaseInsensitive = true } };

    // Perf optimization over Enum.ToString()
    private static readonly string[] ReportedFileOperationNames =
#if NET9_0_OR_GREATER
        Enum.GetNames<ReportedFileOperation>();
#else
        Enum.GetNames(typeof(ReportedFileOperation));
#endif

    private readonly ConcurrentDictionary<NodeContext, FileAccessesState> _fileAccessStates = new();

    // Use a shared process table for all nodes. This helps facilitate cases where "server" processes are used across projects such as vctip.exe.
    // Otherwise if a file access happens in a node which didn't launch the process originally, the AllowFileAccessAfterProjectFinishProcessPatterns
    // setting cannot be properly used since the process name isn't known at that point.
    // Processes are removed from this table once they exit, so memory-wise this should in fact be better than a table per node, and we are not worried
    // about collisions since process ids do not collide system-wide.
    private readonly ConcurrentDictionary<ulong, string> _processTable = new();

    private readonly PluginLoggerBase _logger;

    private readonly PluginSettings _pluginSettings;

    public FileAccessRepository(PluginLoggerBase logger, PluginSettings pluginSettings)
    {
        _logger = logger;
        _pluginSettings = pluginSettings;
    }

    public void Dispose()
    {
        foreach (KeyValuePair<NodeContext, FileAccessesState> kvp in _fileAccessStates)
        {
            FileAccessesState state = kvp.Value;
            state.Dispose();
        }

        _fileAccessStates.Clear();
    }

    public void AddFileAccess(NodeContext nodeContext, FileAccessData fileAccessData)
        => GetFileAccessesState(nodeContext).AddFileAccess(fileAccessData);

    public void AddProcess(NodeContext nodeContext, ProcessData processData)
        => GetFileAccessesState(nodeContext).AddProcess(processData);

    public FileAccesses FinishProject(NodeContext nodeContext)
        => GetFileAccessesState(nodeContext).FinishProject();

    private FileAccessesState GetFileAccessesState(NodeContext nodeContext)
        => _fileAccessStates.GetOrAdd(nodeContext, nodeContext => new FileAccessesState(nodeContext, _logger, _pluginSettings, _processTable));

    /// <summary>
    /// Builds the "ever-written or ancestor-of-written" set used to filter post-write probe/enumeration
    /// observations. For every path in <paramref name="writtenPaths"/>, the result includes the path itself
    /// plus every ancestor directory back to the root. This extends the self-output probe filter with
    /// ancestor-dir handling — MSBuild commonly probes output directories (e.g., <c>bin\Debug\net9.0\</c>)
    /// before creating them, and we need to suppress those probes so re-observation at cache lookup doesn't
    /// flip them to <c>ExistingProbe</c> after the build creates the directory.
    /// </summary>
    internal static HashSet<string> BuildEverWrittenOrAncestorSet(List<string> writtenPaths)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in writtenPaths)
        {
            result.Add(path);

            // Path.GetDirectoryName output is already trim-normalized (no trailing separator except at drive
            // roots like "C:\", which then yields null on the next call and terminates the walk). So we only
            // need to trim caller-supplied input once, not at every level.
            string? ancestor = Path.GetDirectoryName(TrimTrailingSeparator(path));
            while (!string.IsNullOrEmpty(ancestor))
            {
                if (!result.Add(ancestor!))
                {
                    // Already in the set — every shallower ancestor is too. Stop the walk.
                    break;
                }

                ancestor = Path.GetDirectoryName(ancestor);
            }
        }

        return result;
    }

    /// <summary>
    /// Trims a single trailing directory separator (forward or back slash) from the given path. No-op when
    /// the path doesn't end with a separator.
    /// </summary>
    internal static string TrimTrailingSeparator(string path)
        => path.Length > 0 && (path[path.Length - 1] == Path.DirectorySeparatorChar || path[path.Length - 1] == Path.AltDirectorySeparatorChar)
            ? path.Substring(0, path.Length - 1)
            : path;

    private sealed class FileAccessesState : IDisposable
    {
        private readonly object _stateLock = new();

        private readonly NodeContext _nodeContext;

        private readonly PluginLoggerBase _logger;

        private readonly PluginSettings _pluginSettings;

        private readonly ConcurrentDictionary<ulong, string> _processTable;

        private readonly StreamWriter _logFileStream;

        private Dictionary<string, FileAccessInfo>? _fileTable = new(StringComparer.OrdinalIgnoreCase);

        private List<RemoveDirectoryOperation>? _deletedDirectories = new();

        // Captured probe and enumeration observations, in arrival order. Null when
        // EnableProbeAndEnumerationFingerprinting is off — AddFileAccess uses null as the signal to short-circuit.
        private List<ObservedAccess>? _observations;

        private long _fileAccessCounter;

        private bool _isFinished;

        public FileAccessesState(
            NodeContext nodeContext,
            PluginLoggerBase logger,
            PluginSettings pluginSettings,
            ConcurrentDictionary<ulong, string> processTable)
        {
            _nodeContext = nodeContext;
            _logger = logger;
            _pluginSettings = pluginSettings;
            _processTable = processTable;

            // Only allocate the observations list when the feature flag is on. AddFileAccess uses _observations
            // being null vs non-null as the capture-or-skip signal so flag-off doesn't pay any per-probe cost.
            if (_pluginSettings.EnableProbeAndEnumerationFingerprinting)
            {
                _observations = new List<ObservedAccess>();
            }

            string logFilePath = Path.Combine(nodeContext.LogDirectory, "fileAccesses.log");
            _logFileStream = File.CreateText(logFilePath);
        }

        public void Dispose()
        {
            _logFileStream.Dispose();
        }

        public void AddFileAccess(FileAccessData fileAccessData)
        {
            string path = PathHelper.RemoveLongPathPrefixes(fileAccessData.Path);
            lock (_stateLock)
            {
                if (_isFinished)
                {
                    string? processName = null;
                    Glob? processMatch = null;
                    if (_processTable.TryGetValue(fileAccessData.ProcessId, out processName))
                    {
                        processMatch = IsAllowFileAccessAfterProjectFinishProcessPatterns(processName);
                    }

                    Glob? fileMatch = IsAllowFileAccessAfterProjectFinishFilePatterns(path);

                    processName ??= $"ProcessId: {fileAccessData.ProcessId}";

                    if (processMatch != null)
                    {
                        _logger.LogWarning(
                            $"File access reported from process after the project finished, but process matched {nameof(_pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns)} `{processMatch}`. " +
                            $"This may lead to incorrect caching. Node Id: {_nodeContext.Id}, Process Id: {fileAccessData.ProcessId} ProcessPath: `{processName}` File Path: `{path}`");
                    }
                    else if (fileMatch != null)
                    {
                        _logger.LogWarning(
                            $"File access reported from process after the project finished, but file path matched {nameof(_pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns)} `{fileMatch}`. " +
                            $"This may lead to incorrect caching. Node Id: {_nodeContext.Id}, Process Id: {fileAccessData.ProcessId} ProcessPath: `{processName}` File Path: `{path}`");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"File access reported from process `{processName}` after the project finished. " +
                            $"This may lead to incorrect caching. Node Id: {_nodeContext.Id}, Path: {path}");
                    }
                }

                FlagsAndAttributes flagsAndAttributes = fileAccessData.FlagsAndAttributes;
                DesiredAccess desiredAccess = fileAccessData.DesiredAccess;
                ReportedFileOperation operation = fileAccessData.Operation;

                uint processId = fileAccessData.ProcessId;
                RequestedAccess requestedAccess = fileAccessData.RequestedAccess;
                uint error = fileAccessData.Error;

                // Used to identify file accesses reconstructed from breakaway processes, such as csc.exe when using shared compilation.
                bool isAnAugmentedFileAccess = fileAccessData.IsAnAugmentedFileAccess;

                // Augmented file accesses may not be in a canonical form (may have "..\..\")
                if (isAnAugmentedFileAccess)
                {
                    path = Path.GetFullPath(path);
                }

                if (operation == ReportedFileOperation.Process)
                {
                    _logFileStream.WriteLine($"New process: PId {processId}, process name {path}, arguments {fileAccessData.ProcessArgs}");
                    _processTable.TryAdd(processId, path);
                }

                // Note: This is a hot path, so writing fields one at a time to avoid the overhead of a string.Format with many arguments.
                _logFileStream.Write(processId);
                _logFileStream.Write(", ");
                _logFileStream.Write(fileAccessData.Id);
                _logFileStream.Write(", ");
                _logFileStream.Write(fileAccessData.CorrelationId);
                _logFileStream.Write(", ");
                UInt32FlagsFormatter<DesiredAccess>.Write(_logFileStream, (uint)desiredAccess);
                _logFileStream.Write(", ");
                UInt32FlagsFormatter<FlagsAndAttributes>.Write(_logFileStream, (uint)flagsAndAttributes);
                _logFileStream.Write(", ");
                WriteRequestedAccess(_logFileStream, requestedAccess);
                _logFileStream.Write(", ");
                _logFileStream.Write(ReportedFileOperationNames[(int)operation]);
                _logFileStream.Write(", ");
                _logFileStream.Write(path);
                _logFileStream.Write(", ");
                _logFileStream.Write(error);
                if (isAnAugmentedFileAccess)
                {
                    _logFileStream.Write(" (Augmented)");
                }

                _logFileStream.WriteLine();

                // Classify probes/enumerations BEFORE the generic `error != 0` short-circuit below — probes
                // can have not-found errors (ERROR_FILE_NOT_FOUND for AbsentPathProbe) that must not be dropped.
                // RemoveDirectory is handled by _deletedDirectories and is not an observation.
                bool isProbe = requestedAccess == RequestedAccess.Probe;
                bool isEnumeration = requestedAccess == RequestedAccess.Enumerate
                                  || requestedAccess == RequestedAccess.EnumerationProbe;

                if (isProbe || isEnumeration)
                {
                    // Skip capture entirely when the feature flag is off (probes are the majority of events).
                    if (_observations != null && operation != ReportedFileOperation.RemoveDirectory)
                    {
                        ObservationType obsType;
                        if (isProbe)
                        {
                            // error == 0 means the probe succeeded (file/directory exists).
                            // Non-zero error means the probe failed; the typical not-found codes
                            // (ERROR_FILE_NOT_FOUND=2, ERROR_PATH_NOT_FOUND=3) are AbsentPathProbe.
                            // Other error codes (ACCESS_DENIED, etc.) are ambiguous — record as Absent
                            // for now, which is the conservative choice.
                            obsType = error == 0 ? ObservationType.ExistingProbe : ObservationType.AbsentPathProbe;
                        }
                        else
                        {
                            obsType = ObservationType.DirectoryEnumeration;
                        }

                        // TODO (cross-repo: incremental-build-fingerprinting): EnumerationPattern is
                        // unavailable today. BXL's IDetoursEventListener.FileAccessData is missing the
                        // EnumeratePattern field (dropped in SandboxedProcessReports when materializing
                        // the listener struct); MSBuild's FileAccessData is missing it too. Both layers
                        // need additive API changes before this can be populated. Until then, observations
                        // capture the directory as unfiltered, which conservatively invalidates on ANY
                        // child change — making filtered enumerations (the common case) unable to hit.
                        // The feature flag (_observations != null) gates this whole branch and should
                        // stay opt-out OFF until upstream lands.
                        _observations.Add(new ObservedAccess(path, obsType, EnumerationPattern: null));
                    }

                    // Bump the counter so probes participate in event ordering. Unused gaps are benign —
                    // only relative order matters (the counter governs RemoveDirectory↔write ordering).
                    _fileAccessCounter++;
                    return;
                }

                if (error != 0)
                {
                    // we don't want to process failing file accesses- logging them with the error code
                    return;
                }

                if (operation == ReportedFileOperation.RemoveDirectory)
                {
                    // if a file is created under this path in future, it will have a higher file counter
                    // this file counter can be used to determine whether the file should be considered deleted or not
                    if (_deletedDirectories != null)
                    {
                        _deletedDirectories.Add(new RemoveDirectoryOperation(_fileAccessCounter, path));
                    }
                }
                else if (_fileTable != null)
                {
                    if (!_fileTable.TryGetValue(path, out FileAccessInfo? access))
                    {
                        access = new FileAccessInfo(path);
                        _fileTable.Add(path, access);
                    }

                    access.AccessAndOperations.Add(new AccessAndOperation(
                        _fileAccessCounter,
                        desiredAccess,
                        flagsAndAttributes,
                        requestedAccess,
                        operation,
                        isAnAugmentedFileAccess));
                }

                _fileAccessCounter++;
            }
        }

        public void AddProcess(ProcessData processData)
        {
            lock (_stateLock)
            {
                if (_isFinished)
                {
                    Glob? match = IsAllowProcessCloseAfterProjectFinishProcessPatterns(processData.ProcessName);
                    if (match != null)
                    {
                        _logger.LogMessage(
                            $"Process `{processData.ProcessName}` exited after the project finished, but matched {nameof(_pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns)} `{match}`. " +
                            $"This may lead to incorrect caching. Node Id: {_nodeContext.Id}.");
                    }
                    else
                    {
                        _logger.LogWarning(
                            $"Process `{processData.ProcessName}` exited after the project finished. " +
                            $"This may lead to incorrect caching. Node Id: {_nodeContext.Id}.");
                    }
                }

                _processTable.TryRemove(processData.ProcessId, out _);

                _logFileStream.WriteLine(
                    "Process exited. PId: {0}, Parent: {1}, Name: {2}, ExitCode: {3}, CreationTime: {4}, ExitTime: {5}",
                    processData.ProcessId,
                    processData.ParentProcessId,
                    processData.ProcessName,
                    processData.ExitCode,
                    processData.CreationDateTime,
                    processData.ExitDateTime);
            }
        }

        public FileAccesses FinishProject()
        {
            Dictionary<string, FileAccessInfo> fileTable;
            List<RemoveDirectoryOperation> deletedDirectories;
            List<ObservedAccess>? observations;
            lock (_stateLock)
            {
                _isFinished = true;
                _logFileStream.WriteLine("Project finished");

                fileTable = _fileTable!;
                deletedDirectories = _deletedDirectories!;
                observations = _observations;

                // Allow memory to be reclaimed
                _fileTable = null;
                _deletedDirectories = null;
                _observations = null;

                if (_pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns.Count == 0 &&
                    _pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns.Count == 0 &&
                    _pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns.Count == 0)
                {
                    _logFileStream.Dispose();
                }
            }

            return ProcessFileAccesses(fileTable, deletedDirectories, observations);
        }

        private Glob? IsAllowFileAccessAfterProjectFinishFilePatterns(string fileName) =>
            _pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns.FirstOrDefault(p => p.IsMatch(fileName));

        private Glob? IsAllowFileAccessAfterProjectFinishProcessPatterns(string processName) =>
            _pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns.FirstOrDefault(p => p.IsMatch(processName));

        private Glob? IsAllowProcessCloseAfterProjectFinishProcessPatterns(string processName) =>
            _pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns.FirstOrDefault(p => p.IsMatch(processName));

        private static FileAccesses ProcessFileAccesses(
            Dictionary<string, FileAccessInfo> fileTable,
            List<RemoveDirectoryOperation> deletedDirectories,
            List<ObservedAccess>? observations)
        {
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ObservedAccess> allObservations = new();

            IEnumerable<FileAccessInfo> outputFileInfos = fileTable
                .Select(fileInfoKvp => fileInfoKvp.Value)
                .Where(IsOutput);
            IEnumerable<FileAccessInfo> inputFileInfos = fileTable
                .Select(fileInfoKvp => fileInfoKvp.Value)
                .Where(IsInput);

            foreach (FileAccessInfo fileInfo in outputFileInfos)
            {
                string filePath = fileInfo.FilePath;

                if (!ParentFolderExists(fileInfo, deletedDirectories))
                {
                    continue;
                }

                outputs.Add(filePath);
            }

            foreach (FileAccessInfo fileInfo in inputFileInfos)
            {
                string filePath = fileInfo.FilePath;

                // Don't consider reads of an output as an input
                if (outputs.Contains(filePath))
                {
                    continue;
                }

                allObservations.Add(new ObservedAccess(filePath, ObservationType.FileContentRead));
            }

            // Drop probe/enumeration observations for paths written by this project — post-write
            // probes/enumerations reflect intra-build state, not the pre-build state that drives cache
            // lookup. Keeping them would cause false misses for probe-then-write patterns (e.g. a target
            // that probes `obj/A.GeneratedCode.cs`, generates it if missing, then re-probes).
            //
            // We use a broader "ever-written" set than `outputs`: outputs filters to existing-file outputs
            // only, which would leak transient temp files, build-created directories, and orphan-parent
            // files. Cross-project probes still survive — this is project-local, not graph-wide.
            //
            // Ancestor directories of every written file are also filtered: MSBuild commonly probes an
            // output directory (e.g., `bin\Debug\net9.0\`) before creating it; without this filter,
            // re-observation would promote the cached AbsentPathProbe to ExistingProbe and miss.
            //
            // For DirectoryEnumeration observations that survive the filter, PartitionDirectoryMembers
            // splits the directory's contents into Members (external) and WrittenMembers (self-outputs).
            // At lookup, the WrittenMembers list cancels whatever the previous build wrote, so cache hits
            // remain correct whether outputs are still on disk or not.
            if (observations != null && observations.Count > 0)
            {
                // Single pass over fileTable: collect writtenPaths and build a per-directory leaf-name
                // index of self-writes so we can partition each surviving DirectoryEnumeration's members
                // in O(membersInDir) time.
                List<string> writtenPaths = new();
                Dictionary<string, HashSet<string>> writtenLeafNamesByDir = new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, FileAccessInfo> kvp in fileTable)
                {
                    if (!EverWritten(kvp.Value))
                    {
                        continue;
                    }

                    string writtenPath = kvp.Key;
                    writtenPaths.Add(writtenPath);

                    string parent = TrimTrailingSeparator(Path.GetDirectoryName(writtenPath) ?? string.Empty);
                    string leaf = Path.GetFileName(writtenPath);
                    if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
                    {
                        continue;
                    }

                    if (!writtenLeafNamesByDir.TryGetValue(parent, out HashSet<string>? leafSet))
                    {
                        leafSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        writtenLeafNamesByDir[parent] = leafSet;
                    }

                    leafSet.Add(leaf);
                }

                HashSet<string> everWrittenOrAncestor = BuildEverWrittenOrAncestorSet(writtenPaths);

                foreach (ObservedAccess obs in observations)
                {
                    if (everWrittenOrAncestor.Contains(obs.Path) || everWrittenOrAncestor.Contains(TrimTrailingSeparator(obs.Path)))
                    {
                        continue;
                    }

                    if (obs.Type != ObservationType.DirectoryEnumeration)
                    {
                        allObservations.Add(obs);
                        continue;
                    }

                    // Enrich the DirectoryEnumeration observation with the partitioned member lists.
                    string dirAbsolute = TrimTrailingSeparator(obs.Path);
                    HashSet<string> selfOutputLeafNames = writtenLeafNamesByDir.TryGetValue(dirAbsolute, out HashSet<string>? leaves)
                        ? leaves
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    (IReadOnlyList<string>? members, IReadOnlyList<string>? writtenMembers) = PartitionDirectoryMembers(dirAbsolute, obs.EnumerationPattern, selfOutputLeafNames);
                    allObservations.Add(obs with { Members = members, WrittenMembers = writtenMembers });
                }
            }

            return new FileAccesses(allObservations, outputs);
        }

        /// <summary>
        /// Enumerates the directory and partitions members into <c>Members</c> (external dependencies) and
        /// <c>WrittenMembers</c> (this build's outputs). Both lists are leaf names, sorted
        /// <c>OrdinalIgnoreCase</c>. Returns <c>(null, null)</c> if the directory is missing or inaccessible.
        /// </summary>
        private static (IReadOnlyList<string>? Members, IReadOnlyList<string>? WrittenMembers) PartitionDirectoryMembers(
            string absoluteDirectoryPath,
            string? enumerationPattern,
            HashSet<string> selfOutputLeafNames)
        {
            if (!Directory.Exists(absoluteDirectoryPath))
            {
                return (null, null);
            }

            Glob? patternGlob = string.IsNullOrEmpty(enumerationPattern)
                ? null
                : Glob.Parse(enumerationPattern, CaseInsensitiveGlobOptions);

            List<string> members = new();
            List<string> writtenMembers = new();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(absoluteDirectoryPath, "*", SearchOption.TopDirectoryOnly))
                {
                    string leaf = Path.GetFileName(entry);
                    if (patternGlob != null && !patternGlob.IsMatch(leaf))
                    {
                        continue;
                    }

                    if (selfOutputLeafNames.Contains(leaf))
                    {
                        writtenMembers.Add(leaf);
                    }
                    else
                    {
                        members.Add(leaf);
                    }
                }
            }
            catch (IOException)
            {
                return (null, null);
            }
            catch (UnauthorizedAccessException)
            {
                return (null, null);
            }

            members.Sort(StringComparer.OrdinalIgnoreCase);
            writtenMembers.Sort(StringComparer.OrdinalIgnoreCase);
            return (members, writtenMembers);
        }

        private static bool IsOutput(FileAccessInfo fileInfo)
        {
            // Ignore temporary files: files that were created but deleted later
            // Ignore directories: for output collection, we only care about files
            return EverWritten(fileInfo) && FileExists(fileInfo) && !IsDirectory(fileInfo);
        }

        private static bool IsInput(FileAccessInfo fileInfo)
        {
            // Ignore temporary files: files that were created but deleted later
            // Ignore directories: for fingerprinting, we only care about files
            return !EverWritten(fileInfo) && FileExists(fileInfo) && !IsDirectory(fileInfo);
        }

        private static bool FileExists(FileAccessInfo fileInfo)
        {
            // Augmented operations come out of order, so look backwards until we find one which is not an augmented read.
            // This covers the case of a file being generated, included in compilation, then deleted, but the augmented read
            // comes after the delete. This should be safe since the read should have failed if the file was missing.
            // This does *not* cover the case of an augmented write, as that case is ambiguous since we can't know which happened
            // first: the write or the delete. However, this scenario is very unlikely.
            int lastAccessIndex = fileInfo.AccessAndOperations.Count;
            AccessAndOperation lastAccess;
            do
            {
                lastAccessIndex--;
                lastAccess = fileInfo.AccessAndOperations[lastAccessIndex];
            }
            while (lastAccessIndex > 0 && lastAccess.IsAugmented && lastAccess.RequestedAccess == RequestedAccess.Read);

            ReportedFileOperation operation = lastAccess.ReportedFileOperation;

            bool isDeleteOperation =
                operation == ReportedFileOperation.DeleteFile
                // FileDispositionInformation is used to request to delete a file or cancel a previously requested deletion. For the latter, BuildXL doesn't detour it. So ZwSetDispositionInfomartionFile can be treated as deletion.
                || operation == ReportedFileOperation.ZwSetDispositionInformationFile && (lastAccess.DesiredAccess & DesiredAccess.DELETE) != 0
                // FileModeInformation can be used for a number of operations, but BuildXL only detours FILE_DELETE_ON_CLOSE. So, ZwSetModeInformationFile can be treated as deletion.
                || operation == ReportedFileOperation.ZwSetModeInformationFile && (lastAccess.DesiredAccess & DesiredAccess.DELETE) != 0
                || (lastAccess.FlagsAndAttributes & FlagsAndAttributes.FILE_FLAG_DELETE_ON_CLOSE) != 0;
            bool fileSourceMoved =
                operation == ReportedFileOperation.MoveFileSource
                || operation == ReportedFileOperation.MoveFileWithProgressSource
                || operation == ReportedFileOperation.SetFileInformationByHandleSource
                || operation == ReportedFileOperation.ZwSetRenameInformationFileSource
                || operation == ReportedFileOperation.ZwSetFileNameInformationFileSource;

            return !isDeleteOperation && !fileSourceMoved;
        }

        private static bool IsDirectory(FileAccessInfo fileInfo)
            => fileInfo.AccessAndOperations.Any(access =>
                access.ReportedFileOperation == ReportedFileOperation.CreateDirectory
                || (access.FlagsAndAttributes & FlagsAndAttributes.FILE_ATTRIBUTE_DIRECTORY) != 0)
            || fileInfo.FilePath.EndsWith('\\');

        private static bool ParentFolderExists(FileAccessInfo fileInfo, List<RemoveDirectoryOperation> deletedDirectories)
        {
            foreach (RemoveDirectoryOperation deletedDirectory in deletedDirectories)
            {
                // GlobalAccessId is an increasing sequence of all accesses for a target (across all processes) and is guaranteed to be unique and in order.
                // If the last access of the file was before the directory was deleted, the file is effectively deleted.
                // If the last access of the file was after the directory was deleted, the directory must have been later recreated.
                if (fileInfo.FilePath.IsUnderDirectory(deletedDirectory.DirectoryPath)
                    && fileInfo.AccessAndOperations[fileInfo.AccessAndOperations.Count - 1].GlobalAccessId < deletedDirectory.GlobalAccessId)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool EverWritten(FileAccessInfo fileInfo)
            => fileInfo.AccessAndOperations.Any(access => (access.RequestedAccess & RequestedAccess.Write) != 0);

        // Fast formatter for the RequestedAccess enum.
        private static void WriteRequestedAccess(StreamWriter writer, RequestedAccess requestedAccess)
        {
            if (requestedAccess == RequestedAccess.None)
            {
                writer.Write(nameof(RequestedAccess.None));
                return;
            }

            if (requestedAccess == RequestedAccess.All)
            {
                writer.Write(nameof(RequestedAccess.All));
                return;
            }

            bool isFirst = true;

            if ((requestedAccess & (RequestedAccess.ReadWrite)) != 0)
            {
                Write(writer, nameof(RequestedAccess.ReadWrite), ref isFirst);
            }
            else
            {
                if ((requestedAccess & (RequestedAccess.Read)) != 0)
                {
                    Write(writer, nameof(RequestedAccess.Read), ref isFirst);
                }

                if ((requestedAccess & (RequestedAccess.Write)) != 0)
                {
                    Write(writer, nameof(RequestedAccess.Write), ref isFirst);
                }
            }

            if ((requestedAccess & (RequestedAccess.Probe)) != 0)
            {
                Write(writer, nameof(RequestedAccess.Probe), ref isFirst);
            }

            if ((requestedAccess & (RequestedAccess.Enumerate)) != 0)
            {
                Write(writer, nameof(RequestedAccess.Enumerate), ref isFirst);
            }

            if ((requestedAccess & (RequestedAccess.EnumerationProbe)) != 0)
            {
                Write(writer, nameof(RequestedAccess.EnumerationProbe), ref isFirst);
            }

            static void Write(StreamWriter writer, string value, ref bool isFirst)
            {
                if (isFirst)
                {
                    isFirst = false;
                }
                else
                {
                    writer.Write('|');
                }

                writer.Write(value);
            }
        }

        private sealed class FileAccessInfo
        {
            public FileAccessInfo(string filePath)
            {
                FilePath = filePath;
            }

            public string FilePath { get; }

            public List<AccessAndOperation> AccessAndOperations { get; } = new List<AccessAndOperation>();
        }

        private sealed class AccessAndOperation
        {
            public AccessAndOperation(
                long globalAccessId,
                DesiredAccess desiredAccess,
                FlagsAndAttributes flagsAndAndAttributes,
                RequestedAccess requestedAccess,
                ReportedFileOperation reportedFileOperation,
                bool isAugmented)
            {
                GlobalAccessId = globalAccessId;
                DesiredAccess = desiredAccess;
                FlagsAndAttributes = flagsAndAndAttributes;
                RequestedAccess = requestedAccess;
                ReportedFileOperation = reportedFileOperation;
                IsAugmented = isAugmented;
            }

            public long GlobalAccessId { get; }

            public DesiredAccess DesiredAccess { get; }

            public FlagsAndAttributes FlagsAndAttributes { get; }

            public RequestedAccess RequestedAccess { get; }

            public ReportedFileOperation ReportedFileOperation { get; }

            public bool IsAugmented { get; }
        }

        private sealed class RemoveDirectoryOperation
        {
            public RemoveDirectoryOperation(long globalAccessId, string path)
            {
                GlobalAccessId = globalAccessId;
                DirectoryPath = path;
            }

            public long GlobalAccessId { get; }

            public string DirectoryPath { get; }
        }
    }
}
