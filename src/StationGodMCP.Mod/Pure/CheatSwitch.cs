#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Pure;

/// <summary>
/// The cheat switch (LU, 2026-10-19; the one exception to "no permission systems in StationGod"): outside a creative
/// world every cheat (a what the catalogue marks cheat, at the call's options) is refused as cheats_off, unless someone
/// at the game's console turned cheats on for some minutes (stationgod cheats on &lt;minutes&gt;). It turns itself off
/// when they run out, is off at every start and every new world, and is never saved. While it lets a cheat through,
/// the reply carries the cheat note (CheatNote) and an acting call is logged for server info. A creative world allows
/// every cheat as before, with no note and no log. The game tells no player anything.
/// </summary>
internal sealed class CheatSwitch : ICheatGate
{
    internal const string OffCode = "cheats_off";

    internal const string ConsoleOnlyCode = "console_only";

    /// <summary>The console command's key (CommandLine.CommandsMap).</summary>
    internal const string CommandKey = "stationgod";

    internal const string OnCommand = "stationgod cheats on <minutes>";

    /// <summary>A day: long enough for any session, short enough that a forgotten switch still runs out.</summary>
    internal const int MaximumMinutes = 1440;

    /// <summary>The cheats server info lists, newest last.</summary>
    internal const int LogSize = 20;

    private const double MsPerMinute = 60000.0;

    private readonly IMonotonicClock _clock;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _creative;
    private readonly List<CheatRunView> _runs = new List<CheatRunView>();
    private double? _untilMs;

    internal CheatSwitch(IMonotonicClock clock, Func<DateTime> utcNow, Func<bool> creative)
    {
        _clock = clock;
        _utcNow = utcNow;
        _creative = creative;
    }

    internal bool IsOn => _untilMs is { } until && _clock.NowMs < until;

    /// <summary>The minutes left while on, to a tenth; null while off.</summary>
    internal double? MinutesLeft => IsOn ? Math.Round((_untilMs!.Value - _clock.NowMs) / MsPerMinute, 1) : null;

    internal IReadOnlyList<CheatRunView> Runs => _runs;

    internal void TurnOn(int minutes) => _untilMs = _clock.NowMs + minutes * MsPerMinute;

    internal void TurnOff() => _untilMs = null;

    /// <summary>A world was left: off, and its log forgotten.</summary>
    internal void Clear()
    {
        _untilMs = null;
        _runs.Clear();
    }

    /// <summary>The refusal of a cheat while the switch is off in a world that is not creative.</summary>
    internal static string Refusal(string label) =>
        $"{label} is a cheat, and cheats are off in this world (it is not creative). Nothing was done. Ask the user: " +
        $"only someone at the game's console can turn them on for a while, with '{OnCommand}'.";

    /// <summary>The refusal of the switch's own command when it comes through StationGod (server command, a script).</summary>
    internal static string ConsoleOnlyRefusal() =>
        $"'{CommandKey}' turns StationGod's cheats on or off, and only someone typing at the game's console can run " +
        "it. Ask the user.";

    public VerbError? Admit(WhatSpec what, JObject? parameters)
    {
        if (what.Write?.IsCheat(parameters) != true || _creative())
        {
            return null;
        }

        if (MinutesLeft is not { } left)
        {
            return new VerbError(OffCode, Refusal(what.Label));
        }

        CheatNote.Set(new CheatNoteView(left, what.Write.Acts(parameters)));
        return null;
    }

    public void Ran(WhatSpec what, JObject? parameters)
    {
        if (what.Write?.IsCheat(parameters) != true || !what.Write.Acts(parameters) || _creative() ||
            MinutesLeft is not { } left)
        {
            return;
        }

        if (_runs.Count == LogSize)
        {
            _runs.RemoveAt(0);
        }

        _runs.Add(new CheatRunView(what.Label, _utcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            left));
    }

    /// <summary>The switch as server info shows it.</summary>
    internal CheatsView View() => new CheatsView(IsOn, MinutesLeft, _creative(), new List<CheatRunView>(_runs));

