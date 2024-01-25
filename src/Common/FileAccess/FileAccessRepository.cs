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

namespace Microsoft.MSBuildCache.FileAccess;

internal sealed class FileAccessRepository : IDisposable
{
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

                // TODO: Remove or uncomment once we figure out whether we want this.
                // Ignore these operations as they're a bit too spammy for what we need
                //if (operation == ReportedFileOperation.FindFirstFileEx
                //    || operation == ReportedFileOperation.GetFileAttributes
                //    || operation == ReportedFileOperation.GetFileAttributesEx)
                //{
                //    return;
                //}

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

                _logFileStream.WriteLine(isAnAugmentedFileAccess
                    ? $"{processId}, {desiredAccess}, {flagsAndAttributes}, {requestedAccess}, {operation}, {path}, {error} (Augmented)"
                    : $"{processId}, {desiredAccess}, {flagsAndAttributes}, {requestedAccess}, {operation}, {path}, {error}");

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
                else if (requestedAccess == RequestedAccess.Enumerate
                        || requestedAccess == RequestedAccess.EnumerationProbe
                        || requestedAccess == RequestedAccess.Probe)
                {
                    // Don't add enumerations and probes to fileAccessInfo as they are not needed.
                    // We still want to log them for debugging though which is why they're not filtered earlier.
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
            lock (_stateLock)
            {
                _isFinished = true;

                fileTable = _fileTable!;
                deletedDirectories = _deletedDirectories!;

                if (_pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns.Count == 0 &&
                    _pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns.Count == 0 &&
                    _pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns.Count == 0)
                {
                    _logFileStream.Dispose();

                    // Allow memory to be reclaimed
                    _fileTable = null;
                    _deletedDirectories = null;
                }
            }

            return ProcessFileAccesses(fileTable, deletedDirectories);
        }

        private Glob? IsAllowFileAccessAfterProjectFinishFilePatterns(string fileName) =>
            _pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns.FirstOrDefault(p => p.IsMatch(fileName));

        private Glob? IsAllowFileAccessAfterProjectFinishProcessPatterns(string processName) =>
            _pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns.FirstOrDefault(p => p.IsMatch(processName));

        private Glob? IsAllowProcessCloseAfterProjectFinishProcessPatterns(string processName) =>
            _pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns.FirstOrDefault(p => p.IsMatch(processName));

        private static FileAccesses ProcessFileAccesses(
            Dictionary<string, FileAccessInfo> fileTable,
            List<RemoveDirectoryOperation> deletedDirectories)
        {
            var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                inputs.Add(filePath);
            }

            return new FileAccesses(inputs, outputs);
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
