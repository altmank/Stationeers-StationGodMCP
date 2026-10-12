#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Protocol;

/// <summary>
/// The game side of held restores, on the main thread: a logic type by name or number, whether a device takes a held
/// restore of it (it must write and read the type: the overwrite check reads it), and the device's value read and
/// written as write_logic reads and writes it.
/// </summary>
internal interface IRestoreDevices
{
    /// <summary>A logic type by name or number; throws ApiException with the refusal.</summary>
    LogicTypeView LogicType(JToken? token);

    /// <summary>Throws ApiException unless the device is in the world and writes and reads the type.</summary>
    void RequireHoldable(long referenceId, LogicTypeView type);

    /// <summary>The device's value now; false with the refusal's code when it is gone or the read fails.</summary>
    bool TryRead(long referenceId, ushort type, out double value, out string problem);

    /// <summary>Writes the value as write_logic does; false with the refusal's code when it cannot.</summary>
    bool TryWrite(long referenceId, ushort type, double value, out string problem);
}

/// <summary>
/// Held restores (protocol.md, Held restores): logic writes a stream registers with hold_restore, which the mod makes
/// itself when the stream's owner ends abruptly. They apply when the owner sends session_end, when a resume token's
/// window passes without a resume, or when a connection without a token closes without bye. A stream_end, a bye, or a
/// token's state ending after a bye clears them unapplied; a world change drops them, counted.
/// <para>
/// Each restore applies only in the world it was registered in, and only while the device still holds the value the
/// stream last wrote to that logic type: what write_logic and write_logic_many read back after the stream's write
/// (Wrote), or, when the stream has not written it since first registering it, the value the device held then (setting
/// the same device and logic type again changes the value to write, not the check). A device that holds another value
/// has been changed by someone else, and its restore is skipped and reported.
/// </para>
/// <para>
/// Bounded: MaximumPerStream restores per stream and MaximumPerOwner per owner; a change past either is refused whole.
/// The results of applying (applied, skipped with a reason, dropped) are counted for mod_info and, for an owner whose
/// connection holds a resume token, kept for the next welcome on that token (TakeReport). Everything but TakeReport
/// runs on the main thread; Wrote costs one field read while nothing is held.
/// </para>
/// </summary>
internal sealed class HeldRestores : ISessionEndStore
{
    internal const int MaximumPerStream = 256;
    internal const int MaximumPerOwner = 4096;
    internal const string TooManyRestoresCode = "too_many_restores";
    internal const string StreamScope = "stream";
    internal const string OwnerScope = "owner";

    /// <summary>A skipped restore's reason when the device no longer holds the stream's last written value.</summary>
    internal const string ChangedReason = "changed";

    /// <summary>Tokens whose results wait for a welcome; past it the oldest is forgotten.</summary>
    internal const int MaximumReports = 64;

    /// <summary>Entries each of applied and skipped lists in one report; the rest are counted in unlisted.</summary>
    internal const int MaximumListed = 32;

    private readonly IRestoreDevices _devices;
    private readonly Func<string> _worldId;
    private readonly Dictionary<CallContext, StreamRestores> _streams = new Dictionary<CallContext, StreamRestores>();
    private readonly Dictionary<string, int> _perOwner = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<CallContext> _ending = new List<CallContext>();
    private readonly object _reportSync = new object();
    private readonly Dictionary<string, RestoreReport> _reports = new Dictionary<string, RestoreReport>(StringComparer.Ordinal);
    private readonly List<string> _reportOrder = new List<string>();
    private int _held;

