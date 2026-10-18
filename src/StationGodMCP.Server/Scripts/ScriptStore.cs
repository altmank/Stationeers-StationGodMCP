using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>Which folder of the store a script lives in (SPEC-NOTES 5b, Scopes).</summary>
internal enum ScriptScope
{
    Shared,
    World
}

/// <summary>
/// A saved script as the store holds it: its place, its current version, its description and the main(...) parameters
/// the save-time parse read, and the whole file (header included) a run gets as its source.
/// </summary>
internal sealed record SavedScript(string Name, ScriptScope Scope, string? World, int Version, string Description,
    IReadOnlyList<ScriptParameter> Parameters, string Path, string Source)
{
    /// <summary>The scope as the catalogue and the header name it: shared or world.</summary>
    internal string ScopeWord => Scope == ScriptScope.Shared ? "shared" : "world";

    /// <summary>How the replies name its place: "shared", or "world <id>".</summary>
    internal string Place => Scope == ScriptScope.Shared ? "shared" : "world " + World;
}

/// <summary>Where a script was last run: the run that ran it and when it started, from the journal's run line.</summary>
internal sealed record ScriptUse(string RunId, DateTimeOffset At);

/// <summary>What an operation on the store gave: its value, or the verb error code and message that refuses it.</summary>
internal abstract record ScriptOutcome<T>
{
    private ScriptOutcome()
    {
    }

    internal sealed record Done(T Value) : ScriptOutcome<T>;

    internal sealed record Refused(string Code, string Message, JsonObject? Data = null) : ScriptOutcome<T>;
}

/// <summary>The codes the store refuses with (catalogue/verbs/_errors.json).</summary>
internal static class ScriptFailure
{
    internal const string Name = "script_name";
    internal const string Invalid = "script_invalid";
    internal const string Ids = "script_ids";
    internal const string Exists = "script_exists";
    internal const string NotFound = "script_not_found";
    internal const string OtherWorld = "script_world";
    internal const string NoWorld = "no_world";
}

/// <summary>
/// The saved scripts under --scripts-dir (SPEC-NOTES 5b, T-B-4): shared/ and world/&lt;world id&gt;/, one file per
/// script, &lt;name&gt;.py the current version and &lt;name&gt;/v&lt;N&gt;.py every version ever saved. The file's
/// header carries the description, the scope, the version, the save time and the parameters of main(...); the code
/// below it is the source a run gets. Saving again writes the next version and keeps the old ones.
///
/// A name is a file name and (for B-7's import hook) a Python module name: one word of letters, digits and
/// underscores, no keyword, no path separator. The same name may not be saved twice in the scopes a run can see
/// (shared and the connected world), case-insensitively where the platform is; another world's scripts are invisible
/// here. The header is the store's format: a file without one is not a script of this store.
/// </summary>
internal sealed class ScriptStore
{
    private const int LongestName = 64;
    private const string Pys = "*.py";

    private readonly string _root;
    private readonly ScriptCode _code;
    private readonly string? _journalDirectory;
    private readonly IRunJournal _journal;

    internal ScriptStore(string root, ScriptCode code, string? journalDirectory = null, IRunJournal? journal = null)
    {
        _root = Path.GetFullPath(root);
        _code = code;
        _journalDirectory = journalDirectory;
        _journal = journal ?? NoRunJournal.Instance;
    }

    /// <summary>Where the scripts live.</summary>
    internal string Root => _root;

    /// <summary>
    /// The store a hub's command line names: --scripts-dir for the scripts, the journal folder (and the live journal,
    /// when the face has one) for last used, the configured Python and the sg folder for the save-time parse.
    /// </summary>
    internal static ScriptStore For(SidecarOptions options, string sgRoot, IRunJournal? journal = null) => new(
        options.ScriptsDirectory, new ScriptCode(options.Python, sgRoot), options.JournalDirectory, journal);

