#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>move_gas: a queued move's prediction, or an applied or failed move's outcome.</summary>
internal sealed class MoveGasView
{
    internal const string Queued = "queued";

    /// <summary>status of a dry run: the prediction only, nothing queued.</summary>
    internal const string DryRun = "dry_run";

    internal MoveGasView(string? transferId, string status, bool predicted, bool joined, GasSideView? from,
        GasSideView? to, List<MovedGasView> moved, ErrorView? error)
    {
        TransferId = transferId;
        Status = status;
        Predicted = predicted;
        Joined = joined;
        From = from;
        To = to;
        Deleted = from != null && to == null;
        Moved = moved;
        Error = error;
    }

    /// <summary>Null for a dry run (nothing was queued).</summary>
    public string? TransferId { get; }

    /// <summary>queued (the reply is a prediction), dry_run (a prediction, nothing queued), applied, or failed.</summary>
    public string Status { get; }

    public bool Predicted { get; }

    public bool Joined { get; }

    public GasSideView? From { get; }

    public GasSideView? To { get; }

    public bool Deleted { get; }

    public List<MovedGasView> Moved { get; }

    public ErrorView? Error { get; }

    internal static MoveGasView Failed(string transferId, ErrorView error) =>
        new MoveGasView(transferId, "failed", false, false, null, null, new List<MovedGasView>(), error);
}

/// <summary>move_gas with transfer_id, for a move still waiting for the atmospherics tick.</summary>
internal sealed class GasMoveWaitingView
{
    internal GasMoveWaitingView(string transferId)
    {
        TransferId = transferId;
    }

    public string TransferId { get; }

    public string Status => "queued";
}

internal sealed class GasSideView
{
    internal GasSideView(List<GasMemberView> members, GasTotalView total, List<ThingView> joinedBy,
        GasRoomView? room)
    {
        Members = members;
        Total = total;
        JoinedBy = joinedBy;
        Room = room;
    }

    /// <summary>The named atmosphere first, then those joined to it; empty for a room.</summary>
    public List<GasMemberView> Members { get; }

    /// <summary>The members pooled; for a room, the room's air over its cells with air of their own.</summary>
    public GasTotalView Total { get; }

    /// <summary>The devices that join the members.</summary>
    public List<ThingView> JoinedBy { get; }

    /// <summary>The room, when a room was named; else null.</summary>
    public GasRoomView? Room { get; }
}

/// <summary>A room named as a move's from or to.</summary>
internal sealed class GasRoomView
{
    internal GasRoomView(string roomId, string roomType, int cellCount, int cellsWithAir, double volumeL)
    {
        RoomId = roomId;
        RoomType = roomType;
        CellCount = cellCount;
        CellsWithAir = cellsWithAir;
        VolumeL = volumeL;
    }

    /// <summary>As the rooms tool reports it.</summary>
    public string RoomId { get; }

    public string RoomType { get; }

    public int CellCount { get; }

    /// <summary>The cells with an atmosphere of their own: the ones gas is taken from or given to.</summary>
    public int CellsWithAir { get; }

    /// <summary>The volume of those cells.</summary>
    public double VolumeL { get; }
}

internal sealed class GasMemberView
{
    internal GasMemberView(GasOwnerView owner, ThingId atmosphereId, AtmosphereView before, AtmosphereView after)
    {
        Owner = owner;
        AtmosphereId = atmosphereId;
        Before = before;
        After = after;
    }

    public GasOwnerView Owner { get; }

    public ThingId AtmosphereId { get; }

    public AtmosphereView Before { get; }

    public AtmosphereView After { get; }
}

/// <summary>What owns an atmosphere: a thing, or a pipe or landing pad network (no prefab).</summary>
internal sealed class GasOwnerView
{
    internal GasOwnerView(string kind, ThingId referenceId, string? prefabName, string? displayName)
    {
        Kind = kind;
        ReferenceId = referenceId;
        PrefabName = prefabName;
        DisplayName = displayName;
    }

    /// <summary>thing, pipe_network or landing_pad_network.</summary>
    public string Kind { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }
}

/// <summary>A side's members pooled: pressure and temperature as if the game had mixed them all.</summary>
internal sealed class GasTotalView
{
    internal GasTotalView(AtmosphereView before, AtmosphereView after, AtmosphereView? afterBoiling)
    {
        Before = before;
        After = after;
        AfterBoiling = afterBoiling;
    }

    public AtmosphereView Before { get; }

    public AtmosphereView After { get; }

    /// <summary>After, once every liquid that would boil has; null when none would.</summary>
    public AtmosphereView? AfterBoiling { get; }
}

internal sealed class AtmosphereView
{
    internal AtmosphereView(double pressureKpa, double temperatureK, double totalMol, List<GasAmountView> gases)
    {
        PressureKpa = pressureKpa;
        TemperatureK = temperatureK;
        TotalMol = totalMol;
        Gases = gases;
    }

    public double PressureKpa { get; }

    public double TemperatureK { get; }

    public double TotalMol { get; }

    public List<GasAmountView> Gases { get; }
}

internal sealed class GasAmountView
{
    internal GasAmountView(string gas, double amountMol)
    {
        Gas = gas;
        AmountMol = amountMol;
    }

    public string Gas { get; }

    public double AmountMol { get; }
}

internal sealed class MovedGasView
{
    internal MovedGasView(string gas, double amountMol, double energyJ)
    {
        Gas = gas;
        AmountMol = amountMol;
        EnergyJ = energyJ;
    }

    public string Gas { get; }

    public double AmountMol { get; }

    public double EnergyJ { get; }
}