    /// <summary>worldId: the running world's id (WorldScope), read on the main thread.</summary>
    internal HeldRestores(IRestoreDevices devices, Func<string> worldId)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _worldId = worldId ?? throw new ArgumentNullException(nameof(worldId));
    }

    /// <summary>Restores held, every owner and stream together.</summary>
    internal int Held => _held;

    /// <summary>Restores written since the mod loaded (mod_info runtime.restores).</summary>
    internal long Applied { get; private set; }

    /// <summary>Restores not written because the device was changed, gone or refused the write.</summary>
    internal long Skipped { get; private set; }

    /// <summary>Restores dropped unwritten because the world they were registered in is gone.</summary>
    internal long Dropped { get; private set; }

    /// <summary>
    /// hold_restore for the stream of context: clear_all, then each clear, then each set (replacing an entry for the same
    /// device and logic type). Refused whole, nothing changed, when an entry names no holdable device or logic type or
    /// the bounds would be passed. token is the owner's resume token, for its next welcome; null without one. Returns
    /// the stream's restores after the change, in registration order.
    /// </summary>
    internal List<HeldRestoreView> Change(CallContext context, string? token, bool clearAll,
        List<(ThingId Id, JToken? LogicType)> clear, List<(ThingId Id, JToken? LogicType, double Value)> set)
    {
        if (context.IsNone)
        {
            return new List<HeldRestoreView>();
        }

        List<RestoreKey> cleared = new List<RestoreKey>(clear.Count);
        foreach ((ThingId id, JToken? logicType) in clear)
        {
            cleared.Add(new RestoreKey(id.Value, _devices.LogicType(logicType).Id));
        }

        List<(RestoreKey Key, LogicTypeView Type, double Value)> wanted =
            new List<(RestoreKey, LogicTypeView, double)>(set.Count);
        foreach ((ThingId id, JToken? logicType, double value) in set)
        {
            LogicTypeView type = _devices.LogicType(logicType);
            _devices.RequireHoldable(id.Value, type);
            wanted.Add((new RestoreKey(id.Value, type.Id), type, value));
        }

        _streams.TryGetValue(context, out StreamRestores? stream);
        int before = stream?.Count ?? 0;
        int after = Projected(stream, clearAll, cleared, wanted);
        if (after > MaximumPerStream)
        {
            throw TooMany(MaximumPerStream, StreamScope);
        }

        _perOwner.TryGetValue(context.Owner!, out int owned);
        if (owned - before + after > MaximumPerOwner)
        {
            throw TooMany(MaximumPerOwner, OwnerScope);
        }

        // A new entry's overwrite check starts from the device's value now; read before anything changes, so a read
        // that fails refuses the whole call.
        Dictionary<RestoreKey, double> now = new Dictionary<RestoreKey, double>();
        foreach ((RestoreKey key, LogicTypeView _, double _) in wanted)
        {
            bool kept = stream != null && !clearAll && !cleared.Contains(key) && stream.Get(key) != null;
            if (kept || now.ContainsKey(key))
            {
                continue;
            }

            if (!_devices.TryRead(key.ReferenceId, key.LogicType, out double value, out string problem))
            {
                throw new ApiException(problem,
                    $"Device {key.ReferenceId} could not be read, so no restore of it can be held. Nothing changed.");
            }

            now.Add(key, value);
        }

        stream ??= new StreamRestores();
        if (clearAll)
        {
            stream.Clear();
        }

        foreach (RestoreKey key in cleared)
        {
            stream.Remove(key);
        }

        string world = _worldId();
        foreach ((RestoreKey key, LogicTypeView type, double value) in wanted)
        {
            Restore? restore = stream.Get(key);
            if (restore != null)
            {
                restore.Type = type;
                restore.Value = value;
                continue;
            }

            stream.Add(new Restore(context.Stream, key, type, value, world) { LastWritten = now[key] });
        }

        Count(context, stream, before, token);
        return stream.Views();
    }

    /// <summary>
    /// write_logic or write_logic_many wrote value (what the device reads back after it) to a device for the call of
    /// context: the stream's last written value for a restore it holds of that device and logic type.
    /// </summary>
    internal void Wrote(CallContext context, long referenceId, ushort logicType, double value)
    {
        if (_held == 0 || context.IsNone || !_streams.TryGetValue(context, out StreamRestores? stream))
        {
            return;
        }

        Restore? restore = stream.Get(new RestoreKey(referenceId, logicType));
        if (restore != null)
        {
            restore.LastWritten = value;
        }
    }

    /// <summary>
    /// Streams ended: a stream_end, an owner's state ending (a bye, a closed connection, a passed window) clear their
    /// restores unapplied; a world change drops every restore, counted and reported.
    /// </summary>
    public void Ended(StreamEnd end)
    {
        if (_held == 0)
        {
            return;
        }

        if (end.IsWorld)
        {
            _ending.AddRange(_streams.Keys);
            foreach (CallContext context in _ending)
            {
                StreamRestores stream = _streams[context];
                Dropped += stream.Count;
                Report(context.Owner!)?.Drop(stream.Count);
                Forget(context);
            }

            _ending.Clear();
            return;
        }

        if (!end.IsWholeConnection)
        {
            CallContext one = new CallContext(end.Owner!, end.Stream);
            if (_streams.ContainsKey(one))
            {
                Forget(one);
            }

            return;
        }

        Collect(end.Owner!);
        foreach (CallContext context in _ending)
        {
            Forget(context);
        }

        _ending.Clear();
    }

    /// <summary>
    /// The owner's session ended abruptly (session_end, a passed resume window, a closed connection without bye and
    /// without a token): every stream of it applies its restores, lowest stream first, each in registration order.
    /// </summary>
    public void SessionEnded(string owner)
    {
        if (_held == 0)
        {
            return;
        }

        Collect(owner);
        _ending.Sort(static (left, right) => left.Stream.CompareTo(right.Stream));
        RestoreReport? report = Report(owner);
        string world = _worldId();
        foreach (CallContext context in _ending)
        {
            foreach (Restore restore in _streams[context].Entries)
            {
                Apply(restore, world, report);
            }

            Forget(context);
        }

        _ending.Clear();
    }

    /// <summary>
    /// What applying did for the owners of a resume token since its last welcome, taken once (any thread); null when
    /// nothing was applied, skipped or dropped.
    /// </summary>
    internal RestoreReport? TakeReport(string token)
    {
        lock (_reportSync)
        {
            if (!_reports.TryGetValue(token, out RestoreReport? report))
            {
                return null;
            }

            _reports.Remove(token);
            _reportOrder.Remove(token);
            // A copy: the main thread may still be adding to the report it holds while this one is written.
            return report.Copy();
        }
    }

    private void Apply(Restore restore, string world, RestoreReport? report)
    {
        if (!string.Equals(restore.World, world, StringComparison.Ordinal))
        {
            Dropped++;
            report?.Drop(1);
            return;
        }

        long id = restore.Key.ReferenceId;
        if (!_devices.TryRead(id, restore.Key.LogicType, out double now, out string problem))
        {
            Skipped++;
            report?.Skip(new RestoreResultView(restore, problem, null));
            return;
        }

        if (now != restore.LastWritten)
        {
            Skipped++;
            report?.Skip(new RestoreResultView(restore, ChangedReason, now));
            return;
        }

        if (!_devices.TryWrite(id, restore.Key.LogicType, restore.Value, out problem))
        {
            Skipped++;
            report?.Skip(new RestoreResultView(restore, problem, now));
            return;
        }

        Applied++;
        report?.Apply(new RestoreResultView(restore, null, null));
    }

    // The owner's report, made when its connection holds a resume token; null without one. Main thread.
    private RestoreReport? Report(string owner)
    {
        if (!_tokens.TryGetValue(owner, out string? token))
        {
            return null;
        }

        lock (_reportSync)
        {
            if (_reports.TryGetValue(token, out RestoreReport? report))
            {
                return report;
            }

            if (_reportOrder.Count >= MaximumReports)
            {
                _reports.Remove(_reportOrder[0]);
                _reportOrder.RemoveAt(0);
            }

            report = new RestoreReport(_reportSync);
            _reports.Add(token, report);
            _reportOrder.Add(token);
            return report;
        }
    }

    // The owner's streams with restores, into _ending.
    private void Collect(string owner)
    {
        foreach (CallContext context in _streams.Keys)
        {
            if (string.Equals(context.Owner, owner, StringComparison.Ordinal))
            {
                _ending.Add(context);
            }
        }
    }

    private void Forget(CallContext context)
    {
        StreamRestores stream = _streams[context];
        _streams.Remove(context);
        Recount(context.Owner!, -stream.Count);
    }

    private void Count(CallContext context, StreamRestores stream, int before, string? token)
    {
        if (stream.Count == 0)
        {
            _streams.Remove(context);
        }
        else
        {
            _streams[context] = stream;
        }

        if (token != null)
        {
            _tokens[context.Owner!] = token;
        }

        Recount(context.Owner!, stream.Count - before);
    }

    private void Recount(string owner, int change)
    {
        _held += change;
        _perOwner.TryGetValue(owner, out int owned);
        owned += change;
        if (owned > 0)
        {
            _perOwner[owner] = owned;
            return;
        }

        _perOwner.Remove(owner);
        _tokens.Remove(owner);
    }

    // How many restores the stream holds after the change.
    private static int Projected(StreamRestores? stream, bool clearAll, List<RestoreKey> cleared,
        List<(RestoreKey Key, LogicTypeView Type, double Value)> wanted)
    {
        HashSet<RestoreKey> after = new HashSet<RestoreKey>();
        if (stream != null && !clearAll)
        {
            foreach (Restore restore in stream.Entries)
            {
                after.Add(restore.Key);
            }
        }

        foreach (RestoreKey key in cleared)
        {
            after.Remove(key);
        }

        foreach ((RestoreKey key, LogicTypeView _, double _) in wanted)
        {
            after.Add(key);
        }

        return after.Count;
    }

    private static ApiException TooMany(int limit, string scope) =>
        new ApiException(TooManyRestoresCode,
            scope == StreamScope
                ? $"A stream holds at most {limit} restores; clear some first. Nothing changed."
                : $"One connection's streams hold at most {limit} restores together; clear some first. Nothing changed.",
            new Dictionary<string, object> { ["limit"] = limit, ["scope"] = scope });

    /// <summary>A device and logic type: one restore per pair per stream.</summary>
    internal readonly struct RestoreKey : IEquatable<RestoreKey>
    {
        internal RestoreKey(long referenceId, ushort logicType)
        {
            ReferenceId = referenceId;
            LogicType = logicType;
        }

        internal long ReferenceId { get; }

        internal ushort LogicType { get; }

        public bool Equals(RestoreKey other) => ReferenceId == other.ReferenceId && LogicType == other.LogicType;

        public override bool Equals(object? obj) => obj is RestoreKey other && Equals(other);

        public override int GetHashCode() => unchecked((ReferenceId.GetHashCode() * 397) ^ LogicType);
    }

    /// <summary>One held restore: the value to write, and the value the stream last wrote (the overwrite check).</summary>
    internal sealed class Restore
    {
        internal Restore(long stream, RestoreKey key, LogicTypeView type, double value, string world)
        {
            Stream = stream;
            Key = key;
            Type = type;
            Value = value;
            World = world;
        }

        internal long Stream { get; }

        internal RestoreKey Key { get; }

        internal LogicTypeView Type { get; set; }

        internal double Value { get; set; }

        /// <summary>The world id at registration.</summary>
        internal string World { get; }

        internal double LastWritten { get; set; }
    }

    /// <summary>One stream's restores by key, and in registration order (at most MaximumPerStream).</summary>
    private sealed class StreamRestores
    {
        private readonly Dictionary<RestoreKey, Restore> _byKey = new Dictionary<RestoreKey, Restore>();

        internal List<Restore> Entries { get; } = new List<Restore>();

        internal int Count => Entries.Count;

        internal Restore? Get(RestoreKey key) => _byKey.TryGetValue(key, out Restore? restore) ? restore : null;

        internal void Add(Restore restore)
        {
            _byKey.Add(restore.Key, restore);
            Entries.Add(restore);
        }

        internal void Remove(RestoreKey key)
        {
            if (_byKey.TryGetValue(key, out Restore? restore))
            {
                _byKey.Remove(key);
                Entries.Remove(restore);
            }
        }

        internal void Clear()
        {
            _byKey.Clear();
            Entries.Clear();
        }

        internal List<HeldRestoreView> Views()
        {
            List<HeldRestoreView> views = new List<HeldRestoreView>(Entries.Count);
            foreach (Restore restore in Entries)
            {
                views.Add(new HeldRestoreView(restore));
            }

            return views;
        }
    }
}

