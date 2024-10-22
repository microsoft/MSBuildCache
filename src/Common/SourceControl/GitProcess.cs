#if NETFRAMEWORK
// This is a copy of the Process class and associated classes from .NET Framework, with a customization to allow overriding
// the StandardInputEncoding since it's always Console.InputEncoding in the standard implementation. There are also many
// removals of features we don't use to avoid thousands more lines of copy/paste, as well as some minor fixes to make it
// compile outside of System.dll.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Permissions;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Microsoft.MSBuildCache.SourceControl;

internal sealed class GitProcess : IDisposable
{
    private static readonly object CreateProcessLock = new();

    private SafeProcessHandle? _processHandle;

    private int? _exitCode;

    private StreamReader? _standardOutput;
    private StreamWriter? _standardInput;
    private StreamReader? _standardError;
    private bool _disposed;

    public int ExitCode
    {
        get
        {
            EnsureExited();
            return _exitCode.Value;
        }
    }

    [MemberNotNullWhen(true, nameof(_exitCode))]
    public bool HasExited
    {
        get
        {
            if (!_exitCode.HasValue)
            {
                EnsureAssociated();

                SafeProcessHandle handle = _processHandle;
                if (NativeMethods.GetExitCodeProcess(handle, out int exitCode) && exitCode != NativeMethods.STILL_ACTIVE)
                {
                    _exitCode = exitCode;
                }
                else
                {
                    using ProcessWaitHandle wh = new(handle);
                    if (wh.WaitOne(0, false))
                    {
                        if (!NativeMethods.GetExitCodeProcess(handle, out exitCode))
                        {
                            throw new Win32Exception();
                        }

                        _exitCode = exitCode;
                    }
                }
            }

            return _exitCode.HasValue;
        }
    }

    public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();

    public StreamWriter StandardInput => _standardInput ?? throw new InvalidOperationException("StandardIn has not been redirected or the process hasn't started yet.");

    public StreamReader StandardOutput => _standardOutput ?? throw new InvalidOperationException("StandardOut has not been redirected or the process hasn't started yet.");

    public StreamReader StandardError => _standardError ?? throw new InvalidOperationException("StandardError has not been redirected or the process hasn't started yet.");

    public void Dispose()
    {
        if (!_disposed)
        {
            // Dispose managed and unmanaged resources
            Close();

            _disposed = true;
        }
    }

    public void Close()
    {
        if (_processHandle is not null)
        {
            _processHandle.Close();
            _processHandle = null;

            // Don't call close on the Readers and writers
            // since they might be referenced by somebody else while the 
            // process is still alive but this method called.
            _standardOutput = null;
            _standardInput = null;
            _standardError = null;

            _exitCode = null;
        }
    }

    [MemberNotNull(nameof(_processHandle))]
    private void EnsureAssociated()
    {
        if (_processHandle is null)
        {
            throw new InvalidOperationException("No process is associated with this object.");
        }
    }

    [MemberNotNull(nameof(_exitCode))]
    private void EnsureExited()
    {
        if (!HasExited)
        {
            throw new InvalidOperationException("Process must exit before requested information can be determined.");
        }
    }

    public bool Start()
    {
        Close();
        ProcessStartInfo startInfo = StartInfo;
        if (startInfo.FileName.Length == 0)
        {
            throw new InvalidOperationException("Cannot start process because a file name has not been provided.");
        }

        if (startInfo.UseShellExecute)
        {
            throw new InvalidOperationException("Cannot use UseShellExecute.");
        }
        else
        {
            return StartWithCreateProcess(startInfo);
        }
    }

    private static void CreatePipeWithSecurityAttributes(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, NativeMethods.SECURITY_ATTRIBUTES lpPipeAttributes, int nSize)
    {
        bool ret = NativeMethods.CreatePipe(out hReadPipe, out hWritePipe, lpPipeAttributes, nSize);
        if (!ret || hReadPipe.IsInvalid || hWritePipe.IsInvalid)
        {
            throw new Win32Exception();
        }
    }

