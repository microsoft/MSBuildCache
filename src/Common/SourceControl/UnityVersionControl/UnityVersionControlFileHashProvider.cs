using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache.SourceControl.UnityVersionControl
{
    internal class UnityVersionControlFileHashProvider : ISourceControlFileHashProvider
    {
        private readonly PluginLoggerBase _logger;
        public UnityVersionControlFileHashProvider(PluginLoggerBase logger)
        {
            _logger = logger;
        }
        public async Task<IReadOnlyDictionary<string, byte[]>> GetFileHashesAsync(string repoRoot, CancellationToken cancellationToken)
        {
            Task<Dictionary<string, byte[]>> hashesTask = GetRepoFileHashesAsync(repoRoot, cancellationToken);
            return await hashesTask;
        }

        private async Task<Dictionary<string, byte[]>> GetRepoFileHashesAsync(string basePath, CancellationToken cancellationToken)
        {
            return await UnityVersionControl.RunAsync(_logger, workingDir: basePath, "ls -R --format=\"{path}\t{hash}\"",
                async (stdout) => await ParseUnityLsFiles(stdout, (filesToRehash, fileHashes) => GitHashObjectAsync(basePath, filesToRehash, fileHashes, cancellationToken)),
                (exitCode, result) =>
                {
                    if (exitCode != 0)
                    {
                        throw new SourceControlHashException("cm ls failed with exit code " + exitCode);
                    }

                    return result;
                },
                cancellationToken);
        }

        internal async Task<Dictionary<string, byte[]>> ParseUnityLsFiles(
        TextReader cmOutput,
        Func<List<string>, Dictionary<string, byte[]>, Task> hasher)
        {
            // relativePathInRepository<tab>hash
            _logger.LogMessage("Begin reading in the output");
            using var reader = new UnityVersionContorlLsFileOutputReader(cmOutput);
            _logger.LogMessage("Begin parsing the read output");
            var fileHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var filesToRehash = new List<string>();
            StringBuilder? line;
            while ((line = reader.ReadLine()) != null)
            {
                var splitLine = line.ToString().Split('\t');
                string file = splitLine[0];
                if (splitLine.Length > 1 && splitLine[1].Length > 0)
                {
                    string hash = splitLine[1];
                    if (hash.Length == 0)
                    {
                        throw new InvalidOperationException("hash cant be of zero length");
                    }
                    fileHashes[file] = HexUtilities.Base64ToBytes(hash);
                }
                else
                {
                    _logger.LogMessage($"{file} is missing a hash and will be rehashed.");
                    filesToRehash.Add(file);
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
                        throw new SourceControlHashException("git hash-object failed with exit code  " + exitCode);
                    }

                    return result;
                },
                cancellationToken);
        }

        private sealed class UnityVersionContorlLsFileOutputReader : IDisposable
        {
            readonly BlockingCollection<StringBuilder> _lines = new BlockingCollection<StringBuilder>();

            public UnityVersionContorlLsFileOutputReader(TextReader reader)
            {
                PopulateAsync(reader);
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
                    while (startIdx < readCnt && (eolIdx = Array.IndexOf(buffer, '\n', startIdx)) != -1)
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
                            throw new InvalidDataException($"Internal: cm ls output line length {readCnt - startIdx} exceeds {nameof(buffer)} size {buffer.Length}. Increase the latter.");
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

}