/// <summary>One held restore as hold_restore answers it: the device, the logic type and the value it will write.</summary>
internal sealed class HeldRestoreView
{
    internal HeldRestoreView(HeldRestores.Restore restore)
    {
        ReferenceId = new ThingId(restore.Key.ReferenceId);
        LogicType = restore.Type;
        Value = restore.Value;
    }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double Value { get; }
}

/// <summary>hold_restore's result: the stream's restores after the change, in registration order.</summary>
internal sealed class HeldRestoresView
{
    internal HeldRestoresView(List<HeldRestoreView> restores) => Restores = restores;

    public List<HeldRestoreView> Restores { get; }
}

/// <summary>
/// One restore's result in welcome.restores: its stream, device, logic type and value; for a skipped one the reason
/// (changed, or the refusal's code) and, where it was read, the device's value then.
/// </summary>
internal sealed class RestoreResultView
{
    internal RestoreResultView(HeldRestores.Restore restore, string? reason, double? current)
    {
        Stream = restore.Stream;
        ReferenceId = new ThingId(restore.Key.ReferenceId);
        LogicType = restore.Type;
        Value = restore.Value;
        Reason = reason;
        Current = current;
    }

    public long Stream { get; }

    public ThingId ReferenceId { get; }

    public LogicTypeView LogicType { get; }