    /// <summary>
    /// What the console command does with its arguments (after the key): the line it prints, or the problem with them.
    /// No arguments, or "cheats", prints the state; "cheats on &lt;minutes&gt;" and "cheats off" switch it.
    /// </summary>
    internal string Command(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || (arguments.Count == 1 && Is(arguments[0], "cheats")))
        {
            return State();
        }

        if (!Is(arguments[0], "cheats"))
        {
            return Usage;
        }

        if (arguments.Count == 2 && Is(arguments[1], "off"))
        {
            TurnOff();
            return State();
        }

        if (arguments.Count == 3 && Is(arguments[1], "on"))
        {
            if (!int.TryParse(arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
                minutes < 1 || minutes > MaximumMinutes)
            {
                return $"minutes must be a whole number from 1 to {MaximumMinutes}. {Usage}";
            }

            TurnOn(minutes);
            return State();
        }

        return Usage;
    }

    internal const string Usage =
        "Usage: stationgod cheats on <minutes> | stationgod cheats off | stationgod cheats (state).";

    private string State()
    {
        string creative = _creative() ? " This world is creative, so cheats are allowed anyway." : string.Empty;
        return MinutesLeft is { } left
            ? $"StationGod cheats are ON for {left.ToString("0.0", CultureInfo.InvariantCulture)} more minutes " +
              $"({_runs.Count} cheat calls logged in this world).{creative}"
            : $"StationGod cheats are OFF.{creative}";
    }

    private static bool Is(string argument, string word) => string.Equals(argument, word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The verb dispatcher's cheat gate: Admit before a what's handler runs (null lets it run, otherwise its refusal), Ran
/// after an admitted call answered or started its work.
/// </summary>
internal interface ICheatGate
{
    VerbError? Admit(WhatSpec what, JObject? parameters);

    void Ran(WhatSpec what, JObject? parameters);
}

/// <summary>
/// The cheat note of the call running now, added to its reply as cheat (VerbNotes), like gas_hold: set when the switch
/// lets a cheat through. Calls run one at a time on the main thread.
/// </summary>
internal static class CheatNote
{
    [ThreadStatic]
    private static CheatNoteView? _note;

    internal static void Begin() => _note = null;

    internal static void Set(CheatNoteView note) => _note = note;

    /// <summary>The note since Begin, or null; then forgotten.</summary>
    internal static CheatNoteView? Take()
    {
        CheatNoteView? note = _note;
        _note = null;
        return note;
    }
}

/// <summary>A reply's cheat: it ran (or would run) as a cheat because the console's switch is on, and for how long.</summary>
internal sealed class CheatNoteView
{
    internal CheatNoteView(double minutesLeft, bool acts)
    {
        MinutesLeft = minutesLeft;
        string left = minutesLeft.ToString("0.0", CultureInfo.InvariantCulture);
        Note = acts
            ? $"Ran as a cheat: cheats are on at the game's console for {left} more minutes."
            : $"A cheat: allowed because cheats are on at the game's console for {left} more minutes.";
    }

    public bool CheatsOn => true;

    public double MinutesLeft { get; }

    public string Note { get; }
}

/// <summary>One cheat that ran under the switch: the what (build item), when (UTC), and the minutes then left.</summary>
internal sealed class CheatRunView
{
    internal CheatRunView(string what, string atUtc, double minutesLeft)
    {
        What = what;
        AtUtc = atUtc;
        MinutesLeft = minutesLeft;
    }

    public string What { get; }

    public string AtUtc { get; }

    public double MinutesLeft { get; }
}

/// <summary>server info's cheats: whether the switch is on, the minutes left, whether the world is creative, its log.</summary>
internal sealed class CheatsView
{
    internal CheatsView(bool on, double? minutesLeft, bool creative, List<CheatRunView> runs)
    {
        On = on;
        MinutesLeft = minutesLeft;
        Creative = creative;
        Runs = runs;
    }

    public bool On { get; }

    public double? MinutesLeft { get; }

    public bool Creative { get; }

    public string Command => CheatSwitch.OnCommand;

    public List<CheatRunView> Runs { get; }
}
