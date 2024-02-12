
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache.SourceControl;

public static class Git
{
#if NETFRAMEWORK
    private static readonly object InputEncodingLock = new object();
#endif

    // UTF8 - NO BOM
    private static readonly Encoding InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<string> BranchNameAsync(PluginLoggerBase logger, string repoRoot)
    {
        string branchName = await RunAsync(logger, repoRoot, "rev-parse --abbrev-ref HEAD",
            (_, stdout) => stdout.ReadToEndAsync(),
            (exitCode, result) => result,
            CancellationToken.None);
        return branchName.Trim();
    }

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

        Stopwatch sw = Stopwatch.StartNew();

#if NETFRAMEWORK
        // In .NET Framework the StandardInputEncoding is always Console.InputEncoding and determines at process start time.
        // Because we need to redirect StandardInputEncoding, temporarily set Console.InputEncoding to what we need until the
        // process is started. Use a lock to avoid collisions.
        lock (InputEncodingLock)
        {
            Encoding originalConsoleInputEncoding = Console.InputEncoding;
            try
            {
                Console.InputEncoding = InputEncoding;

                process.Start();
            }
            finally
            {
                Console.InputEncoding = originalConsoleInputEncoding;
            }
        }
#else
        process.StartInfo.StandardInputEncoding = InputEncoding;

        process.Start();
#endif

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
        };
    }
}