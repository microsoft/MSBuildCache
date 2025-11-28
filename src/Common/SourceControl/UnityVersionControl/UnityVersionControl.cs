
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache.SourceControl.UnityVersionControl
{
    internal static class UnityVersionControl
    {
        public static async Task<T> RunAsync<T>(
        PluginLoggerBase logger,
        string workingDir, string args,
        Func<StreamReader, Task<T>> onRunning,
        Func<int, T, T> onExit,
        CancellationToken cancellationToken)
        {
            using Process process = new();
            process.StartInfo.FileName = "cm"; // Unity Version Control command line "cm" is expected to be on the PATH
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WorkingDirectory = workingDir;
            process.StartInfo.RedirectStandardInput = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;

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
                        return await onRunning(stdout);
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
                        logger.LogMessage($"cm.exe {args} (@{process.StartInfo.WorkingDirectory}) took {sw.ElapsedMilliseconds} msec and returned {process.ExitCode}.");
                    }
                    else
                    {
                        logger.LogMessage($"cm.exe {args} (@{process.StartInfo.WorkingDirectory}) took {sw.ElapsedMilliseconds} msec and returned {process.ExitCode}. Stderr: {await errorTask}");
                    }

                    return onExit(process.ExitCode, await resultTask);
                }
            }
        }
    }
}
