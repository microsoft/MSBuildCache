
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.Build.Experimental.ProjectCache;
#if NETFRAMEWORK
using Process = Microsoft.MSBuildCache.SourceControl.GitProcess;
#endif

namespace Microsoft.MSBuildCache.SourceControl;

internal static class Git
{
    // UTF8 - NO BOM
    private static readonly Encoding InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<T> RunAsync<T>(
        PluginLoggerBase logger,
        string workingDir, string args,
        Func<StreamWriter, StreamReader, Task<T>> onRunning,
        Func<int, T, T> onExit,
        CancellationToken cancellationToken)
    {
        using Process process = new();
        process.StartInfo.FileName = "git"; // Git is expected to be on the PATH
        process.StartInfo.Arguments = args;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.EnvironmentVariables["GIT_FLUSH"] = "1"; // https://git-scm.com/docs/git#git-codeGITFLUSHcode
        process.StartInfo.WorkingDirectory = workingDir;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardInputEncoding = InputEncoding;

        Stopwatch sw = Stopwatch.StartNew();

        process.Start();

        static void KillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Swallow. This is best-effort
            }
        }

        using (cancellationToken.Register(() => KillProcess(process)))
        {
            using (StreamWriter stdin = process.StandardInput)
            using (StreamReader stdout = process.StandardOutput)
            using (StreamReader stderr = process.StandardError)
            {
                Task<T> resultTask = Task.Run(async () =>
                {
                    try
                    {
                        return await onRunning(stdin, stdout);
                    }
                    finally
                    {
                        stdin.Close();
                    }
                });
                Task<string> errorTask = Task.Run(() => stderr.ReadToEndAsync());

#if NETFRAMEWORK
                process.WaitForExit();
                cancellationToken.ThrowIfCancellationRequested();
#else
                await process.WaitForExitAsync(cancellationToken);
#endif

                if (process.ExitCode == 0)
                {
                    logger.LogMessage($"git.exe {args} (@{process.StartInfo.WorkingDirectory}) took {sw.ElapsedMilliseconds} msec and returned {process.ExitCode}.");
                }
                else
                {
                    logger.LogMessage($"git.exe {args} (@{process.StartInfo.WorkingDirectory}) took {sw.ElapsedMilliseconds} msec and returned {process.ExitCode}. Stderr: {await errorTask}");
                }

                return onExit(process.ExitCode, await resultTask);
            }
        }
    }

    internal static Task HashObjectAsync(string basePath, List<string> filesToRehash, Dictionary<string, byte[]> filehashes, PluginLoggerBase _logger, CancellationToken cancellationToken)
    {
        return RunAsync(
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
}