    public double Value { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? Reason { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public double? Current { get; }
}

/// <summary>
/// welcome.restores: what the mod's held restores did for a resume token's owners since its last welcome. At most
/// HeldRestores.MaximumListed entries in each list; unlisted counts the rest, written only when above 0. Filled on the
/// main thread and written on a connection's thread, under the store's report lock.
/// </summary>
internal sealed class RestoreReport
{
    private readonly object _sync;

    internal RestoreReport(object sync) => _sync = sync;

    public List<RestoreResultView> Applied { get; } = new List<RestoreResultView>();

    public List<RestoreResultView> Skipped { get; } = new List<RestoreResultView>();

    public int Dropped { get; private set; }

    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public int Unlisted { get; private set; }

    internal void Apply(RestoreResultView result) => List(Applied, result);

    /// <summary>A copy to write, made under the report lock.</summary>
    internal RestoreReport Copy()
    {
        RestoreReport copy = new RestoreReport(new object()) { Dropped = Dropped, Unlisted = Unlisted };
        copy.Applied.AddRange(Applied);
        copy.Skipped.AddRange(Skipped);
        return copy;
    }

    internal void Skip(RestoreResultView result) => List(Skipped, result);

    internal void Drop(int count)
    {
        lock (_sync)
        {
            Dropped += count;
        }
    }

    private void List(List<RestoreResultView> list, RestoreResultView result)
    {
        lock (_sync)
        {
            if (list.Count < HeldRestores.MaximumListed)
            {
                list.Add(result);
            }
            else
            {
                Unlisted++;
            }
        }
    }
}
