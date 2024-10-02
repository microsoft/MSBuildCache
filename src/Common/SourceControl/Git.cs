
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
#if !NETFRAMEWORK
        process.StartInfo.StandardInputEncoding = InputEncoding;
#endif

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
#if NETFRAMEWORK
            // In .NET Framework the StandardInputEncoding cannot be set and is always Console.InputEncoding.
            // To work around, wrap the underlying stream in a writer with the correct encoding.
            using (StreamWriter stdin = new StreamWriter(process.StandardInput.BaseStream, InputEncoding, 4096))
#else
            using (StreamWriter stdin = process.StandardInput)
#endif
            using (StreamReader stdout = process.StandardOutput)
            using (StreamReader stderr = process.StandardError)
            {
                stdin.AutoFlush = true;

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