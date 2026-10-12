#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// What the write rules see when a write opens a device (OpeningTransition): the pipe networks it opens to, as they
/// hold now, and the things those rules judge.
/// <list type="bullet">
/// <item>networks: the device's pipe networks (a vent's one; a valve's, pump's or regulator's both sides).</item>
/// <item>things: the device; for a valve, pump or regulator also every vent on those networks (their devices and
/// members), the vents it would let the other side out through.</item>
/// </list>
/// Every other set is empty. The world object and cell lookups are the surrounding world's. Nothing is planned.
/// </summary>
internal sealed class OpeningWorld : ILintWorld
{
    private static readonly IReadOnlyList<ILintObject> None = new List<ILintObject>();

    private readonly ILintWorld _around;
    private readonly List<ILintObject> _things = new List<ILintObject>();
    private readonly List<ILintObject> _networks = new List<ILintObject>();

    internal OpeningWorld(ILintWorld around, ILintObject device, OpeningDevice kind)
    {
        _around = around;
        _things.Add(device);
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal) { device.Key };
        foreach (LintValue value in device.Get(LintModel.Thing["networks"]).AsList)
        {
            ILintObject network = value.AsObject;
            if (network.Get(LintModel.Network["kind"]).AsString == "pipe" && seen.Add(network.Key))
            {
                _networks.Add(network);
            }
        }

        if (kind == OpeningDevice.Vent)
        {
            return;
        }

        foreach (ILintObject network in _networks)
        {
            AddVents(network.Get(LintModel.Network["devices"]), seen);
            AddVents(network.Get(LintModel.Network["members"]), seen);
        }
    }

    public ILintObject World => _around.World;

    public IReadOnlyList<ILintObject> Subjects(string set) =>
        set == "things" ? _things : set == "networks" ? _networks : None;

    public ILintObject? CellAt(Vec3 point, double size) => _around.CellAt(point, size);

    public bool IsPlanned(ILintObject subject) => false;

    private void AddVents(LintValue things, HashSet<string> seen)
    {
        foreach (LintValue value in things.AsList)
        {
            ILintObject thing = value.AsObject;
            if (!seen.Contains(thing.Key) && OpeningTransition.IsVent(thing) && seen.Add(thing.Key))
            {
                _things.Add(thing);
            }
        }
    }
}
