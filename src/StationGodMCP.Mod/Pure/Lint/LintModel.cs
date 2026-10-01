#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// The lint model: one set of object types for everything a rule can read, with each field's type and meaning.
/// lint_rules fields lists it; the game side fills each field from the game's own objects.
/// </summary>
internal static class LintModel
{
    internal static readonly ObjectType Thing = new ObjectType("thing",
        "Anything built: a cable, pipe or chute piece, a device, a frame, wall, window or door, a large device.");

    internal static readonly ObjectType Port = new ObjectType("port", "One cable, pipe or chute connection of a device.");

    internal static readonly ObjectType Network = new ObjectType("network", "A cable, pipe or chute network.");

    internal static readonly ObjectType Cell = new ObjectType("cell",
        "A 0.5 m small-grid cell (size 0.5), or a 2 m cell (size 2) where a 2 m structure registers.");

    internal static readonly ObjectType Room = new ObjectType("room", "A room the game has found: cells sealed from the outside.");

    internal static readonly ObjectType Slot = new ObjectType("slot", "One slot of a thing.");

    internal static readonly ObjectType World = new ObjectType("world", "The world: the sun, the day and the outdoor air.");

    internal static readonly ObjectType Sun = new ObjectType("sun", "Where the sun is now and the path it takes over the day.");

    internal static readonly ObjectType Atmosphere = new ObjectType("atmosphere", "A body of gas: the outdoors, a room.");

    internal static readonly ObjectType Rotation = new ObjectType("rotation", "Which way a thing faces.");

    internal static readonly ObjectType Box = new ObjectType("box", "An axis-aligned box in metres.");

    internal static readonly ObjectType Mount = new ObjectType("mount", "How a face-mounted thing sits on its plane.");

    internal static readonly ObjectType Chip = new ObjectType("chip", "A program chip (IC10 or Lua) in a housing on a data network.");

    internal static readonly ObjectType Batch = new ObjectType("batch",
        "A batch operation in a chip's program naming devices by prefab hash and name hash (lbn, sbn and the like).");

    internal static readonly ObjectType Placement = new ObjectType("placement",
        "Whether a player could place a thing again where it stands (check_replaceable's rule).");

    internal static readonly ObjectType Controls = new ObjectType("controls",
        "The side of a device that carries its slots, buttons and switches.");

    private static readonly Dictionary<string, ObjectType> Objects = new Dictionary<string, ObjectType>(StringComparer.Ordinal);

