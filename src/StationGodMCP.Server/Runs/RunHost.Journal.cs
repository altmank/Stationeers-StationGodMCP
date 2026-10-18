namespace StationGodMCP.Server;

/// <summary>
/// The journals (hub.md, The journal): adopting what earlier hubs left (their runs as script-&lt;pid&gt;.N, their
/// held restores' outcomes read with the old token), the session's first reply that reports them, retiring journals
/// whose runs are all ended and reported, and the memory bound on write logs kept for undo.
/// </summary>
public sealed partial class RunHost
{
    private static readonly TimeSpan RestoresBound = TimeSpan.FromSeconds(10);

    private readonly List<AdoptedJournal> _adopted = [];
    private Task _adoption = Task.CompletedTask;
    private bool _firstReplyDone;
    private bool _firstReplyGone;

    /// <summary>
    /// Adopts, in the background, every journal in the folder (but this hub's own) whose lock it can take, that is not
    /// retired and that served the same game; the first reply waits for it. The old tokens' restores are read for all
    /// journals at once, within one bound. Only a hub serving an MCP client adopts (SidecarOptions.AdoptsJournals).
    /// </summary>
    internal void StartAdoption(string directory, string? ownPath, Func<string, ITokenLink> links, string? target = null)
    {
        _adoption = Task.Run(async () =>
        {
            List<AdoptedJournal> taken = AdoptedJournal.TakeAll(directory, ownPath, target);
            lock (_gate)
            {
                foreach (AdoptedJournal journal in taken)
                {
                    _adopted.Add(journal);
                    foreach (RunRecord run in journal.Runs)
                    {
                        Notes notes = new(run, null) { Journal = journal };
                        _runs[run.Id] = notes;
                        _order.Add(notes);
                    }
                }
            }

            using CancellationTokenSource bound = new(RestoresBound);
            List<Task> reading = [];
            foreach (AdoptedJournal journal in taken)
            {
                reading.Add(ReadRestoresAsync(journal, links, bound.Token));
            }

            await Task.WhenAll(reading).ConfigureAwait(false);
        });
    }

    private async Task ReadRestoresAsync(AdoptedJournal journal, Func<string, ITokenLink> links, CancellationToken bound)
    {
        bool read;
        try
        {
            read = await journal.ReadRestoresAsync(links, _settings.AdoptionReconnect, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            read = false;
        }

        if (!read && journal.HasHolds)
        {
            foreach (RunRecord run in journal.Runs)
            {
                run.Restores ??= "held restores: unknown (the game could not be reached to read them)";
            }
        }
    }

    private async Task AdoptedAsync()
    {
        try
        {
            await _adoption.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Adoption is best effort: a journal it could not read is left for the next hub.
        }
    }

    // One line per adopted run of this world that left something, on the session's first reply. The adopted journals retire once that
    // reply has gone (the next reply is built, or the hub ends) and only when their restores' outcomes are known.
    private async Task<List<string>> FirstReplyLinesAsync()
    {
        bool retire = false;
        lock (_gate)
        {
            if (_firstReplyDone)
            {
                retire = !_firstReplyGone;
                _firstReplyGone = true;
            }
        }

        if (retire)
        {
            RetireAdopted();
        }

        lock (_gate)
        {
            if (_firstReplyDone)
            {
                return [];
            }

            _firstReplyDone = true;
        }

        await AdoptedAsync().ConfigureAwait(false);
        List<string> lines = [];
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                RunRecord record = notes.Record;
                if (!record.Adopted || notes.Reported)
                {
                    continue;
                }

                notes.Reported = true;
                if (!OfThisWorld(record) || (record.Left.Count == 0 && record.Restores == null))
                {
                    continue;
                }

                string left = record.Left.Count > 0 ? "; left: " + string.Join(", ", record.Left) : "";
                string restores = record.Restores != null ? "; " + record.Restores : "";
                string torn = notes.Journal is { TornLines: > 0 } journal
                    ? $"; {journal.TornLines} torn journal lines skipped"
                    : "";
                lines.Add($"notice: {record.Id} ({record.FirstLine}) {Run.StateWord(record.State)} in an earlier session" +
                          $"{left}{restores}{torn}; undo it with undo {{id: \"{record.Id}\"}}; if you did not start it, " +
                          "leave it");
            }
        }

        return lines;
    }

    // A journal whose restores' outcomes are unknown (it held some, and they could not be read) stays for the next hub.
    private void RetireAdopted()
    {
        List<AdoptedJournal> journals;
        lock (_gate)
        {
            journals = [.. _adopted];
        }

        foreach (AdoptedJournal journal in journals)
        {
            if (journal.HasRestores || !journal.HasHolds)
            {
                journal.Retire();
            }
        }
    }

    // Past the bound, the oldest ended runs' write logs go; their undo then names what it can no longer revert.
    private void KeepUndoMemory()
    {
        lock (_gate)
        {
            int total = 0;
            foreach (Notes notes in _order)
            {
                total += Targets(notes.Record);
            }

            foreach (Notes notes in _order)
            {
                if (total <= _settings.UndoMemoryTargets)
                {
                    return;
                }

                RunRecord record = notes.Record;
                if (record.IsLive || record.Writes.Count == 0)
                {
                    continue;
                }

                lock (record.Sync)
                {
                    total -= Targets(record);
                    record.Forgotten = Summary(record);
                    record.Writes.Clear();
                }
            }
        }
    }

    private static int Targets(RunRecord record)
    {
        lock (record.Sync)
        {
            int count = 0;
            foreach (WriteEntry write in record.Writes)
            {
                count += write.Targets.Count;
            }

            return count;
        }
    }

    // "write logic x3 (1 On=1, 2 On=1, 3 On=0)": what a run's log held. Under the record's lock.
    private static string Summary(RunRecord record)
    {
        List<string> parts = [];
        foreach (WriteEntry write in record.Writes)
        {
            List<string> items = [];
            foreach (WrittenTarget target in write.Targets)
            {
                if (items.Count < 8)
                {
                    items.Add(Run.Item(target));
                }
            }

            parts.Add($"{write.Label} x{write.Targets.Count} ({string.Join(", ", items)})");
        }

        return string.Join("; ", parts);
    }

    // This hub's journal retires when all its runs ended and were reported; retired journals may be deleted.
    private void CloseJournals()
    {
        bool reported = true;
        List<AdoptedJournal> adopted;
        lock (_gate)
        {
            foreach (Notes notes in _order)
            {
                if (!notes.Record.Adopted && (notes.Record.IsLive || !notes.Delivered))
                {
                    reported = false;
                }
            }

            adopted = [.. _adopted];
        }

        bool firstReplyBuilt;
        lock (_gate)
        {
            firstReplyBuilt = _firstReplyDone && !_firstReplyGone;
            _firstReplyGone = true;
        }

        if (firstReplyBuilt)
        {
            RetireAdopted();
        }

        if (_settings.Journal is Journal own)
        {
            if (reported)
            {
                own.Retire();
            }

            own.Dispose();
            DeleteRetired(own.Retired, own.FilePath);
        }

        foreach (AdoptedJournal journal in adopted)
        {
            journal.Dispose();
            DeleteRetired(journal.Retired, journal.FilePath);
        }
    }

    private static void DeleteRetired(bool retired, string path)
    {
        if (!retired)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A retired journal left behind is skipped by the next hub.
        }
    }
}