    // Using synchronous Anonymous pipes for process input/output redirection means we would end up 
    // wasting a worker threadpool thread per pipe instance. Overlapped pipe IO is desirable, since 
    // it will take advantage of the NT IO completion port infrastructure. But we can't really use 
    // Overlapped I/O for process input/output as it would break Console apps (managed Console class 
    // methods such as WriteLine as well as native CRT functions like printf) which are making an
    // assumption that the console standard handles (obtained via GetStdHandle()) are opened
    // for synchronous I/O and hence they can work fine with ReadFile/WriteFile synchrnously!
    private void CreatePipe(out SafeFileHandle parentHandle, out SafeFileHandle childHandle, bool parentInputs)
    {
        NativeMethods.SECURITY_ATTRIBUTES securityAttributesParent = new NativeMethods.SECURITY_ATTRIBUTES();
        securityAttributesParent.bInheritHandle = true;

        SafeFileHandle? hTmp = null;
        try
        {
            if (parentInputs)
            {
                CreatePipeWithSecurityAttributes(out childHandle, out hTmp, securityAttributesParent, 0);
            }
            else
            {
                CreatePipeWithSecurityAttributes(out hTmp, out childHandle, securityAttributesParent, 0);
            }

            // Duplicate the parent handle to be non-inheritable so that the child process 
            // doesn't have access. This is done for correctness sake, exact reason is unclear.
            // One potential theory is that child process can do something brain dead like 
            // closing the parent end of the pipe and there by getting into a blocking situation
            // as parent will not be draining the pipe at the other end anymore. 
            if (!NativeMethods.DuplicateHandle(new HandleRef(this, NativeMethods.GetCurrentProcess()),
                                                                   hTmp,
                                                                   new HandleRef(this, NativeMethods.GetCurrentProcess()),
                                                                   out parentHandle,
                                                                   0,
                                                                   false,
                                                                   NativeMethods.DUPLICATE_SAME_ACCESS))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            if (hTmp != null && !hTmp.IsInvalid)
            {
                hTmp.Close();
            }
        }
    }

    private static StringBuilder BuildCommandLine(string executableFileName, string arguments)
    {
        // Construct a StringBuilder with the appropriate command line
        // to pass to CreateProcess.  If the filename isn't already 
        // in quotes, we quote it here.  This prevents some security
        // problems (it specifies exactly which part of the string
        // is the file to execute).
        StringBuilder commandLine = new StringBuilder();
        string fileName = executableFileName.Trim();
        bool fileNameIsQuoted = (fileName.StartsWith("\"", StringComparison.Ordinal) && fileName.EndsWith("\"", StringComparison.Ordinal));
        if (!fileNameIsQuoted)
        {
            commandLine.Append("\"");
        }

        commandLine.Append(fileName);

        if (!fileNameIsQuoted)
        {
            commandLine.Append("\"");
        }

        if (!string.IsNullOrEmpty(arguments))
        {
            commandLine.Append(" ");
            commandLine.Append(arguments);
        }

        return commandLine;
    }