    static LintModel()
    {
        LintType number = LintType.Number, text = LintType.String, flag = LintType.Bool, vec = LintType.Vec;
        LintType numberOrNull = LintType.Nullable(number), textOrNull = LintType.Nullable(text);
        LintType things = LintType.ListOf(Thing), cells = LintType.ListOf(Cell), networks = LintType.ListOf(Network);
        LintType gases = LintType.MapOf(number);

        Thing
            .Field("reference_id", number, "The game's reference id.")
            .Field("prefab", text, "Prefab name, e.g. StructureSolarPanel.")
            .Field("prefab_hash", number, "Prefab hash (as IC10's HASH(\"prefab\") gives).")
            .Field("display_name", text, "The name a player sees: its label when it has one, else its kind's name.")
            .Field("label", textOrNull, "The label a labeller gave it; null when none.")
            .Field("kind", text, "cable, pipe, chute (network pieces), in_line_tank, passive_vent (stand in a pipe's slot), frame, wall, window, door, device (small-grid devices and things), structure (other 2 m structures, large devices included).")
            .Field("runtime_type", text, "The game class, e.g. SolarPanel.")
            .Field("runtime_types", LintType.ListOf(text), "The game class and every base class, e.g. [\"SolarPanel\", \"Device\", \"SmallGrid\", \"Structure\", \"Thing\"].")
            .Field("grid", text, "small (0.5 m grid) or large (2 m grid).")
            .Field("build_state", number, "Build state index (0 is the kit; finished is the last).")
            .Field("finished", flag, "At its last build state.")
            .Field("broken", flag, "A broken structure (a fire-burnt device).")
            .Field("planned", flag, "Part of a dry run's plan, not built yet: only its prefab, place, turn, cells and ports are known.")
            .Field("position", vec, "Where it stands, metres.")
            .Field("rotation", LintType.Nullable(Rotation), "Which way it faces; null when not on the grid's axes.")
            .Field("mesh_box", LintType.Nullable(Box), "The box its meshes fill.")
            .Field("cells", cells, "The cells it registers in: small cells for small-grid things, 2 m cells for 2 m structures.")
            .Field("room", LintType.Nullable(Room), "The room it stands in; null outdoors or in no room.")
            .Field("outdoors", flag, "It stands in no room.")
            .Field("slots", LintType.ListOf(Slot), "Its slots.")
            .Field("mounted", LintType.Nullable(Mount), "How it sits on its plane, for a face-mounted thing; null otherwise.")
            .Field("network", LintType.Nullable(Network), "The network a cable, pipe or chute piece belongs to.")
            .Field("networks", networks, "Every network it is on: a piece's own; a device's cable networks (power and data) and the networks its ports join.")
            .Field("ports", LintType.ListOf(Port), "Its cable, pipe and chute ports.")
            .Field("flow", textOrNull, "What it does to a pipe flow from its inputs to its outputs: pump, valve, regulator, filter, mixer; null for anything else.")
            .Field("grade", textOrNull, "A cable piece's grade (normal, heavy, super_heavy); a pipe piece's (normal, insulated, normal_low_volume, insulated_low_volume, duct).")
            .Field("max_power", numberOrNull, "A cable piece's rating in watts: more through it burns it.")
            .Field("max_pressure", numberOrNull, "A pipe piece's rating in kPa: more in it bursts it.")
            .Field("insulated", flag, "An insulated pipe piece.")
            .Field("content", textOrNull, "A pipe piece's content: gas or liquid (null for anything else).")
            .Field("chip", LintType.Nullable(Chip), "The program chip it holds (an IC housing, a Lua circuit); null when none.");

        Port
            .Field("device", Thing, "The thing the port belongs to.")
            .Field("index", number, "Its index on the device, as connections and grid_survey number ports.")
            .Field("type", text, "The game's network type: Power, Data, PowerAndData, Pipe, PipeLiquid, Chute.")
            .Field("role", text, "The game's connection role: Input, Input2, Output, Output2, Waste, None and others.")
            .Field("flow", textOrNull, "in for inputs, out for outputs and waste, null for a role with no direction.")
            .Field("joining_cell", Cell, "The cell a piece joining the port stands in.")
            .Field("network", LintType.Nullable(Network), "The network joined to it; null when nothing is.")
            .Field("occupant", LintType.Nullable(Thing), "The piece of the port's kind standing in the joining cell; null when none.")
            .Field("joined", flag, "occupant joins the port (has an end toward it).")
            .Field("position", vec, "The joining cell's centre.");

        Network
            .Field("id", number, "The network's id.")
            .Field("kind", text, "cable, pipe or chute (a liquid pipe network is a pipe network with content liquid).")
            .Field("members", things, "Its pieces (cables, pipes, chutes and what stands in their slots).")
            .Field("devices", things, "The devices with a port on it.")
            .Field("chips", LintType.ListOf(Chip), "The program chips in housings on it (a cable network).")
            .Field("grades", LintType.ListOf(text), "The cable grades it is made of, lightest first.")
            .Field("min_cable_power", numberOrNull, "Its weakest cable's rating in watts; null for a pipe or chute network.")
            .Field("max_cable_power", numberOrNull, "Its strongest cable's rating in watts.")
            .Field("load", numberOrNull, "Watts it delivered last power tick (CableNetwork.CurrentLoad).")
            .Field("required_load", numberOrNull, "Watts its consumers ask for (CableNetwork.RequiredLoad).")
            .Field("potential_load", numberOrNull, "Watts its suppliers can give (CableNetwork.PotentialLoad). The power tick burns a cable rated below min(potential_load, required_load).")
            .Field("content", textOrNull, "A pipe network's content: gas or liquid.")
            .Field("gases", gases, "Moles by gas name (the game's Chemistry.GasType names: Oxygen, Nitrogen, CarbonDioxide, Methane, Pollutant, NitrousOxide, Water, Hydrogen, Ozone, LiquidNitrogen ...) for a pipe network; empty otherwise.")
            .Field("total_mol", number, "Total moles.")
            .Field("pressure", numberOrNull, "kPa.")
            .Field("temperature", numberOrNull, "Kelvin.")
            .Field("max_pressure", numberOrNull, "Its weakest pipe's rating in kPa.")
            .Field("volume", numberOrNull, "Litres.")
            .Field("outdoors", flag, "Any member stands outdoors.")
            .Field("position", vec, "Its first member's position.");

        Cell
            .Field("position", vec, "The cell's centre, metres.")
            .Field("size", number, "0.5 for a small cell, 2 for a 2 m cell.")
            .Field("large", Cell, "The 2 m cell it lies in (itself for a 2 m cell).")
            .Field("support", text, "What holds a piece there: inside_frame (in a frame's body), frame_face (on a frame's surface, edge or corner), wall_plane (on a wall or window's plane), air (nothing).")
            .Field("in_door_keepout", flag, "In a door's keep-out (its face and the configured band either side).")
            .Field("keepout_door", LintType.Nullable(Thing), "The door whose keep-out it is in.")
            .Field("on_window_face", flag, "On a window's face.")
            .Field("window", LintType.Nullable(Thing), "The window whose face it is on.")
            .Field("door_jamb", LintType.Nullable(Thing), "The door whose jamb band it lies in: on the door's plane band just past its side edges, within its height (walls only; floor and ceiling doors have none).")
            .Field("room", LintType.Nullable(Room), "Its room; null outdoors.")
            .Field("outdoors", flag, "In no room.")
            .Field("pieces", things, "Cable, pipe and chute pieces in it.")
            .Field("devices", things, "Devices and other small-grid things in it.")
            .Field("frame", LintType.Nullable(Thing), "The frame filling its 2 m cell.")
            .Field("blocker", LintType.Nullable(Thing), "The structure filling its 2 m cell with BlockGrid collision (a frame, a large full-cell structure): what stops a deep miner's drill.")
            .Field("walls", things, "Walls, windows and doors on its 2 m cell's six faces.");

        Room
            .Field("id", number, "The room's id.")
            .Field("cell_count", number, "Its 2 m cells.")
            .Field("pressure", numberOrNull, "kPa, at its first cell.")
            .Field("temperature", numberOrNull, "Kelvin, at its first cell.")
            .Field("gases", gases, "Moles by gas name, at its first cell.")
            .Field("devices", things, "The devices and 2 m structures standing in it, among the things the lint call reads.")
            .Field("position", vec, "Its first cell's centre.");

        Slot
            .Field("index", number, "The slot's index.")
            .Field("name", text, "The slot's name.")
            .Field("type", text, "The slot's type (the game's Slot.Class).")
            .Field("occupant", textOrNull, "The prefab name of what is in it; null when empty.")
            .Field("occupant_name", textOrNull, "Its display name.")
            .Field("quantity", number, "How many (a stack's quantity, 1 for a single item, 0 when empty).");

        World
            .Field("sun", Sun, "The sun.")
            .Field("day_length", number, "Seconds in a day.")
            .Field("outdoor", Atmosphere, "The planet's air, where no room is.");

        Sun
            .Field("direction", vec, "Unit vector toward the sun now.")
            .Field("up", flag, "Above the horizon now.")
            .Field("path", LintType.ListOf(vec), "Unit vectors toward the sun at even steps over one day (sun_blocked tests them).");

        Atmosphere
            .Field("gases", gases, "Moles by gas name.")
            .Field("pressure", number, "kPa.")
            .Field("temperature", number, "Kelvin.");

        Rotation
            .Field("facing", text, "Its forward: +x, -x, +y, -y, +z, -z.")
            .Field("up", text, "Its up.");

        Box
            .Field("min", vec, "Lowest corner.")
            .Field("max", vec, "Highest corner.")
            .Field("centre", vec, "Centre.")
            .Field("size", vec, "Size, metres.");

        Mount
            .Field("plane", text, "The face plane behind it, e.g. z=668.")
            .Field("outward", text, "Which way it faces out of the plane: +x ... -z.")
            .Field("back", Cell, "The cell just behind its mesh rectangle's centre, through the plane.")
            .Field("front", Cell, "The cell just in front of it.")
            .Field("sections", number, "How many 2 m wall sections its mesh rectangle spans (more than 0.1 m into each).")
            .Field("fits_one_section", flag, "Neither side of its rectangle is wider than 2.2 m: some shift puts it on one section.");

        Chip
            .Field("housing", Thing, "The housing (or circuit) holding the chip.")
            .Field("language", text, "ic10 or lua.")
            .Field("source", text, "The program.")
            .Field("pins", LintType.ListOf(LintType.Nullable(Thing)), "The devices on the housing's pins d0, d1 ... (null where none is set).");

        Batch
            .Field("chip", Chip, "The chip whose program has it.")
            .Field("op", text, "The instruction (lbn, sbn, lbns, sbns, ...), or the Lua call.")
            .Field("prefab_hash", numberOrNull, "The prefab hash it names; null when not a constant.")
            .Field("prefab", textOrNull, "The prefab name inside HASH(\"...\"), when written so.")
            .Field("name_hash", numberOrNull, "The name hash; null when not a constant.")
            .Field("name", textOrNull, "The name inside HASH(\"...\"), when written so.")
            .Field("logic", textOrNull, "The logic type it reads or writes.")
            .Field("writes", flag, "It writes (sbn, sbns...) rather than reads.");

        Placement
            .Field("replaceable", LintType.Nullable(flag), "true: a player could place it again; false: refused; null: not checked.")
            .Field("rule", textOrNull, "Which rule refused it: support, mount, host, location, adjacent, collision, rotation, no_kit, off_grid.")
            .Field("reason", text, "The game's reason, or why it was not checked.");

        Controls
            .Field("side", text, "The world side its controls face: +x ... -z.")
            .Field("fallback", flag, "No control sits clearly on one side, so its forward stands in.")
            .Field("source", text, "What decided the side.");

        foreach (ObjectType type in new[] { Thing, Port, Network, Cell, Room, Slot, World, Sun, Atmosphere, Rotation, Box, Mount, Chip, Batch, Placement, Controls })
        {
            Objects[type.Name] = type;
        }
    }

    internal static IReadOnlyCollection<ObjectType> Types => Objects.Values;

    internal static ObjectType? ObjectNamed(string name) => Objects.TryGetValue(name, out ObjectType type) ? type : null;

    /// <summary>A type from its name: bool, number, string, vec, any, an object type, list&lt;T&gt;, map&lt;T&gt;, T?.</summary>
    internal static LintType ParseType(string text)
    {
        string name = text.Trim();
        if (name.EndsWith("?", StringComparison.Ordinal))
        {
            return LintType.Nullable(ParseType(name.Substring(0, name.Length - 1)));
        }

        if (name.StartsWith("list<", StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal))
        {
            return LintType.ListOf(ParseType(name.Substring(5, name.Length - 6)));
        }

        if (name.StartsWith("map<", StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal))
        {
            return LintType.MapOf(ParseType(name.Substring(4, name.Length - 5)));
        }

        switch (name)
        {
            case "bool":
                return LintType.Bool;
            case "number":
                return LintType.Number;
            case "string":
                return LintType.String;
            case "vec":
                return LintType.Vec;
            case "any":
                return LintType.Any;
        }

        return ObjectNamed(name) ?? throw new ArgumentException($"No lint type {name}.", nameof(text));
    }
}
