#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace StationGodMCP.Pure.Profiling;

/// <summary>A CSV row: one slow frame, or one second's summary.</summary>
internal enum ProfileRowKind
{
    Frame,
    Second
}

/// <summary>
/// One CSV row as the main thread hands it over: fixed fields only, names as references to strings that never change
/// (scope names, call method names), so enqueueing allocates nothing. HeapDeltaBytes is null when the heap shrank.
/// </summary>
internal readonly struct ProfileRow
{
    internal ProfileRow(ProfileRowKind kind, long utcTicks, long frame, int frames, double ms, double maxMs,
        long? heapDeltaBytes, int calls, NamedSlot top1, NamedSlot top2, NamedSlot top3, NamedSlot slowestCall)
    {
        Kind = kind;
        UtcTicks = utcTicks;
        Frame = frame;
        Frames = frames;
        Ms = ms;
        MaxMs = maxMs;
        HeapDeltaBytes = heapDeltaBytes;
        Calls = calls;
        Top1 = top1;
        Top2 = top2;
        Top3 = top3;
        SlowestCall = slowestCall;
    }

    internal ProfileRowKind Kind { get; }

    internal long UtcTicks { get; }

    /// <summary>The frame (a second: its last frame).</summary>
    internal long Frame { get; }

    internal int Frames { get; }

    /// <summary>StationGod's main-thread ms: the frame's, or the second's total.</summary>
    internal double Ms { get; }

    internal double MaxMs { get; }

    internal long? HeapDeltaBytes { get; }

    internal int Calls { get; }

    internal NamedSlot Top1 { get; }

    internal NamedSlot Top2 { get; }

    internal NamedSlot Top3 { get; }

    internal NamedSlot SlowestCall { get; }
}

/// <summary>A name with its milliseconds, or none (Name null).</summary>
internal readonly struct NamedSlot
{
    internal NamedSlot(string? name, double ms)
    {
        Name = name;
        Ms = ms;
    }

    internal string? Name { get; }

    internal double Ms { get; }
}

/// <summary>
/// The rolling profile CSV. The main thread enqueues rows into a bounded ring (TryEnqueue; a full ring drops the row
/// and counts it); a background thread, the only one, writes them (Drain) and touches no Unity object. Files are
/// profile-stamp.csv, then profile-stamp-2.csv and on: a file past CapBytes is closed and the next row opens the next
/// part, and only the newest KeptFiles parts of this session are kept (older ones of the session are deleted where
/// they can be). A file name already taken is skipped, never written over. A write that fails stops the writer; the
/// failure is reported and later rows count as dropped.
/// </summary>
internal sealed class ProfileCsvWriter
{
    internal const long DefaultCapBytes = 10L * 1024 * 1024;
    internal const int DefaultKeptFiles = 3;
    internal const int DefaultQueueRows = 1024;

    internal const string Header =
        "kind,utc,frame,frames,stationgod_ms,max_frame_ms,heap_delta_bytes,calls,top1,top1_ms,top2,top2_ms,top3,top3_ms," +
        "slowest_call,slowest_call_ms";

    private readonly object _gate = new object();
    private readonly ProfileRow[] _queue;
    private readonly ProfileRow[] _batch;
    private readonly string _directory;
    private readonly string _stamp;
    private readonly long _capBytes;
    private readonly int _keptFiles;
    private readonly Queue<string> _files = new Queue<string>();
    private readonly AutoResetEvent _wake = new AutoResetEvent(false);
    private readonly StringBuilder _line = new StringBuilder(256);
    private int _head;
    private int _count;
    private long _dropped;
    private long _written;
    private StreamWriter? _out;
    private long _bytes;
    private int _part;
    private volatile string _path;
    private volatile string? _failure;
    private volatile bool _stopping;
    private bool _started;

    internal ProfileCsvWriter(string directory, string stamp, long capBytes = DefaultCapBytes,
        int keptFiles = DefaultKeptFiles, int queueRows = DefaultQueueRows)
    {
        _directory = directory;
        _stamp = stamp;
        _capBytes = capBytes;
        _keptFiles = Math.Max(1, keptFiles);
        _queue = new ProfileRow[queueRows];
        _batch = new ProfileRow[queueRows];
        _path = PartPath(1);
    }

    /// <summary>The file rows go to (or went to last).</summary>
    internal string Path => _path;

    internal long Dropped => Interlocked.Read(ref _dropped);

    internal long Written => Interlocked.Read(ref _written);

    internal string? Failure => _failure;

    internal CsvState State() => new CsvState(_path, !_stopping && _failure == null, Written, Dropped, _failure);

    /// <summary>
    /// Main thread: queues a row, or drops and counts it when the ring is full or writing failed; after Stop rows are
    /// refused uncounted.
    /// </summary>
    internal bool TryEnqueue(in ProfileRow row)
    {
        if (_stopping)
        {
            return false;
        }

        lock (_gate)
        {
            if (_count == _queue.Length || _failure != null)
            {
                _dropped++;
                return false;
            }

            _queue[(_head + _count) % _queue.Length] = row;
            _count++;
        }

        return true;
    }

