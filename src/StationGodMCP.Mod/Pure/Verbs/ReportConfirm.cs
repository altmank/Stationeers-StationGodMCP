#nullable enable

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// Report and confirm (PLAN 2.0, direct write rule): a what that takes confirm acts only with confirm: true and
/// otherwise reports what it would do; a what without confirm acts as sent. The hub, which applies the route (typed
/// ids act; refs, networks and spreads report), sends confirm: true for every call it lets act. rows is how many rows
/// each list of a report shows (0 for none; left out, all); everything is still counted and checked.
/// </summary>
internal sealed class ReportConfirm
{
    internal ReportConfirm(bool takesConfirm, bool confirm, int? rows)
    {
        TakesConfirm = takesConfirm;
        Confirm = confirm;
        Rows = rows;
    }

    public bool TakesConfirm { get; }

    public bool Confirm { get; }

    public int? Rows { get; }

    internal static ReportConfirm Acting { get; } = new ReportConfirm(false, false, null);

    internal static ReportConfirm Bind(OptionReader options)
    {
        long? rows = options.Integer("rows");
        return new ReportConfirm(options.What.Takes("confirm"), options.Flag("confirm") ?? false,
            rows is { } shown ? (int)System.Math.Min(shown, int.MaxValue) : null);
    }

    /// <summary>True when the call acts; false when it only reports.</summary>
    internal bool Acts => !TakesConfirm || Confirm;
}
