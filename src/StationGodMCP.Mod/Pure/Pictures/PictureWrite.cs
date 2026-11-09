#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// Writing show picture's file so a dropped call never leaves one: a pool thread makes the bytes (produce: the PNG's
/// encode, which also frees what it read from) and writes them to a temporary file beside the picture; only Commit, on
/// the main thread when the call answers, moves it to the picture's name. Dispose (the call dropped: cancelled, timed
/// out, another world) cancels: produce is told, a temporary file already written is deleted, and one still being
/// written is deleted by the pool thread when it ends. produce is always called exactly once, so it can free its
/// inputs. Commit refuses (FileExists) when the name is taken and overwrite is false.
/// </summary>
internal sealed class PictureWrite : IDisposable
{
    private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
    private readonly string _path;
    private readonly bool _overwrite;
    private readonly Task<long> _task;
    private int _ended;

    internal PictureWrite(string path, bool overwrite, Func<CancellationToken, byte[]?> produce)
    {
        _path = path;
        _overwrite = overwrite;
        Temporary = Path.Combine(Path.GetDirectoryName(path)!,
            "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        CancellationToken token = _cancel.Token;
        string temporary = Temporary;
        _task = Task.Run(() => Write(produce, token, temporary));
    }

    /// <summary>Thrown by Commit when the picture's name is taken and overwrite is false.</summary>
    internal sealed class FileExistsException : IOException
    {
        internal FileExistsException(string path)
            : base($"{path} already exists.")
        {
        }
    }

    internal string Temporary { get; }

    /// <summary>Whether the pool thread has ended (written, failed or cancelled).</summary>
    internal bool IsDone => _task.IsCompleted;

    /// <summary>Waits for the pool thread (tests).</summary>
    internal bool Wait(TimeSpan limit)
    {
        try
        {
            return _task.Wait(limit);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    /// <summary>
    /// Main thread, once IsDone: moves the temporary file to the picture's name and returns its bytes. The pool
    /// thread's failure is rethrown as it was; a taken name without overwrite throws FileExistsException.
    /// </summary>
    internal long Commit()
    {
        if (!_task.IsCompleted)
        {
            throw new InvalidOperationException("The picture is still being written.");
        }

        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            throw new InvalidOperationException("The picture was committed or dropped already.");
        }

        try
        {
            if (_task.IsFaulted)
            {
                throw _task.Exception!.InnerException ?? _task.Exception;
            }

            if (_task.IsCanceled || _task.Result < 0)
            {
                throw new OperationCanceledException("The picture was dropped before it was written.");
            }

            if (File.Exists(_path))
            {
                if (!_overwrite)
                {
                    throw new FileExistsException(_path);
                }

                File.Delete(_path);
            }

            File.Move(Temporary, _path);
            return _task.Result;
        }
        finally
        {
            TryDelete(Temporary);
        }
    }

    /// <summary>The call was dropped: nothing is written, or what was is deleted.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();
        string temporary = Temporary;
        // Whenever the pool thread ends (now or later), the temporary file goes.
        _task.ContinueWith(_ => TryDelete(temporary), TaskScheduler.Default);
    }

    private static long Write(Func<CancellationToken, byte[]?> produce, CancellationToken token, string temporary)
    {
        byte[]? bytes = produce(token);
        if (bytes == null || token.IsCancellationRequested)
        {
            return -1;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes, 0, bytes.Length);
        }

        if (token.IsCancellationRequested)
        {
            TryDelete(temporary);
            return -1;
        }

        return bytes.Length;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