    // ---- reading ----------------------------------------------------------------------------------------------------

    /// <summary>Every script a run in this world can see (shared and world/&lt;world&gt;), by name; text filters the
    /// substring of a name or a description, any case.</summary>
    internal List<SavedScript> Find(string? text, string? world)
    {
        List<SavedScript> found = [];
        foreach ((string folder, ScriptScope scope) in Visible(world))
        {
            foreach (SavedScript script in ReadFolder(folder, scope, scope == ScriptScope.World ? world : null))
            {
                if (text is { Length: > 0 } filter &&
                    !script.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !script.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(script);
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return found;
    }

    /// <summary>The script of that name a run in this world would open: shared, else its world's. Refused when the
    /// name is not one, is saved in both scopes at once, belongs to another world, or has no header.</summary>
    internal ScriptOutcome<SavedScript> Open(string name, string? world)
    {
        if (NameProblem(name) is { } problem)
        {
            return Refused(ScriptFailure.Name, problem);
        }

        SavedScript? shared = Lookup(SharedFolder, name, ScriptScope.Shared, null);
        SavedScript? local = WorldFolder(world) is { } worldFolder
            ? Lookup(worldFolder, name, ScriptScope.World, world)
            : null;
        if (shared != null && local != null)
        {
            return Refused(ScriptFailure.Exists,
                $"'{name}' is saved in both shared and world {world}: remove one (remove script {{name: \"{name}\", " +
                "scope: \"shared\" or \"world\"}), then save the other again");
        }

        if (shared != null)
        {
            return new ScriptOutcome<SavedScript>.Done(shared);
        }

        if (local != null)
        {
            return new ScriptOutcome<SavedScript>.Done(local);
        }

        if (WorldFolder(world) is { } here && FileOf(here, name) is { } headerless)
        {
            return Headerless(headerless);
        }

        if (FileOf(SharedFolder, name) is { } sharedFile)
        {
            return Headerless(sharedFile);
        }

        return NotFound(name, world, "No saved script is named '" + name + "'");
    }

    /// <summary>When a script was last run, from the journal's run lines (T-B-4); null when no record names it.</summary>
    internal ScriptUse? LastUsed(string name)
    {
        long newest = -1;
        string? run = null;
        foreach (string line in JournalLines())
        {
            if (!line.Contains(name, StringComparison.Ordinal))
            {
                continue;
            }

            JsonObject? entry = null;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // A torn journal line says nothing.
            }

            if (entry == null || JsonNodes.Text(entry["t"]) != "run" || JsonNodes.Text(entry["script"]) != name ||
                JsonNodes.Number(entry["at"]) is not { } at || (long)at <= newest)
            {
                continue;
            }

            newest = (long)at;
            run = JsonNodes.Text(entry["run"]);
        }

        return newest < 0 ? null : new ScriptUse(run ?? "?", DateTimeOffset.FromUnixTimeMilliseconds(newest));
    }

    // ---- writing ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Save the script: parse the code (the refusal is the parser's reason), refuse a shared script's id literals,
    /// then write &lt;name&gt;/v&lt;N&gt;.py and &lt;name&gt;.py under the scope's folder. The version is one past the
    /// current one (the header's, or the highest history file's, whichever is higher).
    /// </summary>
    internal ScriptOutcome<SavedScript> Save(string name, string code, string description, ScriptScope scope,
        string? world)
    {
        if (NameProblem(name) is { } problem)
        {
            return Refused(ScriptFailure.Name, problem);
        }

        string said = description.Trim();
        if (said.Length == 0)
        {
            return Refused(ScriptFailure.Invalid, "the description is empty: one line saying what the script does");
        }

        if (said.Contains('\n') || said.Contains('\r'))
        {
            return Refused(ScriptFailure.Invalid, "the description must be one line");
        }

        if (code.Trim().Length == 0)
        {
            return Refused(ScriptFailure.Invalid, "the code is empty");
        }

        string? folder = scope == ScriptScope.Shared ? SharedFolder : WorldFolder(world);
        if (scope == ScriptScope.World && WorldProblem(world) is { } worldProblem)
        {
            return Refused(ScriptFailure.NoWorld, worldProblem);
        }

        if (folder == null)
        {
            return Refused(ScriptFailure.NoWorld,
                "a world script is saved for the world the hub is connected to: start the game in a world, or save it shared");
        }

        ScriptSignature signature = _code.Parse(code);
        if (!signature.Ok)
        {
            return Refused(ScriptFailure.Invalid, "the script does not parse: " + signature.Error);
        }

        if (scope == ScriptScope.Shared && signature.Ids.Count > 0)
        {
            StringBuilder ids = new();
            foreach (JsonNode? id in signature.Ids)
            {
                ids.Append(ids.Length > 0 ? ", " : string.Empty).Append(IdText(id));
            }

            return Refused(ScriptFailure.Ids,
                "a shared script may not hard-code ids (it must run in any world): the code carries " + ids);
        }

        string? other = scope == ScriptScope.Shared ? WorldFolder(world) : SharedFolder;
        if (other != null && FileOf(other, name) is { } clash)
        {
            return Refused(ScriptFailure.Exists,
                $"'{name}' is already saved in the other scope ({Path.Combine(other, Path.GetFileName(clash))}): " +
                $"remove it (remove script {{name: \"{name}\", scope: \"{(scope == ScriptScope.Shared ? "world" : "shared")}\"}}), " +
                "then save it here");
        }

        if (scope == ScriptScope.Shared && InAnyWorld(name) is { } elsewhere)
        {
            return Refused(ScriptFailure.Exists,
                $"'{name}' is saved as a world script of another world ({elsewhere}), where a shared '{name}' would " +
                "clash with it: save it shared under another name, or remove that one in its world first");
        }

        int version = NextVersion(folder, name);
        string path = Path.Combine(folder, name + ".py");
        string history = Path.Combine(folder, name, "v" + version.ToString(CultureInfo.InvariantCulture) + ".py");
        ScriptHeaderFields header = new(name, scope, scope == ScriptScope.World ? world : null, version, said,
            DateTimeOffset.UtcNow, signature.Parameters);
        string text = ScriptHeader.Text(header, code);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            File.WriteAllText(history, text, new UTF8Encoding(false));
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(ScriptFailure.Invalid, $"the script could not be written ({exception.Message})");
        }

        return new ScriptOutcome<SavedScript>.Done(new SavedScript(name, scope, header.World, version, said,
            signature.Parameters, path, text));
    }

    /// <summary>
    /// Remove the script and its history; an unknown name is refused with the nearest names. scope names the copy to
    /// remove (shared, or this world's), so a name saved in both can lose one; left out, the one a run would open.
    /// </summary>
    internal ScriptOutcome<SavedScript> Remove(string name, string? world, ScriptScope? scope = null)
    {
        ScriptOutcome<SavedScript> opened = scope is { } named ? OpenIn(name, world, named) : Open(name, world);
        if (opened is not ScriptOutcome<SavedScript>.Done done)
        {
            return opened;
        }

        SavedScript script = done.Value;
        try
        {
            string folder = Path.GetDirectoryName(script.Path)!;
            string history = Path.Combine(folder, script.Name);
            if (Directory.Exists(history))
            {
                Directory.Delete(history, recursive: true);
            }

            File.Delete(script.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(ScriptFailure.Invalid, $"the script could not be removed ({exception.Message})");
        }

        return new ScriptOutcome<SavedScript>.Done(script);
    }

    // The copy of that name in one scope: shared, or this world's.
    private ScriptOutcome<SavedScript> OpenIn(string name, string? world, ScriptScope scope)
    {
        if (NameProblem(name) is { } problem)
        {
            return Refused(ScriptFailure.Name, problem);
        }

        string? folder = scope == ScriptScope.Shared ? SharedFolder : WorldFolder(world);
        if (Lookup(folder, name, scope, scope == ScriptScope.World ? world : null) is { } script)
        {
            return new ScriptOutcome<SavedScript>.Done(script);
        }

        return folder != null && FileOf(folder, name) is { } headerless
            ? Headerless(headerless)
            : NotFound(name, world, $"No {(scope == ScriptScope.Shared ? "shared" : "world")} script is named '{name}'");
    }

    // A world script of that name in any world's folder: a shared one of the same name would clash in that world.
    private string? InAnyWorld(string name)
    {
        string worlds = Path.Combine(_root, "world");
        if (!Directory.Exists(worlds))
        {
            return null;
        }

        foreach (string folder in Directory.EnumerateDirectories(worlds))
        {
            if (FileOf(folder, name) is { } file)
            {
                return file;
            }
        }

        return null;
    }

    // ---- names and folders ------------------------------------------------------------------------------------------

    /// <summary>The names save, open and remove take: a Python module name, a usable file name on this platform.</summary>
    internal static string? NameProblem(string name)
    {
        if (name.Length == 0)
        {
            return "the name is empty: one word of letters, digits and underscores";
        }

        if (name.Length > LongestName)
        {
            return $"the name is longer than {LongestName} characters";
        }

        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return $"'{name}' must start with a letter or _ (it names a file and a Python module)";
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return $"'{name}' may hold only letters, digits and _ (no path separators, no dots, no spaces)";
            }
        }

        if (PythonKeywords.Contains(name))
        {
            return $"'{name}' is a Python keyword";
        }

        if (OperatingSystem.IsWindows() && ReservedNames.Contains(name.ToUpperInvariant()))
        {
            return $"'{name}' is a reserved file name on Windows";
        }

        return null;
    }

