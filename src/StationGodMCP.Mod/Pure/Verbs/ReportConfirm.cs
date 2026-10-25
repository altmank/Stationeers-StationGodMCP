#nullable enable

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// Report and confirm (PLAN 2.0, direct write rule): a what that takes confirm acts only with confirm: true and
/// otherwise reports what it would do; a what without confirm acts as sent. An explicit confirm: false is the caller
/// asking for the report: it reports whatever the call names, typed ids included, whatever a what's own rule would say.
/// The hub sends confirm: true on the act path and confirm: false on the report path, and leaves confirm out for a what
/// that has no confirm option. rows is how many rows each list of a report shows (0 for none; left out, all);
/// everything is still counted and checked.
/// </summary>
internal sealed class ReportConfirm
{
    private ReportConfirm(bool takesConfirm, bool confirm, bool asked, int? rows)
    {
        TakesConfirm = takesConfirm;
        Confirm = confirm;
        Asked = asked;
        Rows = rows;
    }

    /// <summary>
    /// A decision the what makes itself rather than reading the options (an undo's replay of the call it takes back):
    /// the call gave no confirm either way, so it never asked for the report.
    /// </summary>
    internal ReportConfirm(bool takesConfirm, bool confirm, int? rows)
        : this(takesConfirm, confirm, asked: false, rows)
    {
    }

    public bool TakesConfirm { get; }

    public bool Confirm { get; }

    /// <summary>The call itself asked for the report: it gave confirm: false.</summary>
    public bool Asked { get; }

    public int? Rows { get; }

    internal static ReportConfirm Acting { get; } = new ReportConfirm(false, false, null);

    internal static ReportConfirm Bind(OptionReader options)
    {
        long? rows = options.Integer("rows");
        bool? confirm = options.Flag("confirm");
        return new ReportConfirm(options.What.Takes("confirm"), confirm == true, confirm == false,
            rows is { } shown ? (int)System.Math.Min(shown, int.MaxValue) : null);
    }

    /// <summary>True when the call acts; false when it only reports (left out or an explicit false).</summary>
    internal bool Acts => !TakesConfirm || Confirm;

    /// <summary>
    /// The same decision as a report the call asked for, whatever its confirm said: a check_only call never acts.
    /// </summary>
    internal ReportConfirm ReportOnly() => new ReportConfirm(takesConfirm: true, confirm: false, asked: true, Rows);
}
