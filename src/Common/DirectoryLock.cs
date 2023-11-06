// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache;

/// <summary>
/// This is loosely based on BXL's DirectoryLockFile, but with a fail-fast mode of operation.
/// </summary>
internal sealed class DirectoryLock : IDisposable
{
    private readonly string _lockFilePath;
    private readonly PluginLoggerBase _logger;
    private Stream? _lockFile;

    public DirectoryLock(string lockFilePath, PluginLoggerBase logger)
    {
        _lockFilePath = lockFilePath;
        _logger = logger;
    }

    public bool Acquire()
    {
        if (_lockFile != null)
        {
            _logger.LogMessage($"Lock file already held=[{_lockFilePath}]", MessageImportance.Low);
            return true;
        }

        _logger.LogMessage($"Acquiring lock file=[{_lockFilePath}]", MessageImportance.Low);
        Directory.CreateDirectory(Path.GetDirectoryName(_lockFilePath)!);

        try
        {
            _lockFile = File.Open(_lockFilePath, FileMode.OpenOrCreate, System.IO.FileAccess.Write, FileShare.Read);
            _logger.LogMessage($"Acquired lock file=[{_lockFilePath}]", MessageImportance.Low);
            return true;
        }
        catch (IOException)
        {
            // Swallow, someone else is holding the lock
        }
        catch (UnauthorizedAccessException)
        {
            // Swallow, someone else is holding the lock
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_lockFile != null)
        {
            _lockFile.Dispose();
            _lockFile = null;
        }
    }
}
