#nullable enable

namespace StationGodMCP.Api.Views;

/// <summary>paste_blueprint: a paste BlueprintMod accepted and is now running as its coroutine.</summary>
internal sealed class BlueprintPasteStartedView
{
    internal BlueprintPasteStartedView(BlueprintFileView file, double[] anchor, int rotation, float copyYAngle,
        double expectedDurationS)
    {
        File = file.Path;
        Entries = file.Entries;
        Anchor = anchor;
        Rotation = rotation;
        CopyYAngle = copyYAngle;
        ExpectedDurationS = expectedDurationS;
    }

    public bool Started => true;

    /// <summary>The blueprint file's full path.</summary>
    public string File { get; }

    public int Entries { get; }

    /// <summary>[x, y, z] in metres, as given.</summary>
    public double[] Anchor { get; }

    /// <summary>Degrees added to the angle the blueprint was copied at: 0, 90, 180 or 270.</summary>
    public int Rotation { get; }

    /// <summary>BlueprintData.CopyYAngle, the paste's base angle, so the pieces land on the grid.</summary>
    public float CopyYAngle { get; }

    public double ExpectedDurationS { get; }
}

/// <summary>A blueprint file and how many entries it holds.</summary>
internal sealed class BlueprintFileView
{
    internal BlueprintFileView(string path, int entries)
    {
        Path = path;
        Entries = entries;
    }

    internal string Path { get; }

    internal int Entries { get; }
}

/// <summary>
/// BlueprintMod's StaggeredPasteOperation counts, read from the operation object itself so they outlive the paste.
/// </summary>
internal sealed class BlueprintPasteCountsView
{
    internal BlueprintPasteCountsView(bool complete, bool cancelled, int created, int failed, int skipped, int pasted,
        string? fingerprint)
    {
        Complete = complete;
        Cancelled = cancelled;
        Created = created;
        Failed = failed;
        Skipped = skipped;
        Pasted = pasted;
        Fingerprint = fingerprint;
    }

    internal bool Complete { get; }

    internal bool Cancelled { get; }

    internal int Created { get; }

    internal int Failed { get; }

    internal int Skipped { get; }

    internal int Pasted { get; }

    internal string? Fingerprint { get; }
}

/// <summary>
/// paste_blueprint status: the last paste this tool started. Counts are null when none was started since the mod
/// loaded, or when the paste finished inside the call that started it (no operation was left to read).
/// </summary>
internal sealed class BlueprintPasteStatusView
{
    private BlueprintPasteStatusView(bool known, bool active, BlueprintPasteCountsView? counts, BlueprintFileView? file,
        bool otherActive)
    {
        Known = known;
        Active = active;
        Complete = counts?.Complete;
        Cancelled = counts?.Cancelled;
        Created = counts?.Created;
        Failed = counts?.Failed;
        Skipped = counts?.Skipped;
        Pasted = counts?.Pasted;
        Fingerprint = counts?.Fingerprint;
        File = file?.Path;
        Entries = file?.Entries;
        OtherActive = otherActive;
    }

    /// <summary>Whether this tool has started a paste since the mod loaded.</summary>
    public bool Known { get; }

    /// <summary>That paste is BlueprintMod's ActivePaste: still placing pieces.</summary>
    public bool Active { get; }

    public bool? Complete { get; }

    public bool? Cancelled { get; }

    public int? Created { get; }

    public int? Failed { get; }

    public int? Skipped { get; }

    /// <summary>Things placed and kept for undo (StaggeredPasteOperation.PastedThings).</summary>
    public int? Pasted { get; }

    public string? Fingerprint { get; }

    public string? File { get; }

    public int? Entries { get; }

    /// <summary>A paste this tool did not start (a player's, the D.B.P.U.'s) is running.</summary>
    public bool OtherActive { get; }

    internal static BlueprintPasteStatusView Unknown(bool otherActive) =>
        new BlueprintPasteStatusView(false, false, null, null, otherActive);

    internal static BlueprintPasteStatusView Of(BlueprintFileView file, bool active, BlueprintPasteCountsView? counts,
        bool otherActive) =>
        new BlueprintPasteStatusView(true, active, counts, file, otherActive);
}

/// <summary>paste_blueprint undo: what BlueprintMod's bpundo answered.</summary>
internal sealed class BlueprintUndoView
{
    internal BlueprintUndoView(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
