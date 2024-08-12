// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache.SourceControl;

public sealed class GitParsingException : Exception
{
    public GitParsingException()
    {
    }

    public GitParsingException(string message)
        : base(message)
    {
    }

    public GitParsingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class GitFileHashProvider : ISourceControlFileHashProvider
{
    private static readonly char[] LineDelimiters = { '\n' };

    private static readonly char[] SegmentDelimiters = { ' ', '\t' };

    private readonly PluginLoggerBase _logger;

    public GitFileHashProvider(PluginLoggerBase logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Git all files known to git from ls-files.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> GetFileHashesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        // Kick off grabbing the hashes for the "main/root" module
        Task<Dictionary<string, byte[]>> hashesTask = GetModuleFileHashesAsync(repoRoot, cancellationToken);

        // The common case is to not have any submodules, so just short-circuit
        if (!File.Exists(Path.Combine(repoRoot, ".gitmodules")))
        {
            return await hashesTask;
        }

        // Iterate through the initialized submodules and add those hashes
        List<string> submodules = await GetInitializedSubmodulesAsync(repoRoot, cancellationToken);

        if (submodules.Count == 0)
        {
            // The .gitmodules file existed, but there aren't actually any submodules.
            return await hashesTask;
        }

        // Fetch all submodule file hashes in parallel.
        Task<Dictionary<string, byte[]>>[] subModuleHashesTasks = submodules
            .Select(submodule => GetModuleFileHashesAsync(Path.Combine(repoRoot, submodule), cancellationToken))
            .ToArray();

        Dictionary<string, byte[]> hashes = await hashesTask;

        // Git provides a hash for each of the submodules because in the index, the submodule
        // is modeled as a symlink file where the hash is the commit ID that the submodule is
        // pinned to -- we need to remove the hash from the result set
        foreach (string submodule in submodules)
        {
            hashes.Remove(submodule);
        }

        // Aggregate in all submodule hashes
        IReadOnlyDictionary<string, byte[]>[] subModuleHashes = await Task.WhenAll(subModuleHashesTasks);
        foreach (IReadOnlyDictionary<string, byte[]> h in subModuleHashes)
        {
            foreach (KeyValuePair<string, byte[]> entry in h)
            {
                hashes.Add(entry.Key, entry.Value);
            }
        }

        return hashes;
    }

    private async Task<Dictionary<string, byte[]>> GetModuleFileHashesAsync(string basePath, CancellationToken cancellationToken)
    {
        return await Git.RunAsync(
            _logger,
            workingDir: basePath,
            "ls-files -z -cmos --exclude-standard",
            (_, stdout) => Task.Run(() => ParseGitLsFiles(basePath, stdout, (filesToRehash, fileHashes) => GitHashObjectAsync(basePath, filesToRehash, fileHashes, cancellationToken))),
            (exitCode, result) =>
            {
                if (exitCode != 0)
                {
                    throw new SourceControlHashException("git ls-files failed with exit code  " + exitCode);
                }

                return result;
            },
            cancellationToken);
    }

    internal async Task<Dictionary<string, byte[]>> ParseGitLsFiles(
        string basePath,
        TextReader gitOutput,
        Func<List<string>, Dictionary<string, byte[]>, Task> hasher)
    {
        // 100644<space>05e33066454fb2823990688888877b0c2856a56e<space>0<tab>foo.txt<null>
        // GeneralFormat is that each file entry is delimited by a null character because we're using -z
        // file paths are relative and have / instead of \ but otherwise unmodified (-z is important here)
        // and delimited by a tab from other staging info (if there is any)
        // staging info which includes the hash are space delimited. See UT's for more examples
        using var reader = new GitLsFileOutputReader(gitOutput);
        var fileHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var filesToRehash = new List<string>();
        StringBuilder? line;
        while ((line = reader.ReadLine()) != null)
        {
            // tab's can't be part of filenames https://msdn.microsoft.com/en-us/library/windows/desktop/aa365247(v=vs.85).aspx
            int tabIdx = 0;
            while (tabIdx < line.Length && line[tabIdx] != '\t') // line.IndexOf('\t')
            {
                tabIdx++;
            }
            if (tabIdx == line.Length) // untracked files are just the path name.
            {
                line.Replace('/', '\\');
                string file = Path.Combine(basePath, line.ToString());
                _logger.LogMessage($"Found untracked file {file}.");
                filesToRehash.Add(file);
                if (fileHashes.ContainsKey(file))
                {
                    throw new GitParsingException(file + " already had a hash this doesn't make sense");
                }
            }
            else // 100644 9f4ca612082be4ed7055ccd6f1e005ede8a211f3 0       InternalsVisibleTo.cs
            {
                int pathLength = line.Length - tabIdx - 1;
                line.Replace('/', '\\', tabIdx + 1, pathLength);
                string file = Path.Combine(basePath, line.ToString(tabIdx + 1, pathLength));
                if (fileHashes.ContainsKey(file))
                {
                    // If we already saw this it means it's a previously committed/added file that's been modified
                    _logger.LogMessage($"Found modified file {file}.");
                    filesToRehash.Add(file);
                    continue;
                }

                int startIdx = 0;
                while (startIdx < tabIdx && line[startIdx] != ' ') // line.IndexOf(' ', startIdx)
                {
                    startIdx++;
                }

                int endIdx = ++startIdx;
                while (endIdx < tabIdx && line[endIdx] != ' ') // line.IndexOf(' ', endIdx)
                {
                    endIdx++;
                }

                if (startIdx >= tabIdx || endIdx >= tabIdx)
                {
                    throw new GitParsingException("expect staging bits to have 3 parts seperated by spaces " + line.ToString());
                }

                fileHashes[file] = HexUtilities.HexToBytes(line.ToString(startIdx, endIdx - startIdx));
            }
        }

        if (filesToRehash.Count > 0)
        {
            Stopwatch sw = Stopwatch.StartNew();
            // we could do this as new files come in just not clear it's worth it
            await hasher(filesToRehash, fileHashes);
            _logger.LogMessage($"{fileHashes.Count} files Rehashing {filesToRehash.Count} modified files took {sw.ElapsedMilliseconds} msec");
        }

        return fileHashes;
    }

    internal Task GitHashObjectAsync(string basePath, List<string> filesToRehash, Dictionary<string, byte[]> filehashes, CancellationToken cancellationToken)
    {
        return Git.RunAsync(
            _logger,
            workingDir: basePath,
            "hash-object --stdin-paths",
            async (stdin, stdout) =>
            {
                foreach (string file in filesToRehash)
                {
                    string? gitHashOfFile;

                    if (File.Exists(file))
                    {
                        await stdin.WriteLineAsync(file);
                        gitHashOfFile = await stdout.ReadLineAsync();

                        if (string.IsNullOrWhiteSpace(gitHashOfFile))
                        {
                            _logger.LogMessage($"git hash-object returned an empty string for {file}. Forcing a cache miss by using a Guid");

                            // Guids are only 32 characters and git hashes are 40. Prepend 8 characters to match and to generally be recognizable.
                            gitHashOfFile = "bad00000" + Guid.NewGuid().ToString("N");
                        }
                    }
                    else
                    {
                        gitHashOfFile = null;
                    }

                    filehashes[file] = HexUtilities.HexToBytes(gitHashOfFile);
                }

                return Unit.Void;
            },
            (exitCode, result) =>
            {
                if (exitCode != 0)
                {
                    throw new SourceControlHashException("git ls-files failed with exit code  " + exitCode);
                }

                return result;
            },
            cancellationToken);
    }

    private Task<List<string>> GetInitializedSubmodulesAsync(string repoRoot, CancellationToken cancellationToken)
    {
        string stdErrString = string.Empty;
        return Git.RunAsync(
            _logger,
            workingDir: repoRoot,
            "submodule status --recursive",
            async (stdin, stdout) =>
            {
                string stdOutString = await stdout.ReadToEndAsync();
                return ParseGitSubmoduleStatus(stdOutString);
            },
            (exitCode, result) =>
            {
                if (exitCode == 0)
                {
                    return result;
                }
                {
                    // Errors from "git submodule status" are ignored here and an empty submodule set is returned;
                    // so we drop an error in the log file and include the text from StdErr
                    return new List<string>();
                }
            },
            cancellationToken);
    }

    internal List<string> ParseGitSubmoduleStatus(string output)
    {
        // <[\s|+|-|U]>05e33066454fb2823990688888877b0c2856a56e<space>submodule/path<tab>(sha1-description)
        string[] moduleStatus = output.Split(LineDelimiters, StringSplitOptions.RemoveEmptyEntries);
        var submodules = new List<string>();

        foreach (string module in moduleStatus)
        {
            string[] entry = module.Split(SegmentDelimiters, 2, StringSplitOptions.RemoveEmptyEntries);
            if (entry.Length != 2)
            {
                // Some versions of git.exe add an extra lf at the end of the results for "better display" according
                // to the github logs for that version; we need to ignore empty lines or improperly formatted ones...
                continue;
            }

            if (entry[0][0] == '-')
            {
                // Skip any uninitialized submodules
                continue;
            }

            // first segment is the commit ID
            int index = entry[0][0] == '+' || entry[0][0] == 'U' ? 1 : 0;
            string commit = entry[0].Substring(index);

            // Second segment is the path; but to isolate it, we have to trim off the third
            // segment which is the sha1 description for the commit ID; keep the description
            index = entry[1].LastIndexOfAny(SegmentDelimiters);
            if (index <= 0)
            {
                // Again, the we simply are going to ignore any malformatted subtrings and exclude
                // them from the list of submodules.
                continue;
            }

            // Peel off the description...
            string description = entry[1].Substring(index).Trim();

            // ...then grab the path
            string modulePath = entry[1].Substring(0, index);
            submodules.Add(modulePath.Replace('/', '\\'));
            _logger.LogMessage($"Found submodule: {modulePath}.{commit}.{description}");
        }

        return submodules;
    }

    private sealed class GitLsFileOutputReader : IDisposable
    {
        readonly BlockingCollection<StringBuilder> _lines = new BlockingCollection<StringBuilder>();

        public GitLsFileOutputReader(TextReader reader)
        {
            Task.Run(() => PopulateAsync(reader));
        }

        private void PopulateAsync(TextReader reader)
        {
            int overflowLength = 0;
            var buffer = new char[4096]; // must be large enough to hold at least one line of output
            while (true)
            {
                int readCnt = reader.Read(buffer, overflowLength, buffer.Length - overflowLength);
                if (readCnt == 0) // end of stream
                {
                    if (overflowLength > 0)
                    {
                        _lines.Add(new StringBuilder(overflowLength).Append(buffer, 0, overflowLength));
                    }
                    _lines.CompleteAdding();
                    return;
                }

                readCnt += overflowLength;
                int startIdx = 0, eolIdx;
                while (startIdx < readCnt && (eolIdx = Array.IndexOf(buffer, '\0', startIdx)) != -1)
                {
                    int lineLength = eolIdx - startIdx;
                    if (overflowLength > 0)
                    {
                        overflowLength = 0;
                        startIdx = 0;
                    }
                    _lines.Add(new StringBuilder(lineLength).Append(buffer, startIdx, lineLength));
                    startIdx = eolIdx + 1;
                }
                if (startIdx < readCnt)
                {
                    if (overflowLength > 0) // we already have some overflow left, but the line could not fit the buffer
                    {
                        throw new InvalidDataException($"Internal: git ls-files output line length {readCnt - startIdx} exceeds {nameof(buffer)} size {buffer.Length}. Increase the latter.");
                    }
                    overflowLength = readCnt - startIdx;
                    Array.Copy(buffer, startIdx, buffer, 0, overflowLength);
                }
            }
        }

        public StringBuilder? ReadLine()
        {
            while (!_lines.IsCompleted)
            {
                if (_lines.TryTake(out StringBuilder? result, -1))
                {
                    return result;
                }
            }
            return null;
        }

        public void Dispose()
        {
            _lines.Dispose();
        }
    }
}