    private bool StartWithCreateProcess(ProcessStartInfo startInfo)
    {
        if (startInfo.StandardInputEncoding != null && !startInfo.RedirectStandardInput)
        {
            throw new InvalidOperationException("StandardInputEncoding is only supported when standard input is redirected.");
        }

        if (startInfo.StandardOutputEncoding != null && !startInfo.RedirectStandardOutput)
        {
            throw new InvalidOperationException("StandardOutputEncoding is only supported when standard output is redirected.");
        }

        if (startInfo.StandardErrorEncoding != null && !startInfo.RedirectStandardError)
        {
            throw new InvalidOperationException("StandardErrorEncoding is only supported when standard error is redirected.");
        }

        // See knowledge base article Q190351 for an explanation of the following code.  Noteworthy tricky points:
        //    * The handles are duplicated as non-inheritable before they are passed to CreateProcess so
        //      that the child process can not close them
        //    * CreateProcess allows you to redirect all or none of the standard IO handles, so we use
        //      GetStdHandle for the handles that are not being redirected

        // Cannot start a new process and store its handle if the object has been disposed, since finalization has been suppressed.
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        StringBuilder commandLine = BuildCommandLine(startInfo.FileName, startInfo.Arguments);

        NativeMethods.STARTUPINFO startupInfo = new();
        SafeNativeMethods.PROCESS_INFORMATION processInfo = new();
        SafeProcessHandle procSH = new();
        SafeThreadHandle threadSH = new();
        bool retVal;
        int errorCode = 0;
        // handles used in parent process
        SafeFileHandle? standardInputWritePipeHandle = null;
        SafeFileHandle? standardOutputReadPipeHandle = null;
        SafeFileHandle? standardErrorReadPipeHandle = null;
        GCHandle environmentHandle = new GCHandle();
        lock (CreateProcessLock)
        {
            try
            {
                // set up the streams
                if (startInfo.RedirectStandardInput || startInfo.RedirectStandardOutput || startInfo.RedirectStandardError)
                {
                    if (startInfo.RedirectStandardInput)
                    {
                        CreatePipe(out standardInputWritePipeHandle, out startupInfo.hStdInput, true);
                    }
                    else
                    {
                        startupInfo.hStdInput = new SafeFileHandle(NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE), false);
                    }

                    if (startInfo.RedirectStandardOutput)
                    {
                        CreatePipe(out standardOutputReadPipeHandle, out startupInfo.hStdOutput, false);
                    }
                    else
                    {
                        startupInfo.hStdOutput = new SafeFileHandle(NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE), false);
                    }

                    if (startInfo.RedirectStandardError)
                    {
                        CreatePipe(out standardErrorReadPipeHandle, out startupInfo.hStdError, false);
                    }
                    else
                    {
                        startupInfo.hStdError = new SafeFileHandle(NativeMethods.GetStdHandle(NativeMethods.STD_ERROR_HANDLE), false);
                    }

                    startupInfo.dwFlags = NativeMethods.STARTF_USESTDHANDLES;
                }

                // set up the creation flags paramater
                int creationFlags = 0;
                if (startInfo.CreateNoWindow)
                {
                    creationFlags |= NativeMethods.CREATE_NO_WINDOW;
                }

                // set up the environment block parameter
                creationFlags |= NativeMethods.CREATE_UNICODE_ENVIRONMENT;
                byte[] environmentBytes = CreateEnvironmentBlock(startInfo.EnvironmentVariables);
                environmentHandle = GCHandle.Alloc(environmentBytes, GCHandleType.Pinned);
                IntPtr environmentPtr = environmentHandle.AddrOfPinnedObject();

                string workingDirectory = startInfo.WorkingDirectory;
                if (workingDirectory == string.Empty)
                {
                    workingDirectory = Environment.CurrentDirectory;
                }

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                }
                finally
                {
                    retVal = NativeMethods.CreateProcess(
                            null,               // we don't need this since all the info is in commandLine
                            commandLine,        // pointer to the command line string
                            null,               // pointer to process security attributes, we don't need to inheriat the handle
                            null,               // pointer to thread security attributes
                            true,               // handle inheritance flag
                            creationFlags,      // creation flags
                            environmentPtr,     // pointer to new environment block
                            workingDirectory,   // pointer to current directory name
                            startupInfo,        // pointer to STARTUPINFO
                            processInfo         // pointer to PROCESS_INFORMATION
                        );
                    if (!retVal)
                    {
                        errorCode = Marshal.GetLastWin32Error();
                    }

                    if (processInfo.hProcess != IntPtr.Zero && processInfo.hProcess != NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        procSH.InitialSetHandle(processInfo.hProcess);
                    }

                    if (processInfo.hThread != IntPtr.Zero && processInfo.hThread != NativeMethods.INVALID_HANDLE_VALUE)
                    {
                        threadSH.InitialSetHandle(processInfo.hThread);
                    }
                }
                if (!retVal)
                {
                    if (errorCode == NativeMethods.ERROR_BAD_EXE_FORMAT || errorCode == NativeMethods.ERROR_EXE_MACHINE_TYPE_MISMATCH)
                    {
                        throw new Win32Exception(errorCode, "The specified executable is not a valid application for this OS platform.");
                    }
                    throw new Win32Exception(errorCode);
                }
            }
            finally
            {
                // free environment block
                if (environmentHandle.IsAllocated)
                {
                    environmentHandle.Free();
                }

                startupInfo.Dispose();
            }
        }

        if (startInfo.RedirectStandardInput)
        {
            Encoding enc = (startInfo.StandardInputEncoding != null) ? startInfo.StandardInputEncoding : Console.InputEncoding;
            _standardInput = new StreamWriter(new FileStream(standardInputWritePipeHandle, System.IO.FileAccess.Write, 4096, false), enc, 4096);
            _standardInput.AutoFlush = true;
        }
        if (startInfo.RedirectStandardOutput)
        {
            Encoding enc = (startInfo.StandardOutputEncoding != null) ? startInfo.StandardOutputEncoding : Console.OutputEncoding;
            _standardOutput = new StreamReader(new FileStream(standardOutputReadPipeHandle, System.IO.FileAccess.Read, 4096, false), enc, true, 4096);
        }
        if (startInfo.RedirectStandardError)
        {
            Encoding enc = (startInfo.StandardErrorEncoding != null) ? startInfo.StandardErrorEncoding : Console.OutputEncoding;
            _standardError = new StreamReader(new FileStream(standardErrorReadPipeHandle, System.IO.FileAccess.Read, 4096, false), enc, true, 4096);
        }

        bool ret = false;
        if (!procSH.IsInvalid)
        {
            _processHandle = procSH;
            threadSH.Close();
            ret = true;
        }

        return ret;
    }

    public void Kill()
    {
        EnsureAssociated();

        SafeProcessHandle handle = _processHandle;
        if (!NativeMethods.TerminateProcess(handle, -1))
        {
            throw new Win32Exception();
        }
    }

    public bool WaitForExit()
    {
        EnsureAssociated();

        SafeProcessHandle handle = _processHandle;
        using ProcessWaitHandle waitHandle = new(handle);
        return waitHandle.WaitOne(-1, false);
    }

    private static byte[] CreateEnvironmentBlock(Dictionary<string, string> sd)
    {
        // get the keys
        string[] keys = new string[sd.Count];
        sd.Keys.CopyTo(keys, 0);

        // get the values
        string[] values = new string[sd.Count];
        sd.Values.CopyTo(values, 0);

        // sort both by the keys
        // Windows 2000 requires the environment block to be sorted by the key
        // It will first converting the case the strings and do ordinal comparison.
        Array.Sort(keys, values, StringComparer.OrdinalIgnoreCase);

        // create a list of null terminated "key=val" strings
        StringBuilder stringBuff = new StringBuilder();
        for (int i = 0; i < sd.Count; ++i)
        {
            stringBuff.Append(keys[i]);
            stringBuff.Append('=');
            stringBuff.Append(values[i]);
            stringBuff.Append('\0');
        }
        // an extra null at the end indicates end of list.
        stringBuff.Append('\0');

        return Encoding.Unicode.GetBytes(stringBuff.ToString());
    }

    internal sealed class ProcessStartInfo
    {
        internal ProcessStartInfo()
        {
            // Initialize the child environment block with all the parent variables
            IDictionary envVars = Environment.GetEnvironmentVariables();
            EnvironmentVariables = new Dictionary<string, string>(envVars.Count, StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in envVars)
            {
                EnvironmentVariables.Add((string)entry.Key, (string)entry.Value);
            }
        }

        public string Arguments { get; set; } = string.Empty;

        public bool CreateNoWindow { get; set; }

        public Dictionary<string, string> EnvironmentVariables { get; set; }

        public bool RedirectStandardInput { get; set; }

        public bool RedirectStandardOutput { get; set; }

        public bool RedirectStandardError { get; set; }

        public Encoding? StandardErrorEncoding { get; set; }

        public Encoding? StandardOutputEncoding { get; set; }

        public Encoding? StandardInputEncoding { get; set; }

        public bool UseShellExecute { get; set; }

        public string FileName { get; set; } = string.Empty;

        public string WorkingDirectory { get; set; } = string.Empty;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle processHandle) : base()
        {
            bool succeeded = NativeMethods.DuplicateHandle(
                new HandleRef(this, NativeMethods.GetCurrentProcess()),
                processHandle,
                new HandleRef(this, NativeMethods.GetCurrentProcess()),
                out SafeWaitHandle waitHandle,
                0,
                false,
                NativeMethods.DUPLICATE_SAME_ACCESS);

            if (!succeeded)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            SafeWaitHandle = waitHandle;
        }
    }

    [SuppressUnmanagedCodeSecurity]
    private sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal static SafeProcessHandle InvalidHandle = new SafeProcessHandle(IntPtr.Zero);

        // Note that OpenProcess returns 0 on failure

        internal SafeProcessHandle() : base(true) { }

        internal SafeProcessHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        [SecurityPermission(SecurityAction.LinkDemand, UnmanagedCode = true)]
        public SafeProcessHandle(IntPtr existingHandle, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(existingHandle);
        }

        internal void InitialSetHandle(IntPtr h) => SetHandle(h);

        override protected bool ReleaseHandle() => SafeNativeMethods.CloseHandle(handle);
    }

    [SuppressUnmanagedCodeSecurity]
    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeThreadHandle() : base(true)
        {
        }

        internal void InitialSetHandle(IntPtr h) => SetHandle(h);

        override protected bool ReleaseHandle() => SafeNativeMethods.CloseHandle(handle);
    }

    [HostProtection(MayLeakOnAbort = true)]
    [SuppressUnmanagedCodeSecurity]
    private static class SafeNativeMethods
    {
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        internal class PROCESS_INFORMATION
        {
            // The handles in PROCESS_INFORMATION are initialized in unmanaged functions.
            // We can't use SafeHandle here because Interop doesn't support [out] SafeHandles in structures/classes yet.            
            public IntPtr hProcess = IntPtr.Zero;
            public IntPtr hThread = IntPtr.Zero;
            public int dwProcessId = 0;
            public int dwThreadId = 0;

            // Note this class makes no attempt to free the handles
            // Use InitialSetHandle to copy to handles into SafeHandles
        }
    }

    [HostProtection(MayLeakOnAbort = true)]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool TerminateProcess(SafeProcessHandle processHandle, int exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GetExitCodeProcess(SafeProcessHandle processHandle, out int exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        public static extern bool DuplicateHandle(
            HandleRef hSourceProcessHandle,
            SafeHandle hSourceHandle,
            HandleRef hTargetProcess,
            out SafeFileHandle targetHandle,
            int dwDesiredAccess,
            bool bInheritHandle,
            int dwOptions
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        public static extern bool DuplicateHandle(
            HandleRef hSourceProcessHandle,
            SafeHandle hSourceHandle,
            HandleRef hTargetProcess,
            out SafeWaitHandle targetHandle,
            int dwDesiredAccess,
            bool bInheritHandle,
            int dwOptions
        );

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr GetStdHandle(int whichHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true, BestFitMapping = false)]
        public static extern bool CreateProcess(
            [MarshalAs(UnmanagedType.LPTStr)]
            string? lpApplicationName,                  // LPCTSTR
            StringBuilder lpCommandLine,                // LPTSTR - note: CreateProcess might insert a null somewhere in this string
            SECURITY_ATTRIBUTES? lpProcessAttributes,   // LPSECURITY_ATTRIBUTES
            SECURITY_ATTRIBUTES? lpThreadAttributes,    // LPSECURITY_ATTRIBUTES
            bool bInheritHandles,                       // BOOL
            int dwCreationFlags,                        // DWORD
            IntPtr lpEnvironment,                       // LPVOID
            [MarshalAs(UnmanagedType.LPTStr)]
            string lpCurrentDirectory,                  // LPCTSTR
            STARTUPINFO lpStartupInfo,                  // LPSTARTUPINFO
            SafeNativeMethods.PROCESS_INFORMATION lpProcessInformation    // LPPROCESS_INFORMATION
        );

        public const int STARTF_USESTDHANDLES = 0x00000100;

        public const int STD_INPUT_HANDLE = -10;
        public const int STD_OUTPUT_HANDLE = -11;
        public const int STD_ERROR_HANDLE = -12;

        public const int STILL_ACTIVE = 0x00000103;

        public const int DUPLICATE_SAME_ACCESS = 2;

        public const int CREATE_NO_WINDOW = 0x08000000;
        public const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        public const int ERROR_BAD_EXE_FORMAT = 193;
        public const int ERROR_EXE_MACHINE_TYPE_MISMATCH = 216;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal class STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved = IntPtr.Zero;
            public IntPtr lpDesktop = IntPtr.Zero;
            public IntPtr lpTitle = IntPtr.Zero;
            public int dwX = 0;
            public int dwY = 0;
            public int dwXSize = 0;
            public int dwYSize = 0;
            public int dwXCountChars = 0;
            public int dwYCountChars = 0;
            public int dwFillAttribute = 0;
            public int dwFlags = 0;
            public short wShowWindow = 0;
            public short cbReserved2 = 0;
            public IntPtr lpReserved2 = IntPtr.Zero;
            public SafeFileHandle? hStdInput = new SafeFileHandle(IntPtr.Zero, false);
            public SafeFileHandle? hStdOutput = new SafeFileHandle(IntPtr.Zero, false);
            public SafeFileHandle? hStdError = new SafeFileHandle(IntPtr.Zero, false);

            public STARTUPINFO()
            {
                cb = Marshal.SizeOf(this);
            }

            public void Dispose()
            {
                // close the handles created for child process
                if (hStdInput != null && !hStdInput.IsInvalid)
                {
                    hStdInput.Close();
                    hStdInput = null;
                }

                if (hStdOutput != null && !hStdOutput.IsInvalid)
                {
                    hStdOutput.Close();
                    hStdOutput = null;
                }

                if (hStdError != null && !hStdError.IsInvalid)
                {
                    hStdError.Close();
                    hStdError = null;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal class SECURITY_ATTRIBUTES
        {
            public int nLength = 12;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle = false;
        }
    }
}

#endif