    /// <summary>Starts the background writer.</summary>
    internal void Start()
    {
        Thread thread = new Thread(Run) { IsBackground = true, Name = "StationGodMCP profile CSV" };
        _started = true;
        thread.Start();
    }

    /// <summary>
    /// Asks the writer to write what is queued, close its file and end; does not wait for it. The wake event is
    /// disposed by the writer as it ends, or here when it never started; a second Stop does nothing.
    /// </summary>
    internal void Stop()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            if (_started)
            {
                _wake.Set();
            }
            else
            {
                _wake.Dispose();
            }
        }
    }

    /// <summary>Writes every queued row; the writer thread's work (tests call it directly). Returns rows written.</summary>
    internal int Drain()
    {
        int taken;
        lock (_gate)
        {
            taken = _count;
            for (int index = 0; index < taken; index++)
            {
                _batch[index] = _queue[(_head + index) % _queue.Length];
            }

            _head = (_head + taken) % _queue.Length;
            _count = 0;
        }

        int written = 0;
        for (int index = 0; index < taken; index++)
        {
            if (_failure != null)
            {
                Interlocked.Add(ref _dropped, taken - index);
                break;
            }

            if (Write(in _batch[index]))
            {
                written++;
            }
        }

        _out?.Flush();
        Interlocked.Add(ref _written, written);
        return written;
    }

    /// <summary>Closes the current file (the writer thread on stop; tests).</summary>
    internal void Close()
    {
        try
        {
            _out?.Dispose();
        }
        catch (IOException exception)
        {
            _failure ??= exception.Message;
        }

        _out = null;
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                _wake.WaitOne(500);
                Drain();
            }

            Drain();
        }
        catch (Exception exception)
        {
            // The writer thread's boundary: nothing it does may end the process; the report shows the failure.
            _failure ??= exception.Message;
        }
        finally
        {
            Close();
            lock (_gate)
            {
                _wake.Dispose();
            }
        }
    }

    private bool Write(in ProfileRow row)
    {
        try
        {
            if (_out == null)
            {
                Open();
            }

            string line = Format(in row);
            _out!.WriteLine(line);
            _bytes += Encoding.UTF8.GetByteCount(line) + _out.NewLine.Length;
            if (_bytes >= _capBytes)
            {
                Close();
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            _failure = exception.Message;
            Interlocked.Increment(ref _dropped);
            Close();
            return false;
        }
    }

    // A name already taken (an earlier session's file) is skipped: an existing file is never written over.
    private void Open()
    {
        Directory.CreateDirectory(_directory);
        string path;
        do
        {
            _part++;
            path = PartPath(_part);
        }
        while (File.Exists(path));

        _out = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        _path = path;
        _files.Enqueue(path);
        _out.WriteLine(Header);
        _bytes = Encoding.UTF8.GetByteCount(Header) + _out.NewLine.Length;
        while (_files.Count > _keptFiles)
        {
            Forget(_files.Dequeue());
        }
    }

    // An old part another program holds open (a spreadsheet) stays on disk; the CSV goes on.
    private static void Forget(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
        }
    }

    private string PartPath(int part) =>
        System.IO.Path.Combine(_directory, part == 1 ? $"profile-{_stamp}.csv" : $"profile-{_stamp}-{part}.csv");

    /// <summary>One row as its CSV line (invariant culture, no quoting: names are identifiers).</summary>
    internal string Format(in ProfileRow row)
    {
        StringBuilder line = _line.Clear();
        line.Append(row.Kind == ProfileRowKind.Frame ? "frame" : "second").Append(',')
            .Append(new DateTime(row.UtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
            .Append(',').Append(row.Frame.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(row.Frames.ToString(CultureInfo.InvariantCulture))
            .Append(',').Append(Ms(row.Ms))
            .Append(',').Append(Ms(row.MaxMs))
            .Append(',').Append(row.HeapDeltaBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
            .Append(',').Append(row.Calls.ToString(CultureInfo.InvariantCulture));
        Append(line, row.Top1);
        Append(line, row.Top2);
        Append(line, row.Top3);
        Append(line, row.SlowestCall);
        return line.ToString();
    }

    private static void Append(StringBuilder line, NamedSlot slot)
    {
        line.Append(',').Append(Clean(slot.Name)).Append(',');
        if (slot.Name != null)
        {
            line.Append(Ms(slot.Ms));
        }
    }

    // A method name a client sent is not trusted: a comma or line break would break the row.
    private static string Clean(string? name)
    {
        if (name == null)
        {
            return string.Empty;
        }

        return name.IndexOfAny(Unsafe) < 0 ? name : "?";
    }

    private static readonly char[] Unsafe = { ',', '"', '\r', '\n' };

    private static string Ms(double ms) => Math.Round(ms, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