    /// <summary>Why this world id cannot name a folder, or None.</summary>
    private static string? WorldProblem(string? world)
    {
        if (world is not { Length: > 0 } || world is "." or "..")
        {
            return "the world has no id to name its folder";
        }

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            if (world.Contains(c))
            {
                return $"the world id '{world}' cannot name a folder";
            }
        }

        return world.Contains(Path.DirectorySeparatorChar) || world.Contains(Path.AltDirectorySeparatorChar)
            ? $"the world id '{world}' cannot name a folder"
            : null;
    }

    private string SharedFolder => Path.Combine(_root, "shared");

    private string? WorldFolder(string? world) => world == null || WorldProblem(world) != null
        ? null
        : Path.Combine(_root, "world", world);

    /// <summary>The scopes of this world, in the order a name is looked for.</summary>
    private List<(string Folder, ScriptScope Scope)> Visible(string? world)
    {
        List<(string, ScriptScope)> folders = [(SharedFolder, ScriptScope.Shared)];
        if (WorldFolder(world) is { } folder)
        {
            folders.Add((folder, ScriptScope.World));
        }

        return folders;
    }

    /// <summary>The file of that name in the folder, with the case the disk holds it in; null when there is none.</summary>
    private static string? FileOf(string folder, string name)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(folder, Pys))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(file), name, NameComparison))
            {
                return file;
            }
        }

        return null;
    }

    private SavedScript? Lookup(string? folder, string name, ScriptScope scope, string? world) =>
        folder == null ? null : Fresh(Read(FileOf(folder, name), scope, world));

    /// <summary>
    /// A script as its file stands now: a file edited on disk since its version was saved (it no longer matches its
    /// history copy) gets its header rebuilt from the source, its parameters parsed again and the next version
    /// written, so a new main(...) argument is taken at once. A source that does not parse keeps the header it has
    /// (its run then fails on the source itself).
    /// </summary>
    private SavedScript? Fresh(SavedScript? script)
    {
        if (script == null || !ScriptHeader.TryRead(script.Source, out ScriptHeaderFields header))
        {
            return script;
        }

        string folder = Path.GetDirectoryName(script.Path)!;
        string history = HistoryOf(folder, script.Name, script.Version);
        string? kept = null;
        try
        {
            kept = File.Exists(history) ? File.ReadAllText(history, new UTF8Encoding(false)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return script;
        }

        if (kept != null && kept == script.Source)
        {
            return script;
        }

        string body = ScriptHeader.Body(script.Source);
        ScriptSignature signature = _code.Parse(body);
        if (!signature.Ok)
        {
            return script;
        }

        int version = NextVersion(folder, script.Name);
        ScriptHeaderFields rebuilt = new(script.Name, script.Scope, header.World, version, header.Description,
            DateTimeOffset.UtcNow, signature.Parameters);
        string text = ScriptHeader.Text(rebuilt, body);
        try
        {
            string next = HistoryOf(folder, script.Name, version);
            Directory.CreateDirectory(Path.GetDirectoryName(next)!);
            File.WriteAllText(next, text, new UTF8Encoding(false));
            File.WriteAllText(script.Path, text, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return script;
        }

        return new SavedScript(script.Name, script.Scope, rebuilt.World, version, rebuilt.Description,
            signature.Parameters, script.Path, text);
    }

    private static string HistoryOf(string folder, string name, int version) =>
        Path.Combine(folder, name, "v" + version.ToString(CultureInfo.InvariantCulture) + ".py");

    private List<SavedScript> ReadFolder(string folder, ScriptScope scope, string? world)
    {
        List<SavedScript> scripts = [];
        if (!Directory.Exists(folder))
        {
            return scripts;
        }

        foreach (string file in Directory.EnumerateFiles(folder, Pys))
        {
            if (Fresh(Read(file, scope, world)) is { } script)
            {
                scripts.Add(script);
            }
        }

        return scripts;
    }

    private static SavedScript? Read(string? path, ScriptScope scope, string? world)
    {
        if (path == null)
        {
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }

        if (!ScriptHeader.TryRead(text, out ScriptHeaderFields header))
        {
            return null;
        }

        return new SavedScript(Path.GetFileNameWithoutExtension(path), scope,
            scope == ScriptScope.World ? world : null, header.Version, header.Description, header.Parameters, path, text);
    }

    /// <summary>One past the current version: the header's version or the highest history file's, whichever is more.</summary>
    private static int NextVersion(string folder, string name)
    {
        int version = 0;
        if (FileOf(folder, name) is { } current && Read(current, ScriptScope.Shared, null) is { } script)
        {
            version = script.Version;
        }

        string history = Path.Combine(folder, name);
        if (Directory.Exists(history))
        {
            foreach (string file in Directory.EnumerateFiles(history, "v*.py"))
            {
                string number = Path.GetFileNameWithoutExtension(file)[1..];
                if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int saved) && saved > version)
                {
                    version = saved;
                }
            }
        }

        return version + 1;
    }

    // ---- the journal ------------------------------------------------------------------------------------------------

    // The lines of the live journal (through its own handle: the file is held exclusively) and of the other journals
    // in the folder, best effort: one that cannot be read is not the store's to fail on.
    private List<string> JournalLines()
    {
        List<string> lines = [];
        Journal? live = _journal as Journal;
        if (live != null)
        {
            lines.AddRange(live.Lines());
        }

        if (_journalDirectory == null || !Directory.Exists(_journalDirectory))
        {
            return lines;
        }

        foreach (string file in Directory.EnumerateFiles(_journalDirectory, "*.jsonl"))
        {
            if (live != null && string.Equals(file, live.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                lines.AddRange(File.ReadAllLines(file, Encoding.UTF8));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another hub holds it: its runs are not this store's to read.
            }
        }

        return lines;
    }

    // ---- refusal text -----------------------------------------------------------------------------------------------

    private static ScriptOutcome<SavedScript> Headerless(string path) => Refused(ScriptFailure.Invalid,
        $"{path} has no stationgod script header (write script writes one): its description and parameters are unknown");

    private ScriptOutcome<SavedScript> NotFound(string name, string? world, string message)
    {
        List<string> names = [];
        foreach ((string folder, ScriptScope _) in Visible(world))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(folder, Pys))
            {
                if (IsScript(file))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }

        // A world script elsewhere is not invisible: the agent meant one of those (with no world loaded, any of them).
        if (Directory.Exists(Path.Combine(_root, "world")))
        {
            string? own = WorldFolder(world);
            foreach (string folder in Directory.EnumerateDirectories(Path.Combine(_root, "world")))
            {
                if (own != null && string.Equals(folder, own, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (FileOf(folder, name) is { } elsewhere && IsScript(elsewhere))
                {
                    return Refused(ScriptFailure.OtherWorld,
                        $"'{name}' is a world script of {Path.GetFileName(folder)}: a world script runs only in the world " +
                        "it was saved in");
                }
            }
        }

        List<string> nearest = Nearest.Of(name, names);
        if (nearest.Count == 0)
        {
            return Refused(ScriptFailure.NotFound, message);
        }

        JsonArray list = [];
        foreach (string near in nearest)
        {
            list.Add(JsonValue.Create(near));
        }

        return Refused(ScriptFailure.NotFound, message + "; nearest: " + string.Join(", ", nearest),
            new JsonObject { ["nearest"] = list });
    }

    private static ScriptOutcome<SavedScript> Refused(string code, string message) =>
        new ScriptOutcome<SavedScript>.Refused(code, message);

    private static ScriptOutcome<SavedScript> Refused(string code, string message, JsonObject data) =>
        new ScriptOutcome<SavedScript>.Refused(code, message, data);

    /// <summary>Whether the file carries the store's header (a hand-added file without one is not a script here).</summary>
    private static bool IsScript(string path)
    {
        try
        {
            return ScriptHeader.TryRead(File.ReadAllText(path, new UTF8Encoding(false)), out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>An id constant as the refusal names it: the digits, or the string's text in quotes.</summary>
    private static string IdText(JsonNode? id) => id is JsonValue value && value.GetValueKind() == JsonValueKind.String
        ? "'" + value.GetValue<string>() + "'"
        : id?.ToJsonString() ?? "?";

    /// <summary>A name compares case-insensitively where the file system does.</summary>
    private static StringComparison NameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Python 3.10's keywords: a script's name is a module name for B-7's import hook.</summary>
    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del",
        "elif", "else", "except", "finally", "for", "from", "global", "if", "import", "in", "is", "lambda",
        "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield"
    };

    /// <summary>Windows' reserved device names (any extension).</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
}

/// <summary>What a script's header carries, and the store's format for it.</summary>
internal sealed record ScriptHeaderFields(string Name, ScriptScope Scope, string? World, int Version, string Description,
    DateTimeOffset Saved, IReadOnlyList<ScriptParameter> Parameters);

/// <summary>
/// The store's header, the first lines of every script file: one "# key: value" line per field, closed by "# ---".
/// The code follows it. A file whose first line is not the store's header line is not a script of this store; unknown
/// keys are kept for later versions of the format.
/// </summary>
internal static class ScriptHeader
{
    internal const string FirstLine = "# stationgod script v1";
    internal const string EndLine = "# ---";

    internal const string NameKey = "name";
    internal const string ScopeKey = "scope";
    internal const string WorldKey = "world";
    internal const string VersionKey = "version";
    internal const string SavedKey = "saved";
    internal const string DescriptionKey = "description";
    internal const string ParametersKey = "parameters";

    /// <summary>The header, then the code.</summary>
    internal static string Text(ScriptHeaderFields header, string code)
    {
        StringBuilder text = new();
        text.Append(FirstLine).Append('\n');
        Line(text, NameKey, header.Name);
        Line(text, ScopeKey, header.Scope == ScriptScope.Shared ? "shared" : "world");
        if (header.World != null)
        {
            Line(text, WorldKey, header.World);
        }

        Line(text, VersionKey, header.Version.ToString(CultureInfo.InvariantCulture));
        Line(text, SavedKey, header.Saved.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Line(text, DescriptionKey, header.Description);
        Line(text, ParametersKey, Parameters(header.Parameters).ToJsonString());
        text.Append(EndLine).Append('\n');
        text.Append(code);
        return text.ToString();
    }

    /// <summary>The code below a file's header: everything after the "# ---" line; the whole text when it has none.</summary>
    internal static string Body(string text)
    {
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            string line = (end < 0 ? text[at..] : text[at..end]).TrimEnd('\r');
            if (line == EndLine)
            {
                return end < 0 ? string.Empty : text[(end + 1)..];
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        return text;
    }

    /// <summary>Read a file's header; false for text that is not one (the store's format, or a hand-edited file).</summary>
    internal static bool TryRead(string text, out ScriptHeaderFields header)
    {
        header = new ScriptHeaderFields(string.Empty, ScriptScope.Shared, null, 0, string.Empty,
            DateTimeOffset.MinValue, []);
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != FirstLine)
        {
            return false;
        }

        string name = string.Empty;
        ScriptScope scope = ScriptScope.Shared;
        string? world = null;
        int version = 0;
        DateTimeOffset saved = DateTimeOffset.MinValue;
        string description = string.Empty;
        List<ScriptParameter> parameters = [];
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            if (line == EndLine)
            {
                header = new ScriptHeaderFields(name, scope, world, version, description, saved, parameters);
                return true;
            }

            if (!line.StartsWith("# ", StringComparison.Ordinal))
            {
                return false;
            }

            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string key = line[2..colon];
            string value = line[(colon + 2)..];
            switch (key)
            {
                case NameKey:
                    name = value;
                    break;
                case ScopeKey:
                    scope = value == "world" ? ScriptScope.World : ScriptScope.Shared;
                    break;
                case WorldKey:
                    world = value;
                    break;
                case VersionKey:
                    _ = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out version);
                    break;
                case SavedKey:
                    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out saved))
                    {
                        saved = DateTimeOffset.MinValue;
                    }

                    break;
                case DescriptionKey:
                    description = value;
                    break;
                case ParametersKey:
                    parameters = Parameters(value);
                    break;
            }
        }

        return false;
    }

    private static void Line(StringBuilder text, string key, string value) =>
        text.Append("# ").Append(key).Append(": ").Append(value).Append('\n');

    private static JsonArray Parameters(IReadOnlyList<ScriptParameter> parameters)
    {
        JsonArray array = [];
        foreach (ScriptParameter parameter in parameters)
        {
            array.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["type"] = parameter.Type,
                ["default"] = parameter.HasDefault,
                ["kind"] = parameter.Kind
            });
        }

        return array;
    }

    private static List<ScriptParameter> Parameters(string json)
    {
        List<ScriptParameter> parameters = [];
        try
        {
            foreach (JsonNode? node in JsonNode.Parse(json) as JsonArray ?? [])
            {
                if (node is JsonObject parameter && JsonNodes.Text(parameter["name"]) is { } name)
                {
                    parameters.Add(new ScriptParameter(name, JsonNodes.Text(parameter["type"]),
                        JsonNodes.IsTrue(parameter["default"]),
                        JsonNodes.Text(parameter["kind"]) ?? ScriptParameter.PosOrKw));
                }
            }
        }
        catch (JsonException)
        {
            // A header hand-edited into bad JSON says nothing; the run then takes no arguments here.
        }

        return parameters;
    }
}
