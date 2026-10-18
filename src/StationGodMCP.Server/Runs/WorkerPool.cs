using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StationGodMCP.Server;

/// <summary>
/// The script workers (docs/architecture/hub.md, Runs): one Python process started ahead of time, so a run does not
/// wait for Python to start. A worker serves one run and exits; taking one starts the next. Each worker is started as
/// <c>python -s -P -c BOOT &lt;dir holding sg&gt;</c> with no PYTHON* variable but
/// PYTHONHASHSEED=0 and PYTHONIOENCODING=utf-8, and on Windows sits in a Job Object that kills
/// it when the hub's process ends, however it ends.
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    /// <summary>sg.worker.BOOT: what the worker runs; it puts the folder named after it on sys.path and serves one run.</summary>
    internal const string Boot = "import sys; sys.path.insert(0, sys.argv[1]); import sg.worker; sg.worker.main()";

    private static readonly TimeSpan ResolveWait = TimeSpan.FromSeconds(20);

    private readonly IReadOnlyList<string> _python;
    private readonly string _sgRoot;
    private readonly KillOnCloseJob? _job;
    private readonly object _gate = new();
    private IReadOnlyList<string>? _resolved;
    private ScriptWorker? _idle;
    private bool _disposed;

    /// <param name="python">The Python command: the program, then the arguments before the worker's own.</param>
    /// <param name="sgRoot">The folder that holds the sg package.</param>
    internal WorkerPool(IReadOnlyList<string> python, string sgRoot)
    {
        _python = python;
        _sgRoot = sgRoot;
        _job = OperatingSystem.IsWindows() ? KillOnCloseJob.Create() : null;
    }

    /// <summary>The folder next to the server binary that the build copies sg into.</summary>
    internal static string ShippedSgRoot => Path.Combine(AppContext.BaseDirectory, "python");

    /// <summary>Starts the idle worker now, so the first run finds one ready.</summary>
    internal void Warm() => _ = Task.Run(StartIdle);

    /// <summary>A started worker for one run; the next is started behind it. Throws when Python cannot be started.</summary>
    internal async Task<ScriptWorker> TakeAsync()
    {
        ScriptWorker? worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            worker = _idle;
            _idle = null;
        }

        if (worker == null || worker.HasExited)
        {
            worker?.Dispose();
            worker = await Task.Run(Start).ConfigureAwait(false);
        }

        Warm();
        return worker;
    }

    public void Dispose()
    {
        ScriptWorker? idle;
        lock (_gate)
        {
            _disposed = true;
            idle = _idle;
            _idle = null;
        }

        idle?.Kill();
        idle?.Dispose();
        _job?.Dispose();
    }

    private void StartIdle()
    {
        lock (_gate)
        {
            if (_disposed || _idle != null)
            {
                return;
            }
        }

        ScriptWorker worker;
        try
        {
            worker = Start();
        }
        catch (Exception)
        {
            // TakeAsync starts one itself and reports why it could not.
            return;
        }

        lock (_gate)
        {
            if (!_disposed && _idle == null)
            {
                _idle = worker;
                return;
            }
        }

        worker.Kill();
        worker.Dispose();
    }

    private ScriptWorker Start()
    {
        // A sidecar copied without its python folder (only the top-level files) has no sg to run.
        if (!File.Exists(Path.Combine(_sgRoot, "sg", "__init__.py")))
        {
            throw new InvalidOperationException(
                $"the sidecar has no sg package at {Path.Combine(_sgRoot, "sg")}: install the whole sidecar, its " +
                "python folder included (extract the zip over it, or build.ps1 -Deploy)");
        }

        IReadOnlyList<string> python = Resolved();
        ProcessStartInfo start = new(python[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath()
        };
        for (int index = 1; index < python.Count; index++)
        {
            start.ArgumentList.Add(python[index]);
        }

        // -s -P instead of -I: -I implies -E, which would ignore PYTHONHASHSEED. The environment is cleaned instead, so
        // no PYTHON* variable (PYTHONPATH, PYTHONHOME, PYTHONSTARTUP, ...) reaches the worker.
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add("-P");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Boot);
        start.ArgumentList.Add(_sgRoot);
        List<string> inherited = [.. start.Environment.Keys];
        foreach (string name in inherited)
        {
            if (name.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(name);
            }
        }

        start.Environment["PYTHONHASHSEED"] = "0";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        Process process = Process.Start(start) ??
                          throw new InvalidOperationException($"'{python[0]}' did not start.");
        _job?.Assign(process);
        return new ScriptWorker(process);
    }

    /// <summary>
    /// The Python executable itself, asked once of the configured command (py -3 is a launcher that would start Python
    /// as its own child, outside the Job Object for a moment and out of reach of a kill); the command as given when the
    /// question cannot be answered.
    /// </summary>
    private IReadOnlyList<string> Resolved()
    {
        lock (_gate)
        {
            if (_resolved != null)
            {
                return _resolved;
            }
        }

        IReadOnlyList<string> resolved = _python;
        try
        {
            // Every standard stream redirected: a child given the hub's own stdin would make process creation wait on
            // the hub's pending read of it (Windows serialises a synchronous pipe handle), so the run would wait for
            // the MCP client's next message.
            ProcessStartInfo ask = new(_python[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            for (int index = 1; index < _python.Count; index++)
            {
                ask.ArgumentList.Add(_python[index]);
            }

            ask.ArgumentList.Add("-I");
            ask.ArgumentList.Add("-c");
            ask.ArgumentList.Add("import sys; print(sys.executable)");
            using Process? process = Process.Start(ask);
            if (process != null)
            {
                process.StandardInput.Close();
                _ = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd().Trim();
                if (process.WaitForExit(ResolveWait) && process.ExitCode == 0 && output.Length > 0 && File.Exists(output))
                {
                    resolved = [output];
                }
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Start reports the same failure with the command as configured.
        }

        lock (_gate)
        {
            _resolved = resolved;
        }

        return resolved;
    }
}

/// <summary>
/// One worker process: its stdout is the protocol from it, its stdin the protocol to it, its stderr kept (the last
/// part) for the reply when it fails to start or dies.
/// </summary>
internal sealed class ScriptWorker : IDisposable
{
    private readonly Process _process;
    private readonly StderrTail _stderr = new();
    private readonly Task _readingStderr;

    internal ScriptWorker(Process process)
    {
        _process = process;
        _readingStderr = Task.Run(ReadStderrAsync);
    }

    /// <summary>What the worker sends: the hub reads it.</summary>
    internal Stream FromWorker => _process.StandardOutput.BaseStream;

    /// <summary>What the worker reads: the hub writes it.</summary>
    internal Stream ToWorker => _process.StandardInput.BaseStream;

    internal bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    /// <summary>The end of what the worker wrote to stderr.</summary>
    internal string StderrTail => _stderr.Text;

    internal void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>Waits for the process to exit, and for its stderr to be read out; false when it outlives the wait.</summary>
    internal async Task<bool> ExitedAsync(TimeSpan wait)
    {
        using CancellationTokenSource bound = new(wait);
        try
        {
            await _process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
            await _readingStderr.WaitAsync(bound.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasExited;
        }
    }

    public void Dispose()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // The pipe is already broken.
        }

        _process.Dispose();
    }

    private async Task ReadStderrAsync()
    {
        char[] buffer = new char[1024];
        try
        {
            StreamReader reader = _process.StandardError;
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                _stderr.Append(buffer, read);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process went away.
        }
    }
}

/// <summary>The last few KB a worker wrote to stderr: enough for a traceback, never the whole of a chatty run.</summary>
internal sealed class StderrTail
{
    private const int Kept = 8 * 1024;
    private const int ShownChars = 1500;

    private readonly object _gate = new();
    private readonly StringBuilder _text = new();

    internal string Text
    {
        get
        {
            lock (_gate)
            {
                string all = _text.ToString().Trim();
                return all.Length <= ShownChars ? all : "..." + all[^ShownChars..];
            }
        }
    }

    internal void Append(char[] buffer, int count)
    {
        lock (_gate)
        {
            _text.Append(buffer, 0, count);
            if (_text.Length > Kept)
            {
                _text.Remove(0, _text.Length - Kept);
            }
        }
    }
}

/// <summary>A Windows Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: its processes die when its last handle closes.</summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private readonly IntPtr _handle;

    private KillOnCloseJob(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>The job, or null when Windows will not make one (the workers are then killed on dispose only).</summary>
    internal static KillOnCloseJob? Create()
    {
        IntPtr handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        ExtendedLimit limits = new() { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        int size = Marshal.SizeOf<ExtendedLimit>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!SetInformationJobObject(handle, ExtendedLimitInformation, memory, (uint)size))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }

        return new KillOnCloseJob(handle);
    }

    internal void Assign(Process process)
    {
        try
        {
            AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // It exited already.
        }
    }

    public void Dispose() => CloseHandle(_handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
