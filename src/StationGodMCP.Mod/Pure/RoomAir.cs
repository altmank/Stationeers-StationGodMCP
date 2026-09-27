#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// One room's air added up cell by cell, as Room.CacheRoomData pools it: moles per gas, energy, heat capacity, gas
/// moles and volume summed; temperature is the pooled energy over the pooled heat capacity (IdealGas.Temperature) and
/// pressure the gas moles at that temperature in the summed volume. Also the box of the cell centres.
/// </summary>
internal sealed class RoomAir
{
    private readonly double[] _moles;

    private bool _hasCell;

    internal RoomAir(int gasCount)
    {
        _moles = new double[gasCount];
    }

    internal int CellCount { get; private set; }

    internal double VolumeL { get; private set; }

    internal double EnergyJ { get; private set; }

    internal double HeatCapacityJPerK { get; private set; }

    /// <summary>Moles in the gas state only; liquids are left out of pressure, as GetTotalMolesGasses does.</summary>
    internal double GasMol { get; private set; }

    internal double TotalMol { get; private set; }

    internal double MinX { get; private set; }

    internal double MinY { get; private set; }

    internal double MinZ { get; private set; }

    internal double MaxX { get; private set; }

    internal double MaxY { get; private set; }

    internal double MaxZ { get; private set; }

    internal double TemperatureK => HeatCapacityJPerK > 0.0 ? EnergyJ / HeatCapacityJPerK : 0.0;

    internal double PressureKpa => PlanetMath.PressureKpa(GasMol, TemperatureK, VolumeL);

    internal double MolesOf(int gasIndex) => _moles[gasIndex];

    internal int GasCount => _moles.Length;

    /// <summary>A cell's centre in metres, counted once per cell whether or not it has air.</summary>
    internal void AddCell(double x, double y, double z)
    {
        CellCount++;
        if (!_hasCell)
        {
            MinX = MaxX = x;
            MinY = MaxY = y;
            MinZ = MaxZ = z;
            _hasCell = true;
            return;
        }

        MinX = x < MinX ? x : MinX;
        MinY = y < MinY ? y : MinY;
        MinZ = z < MinZ ? z : MinZ;
        MaxX = x > MaxX ? x : MaxX;
        MaxY = y > MaxY ? y : MaxY;
        MaxZ = z > MaxZ ? z : MaxZ;
    }

    internal void AddVolume(double litres) => VolumeL += litres;

    /// <summary>One gas of one cell: its moles, energy and heat capacity, and whether it is in the gas state.</summary>
    internal void AddGas(int gasIndex, double moles, double energyJ, double heatCapacityJPerK, bool isGas)
    {
        _moles[gasIndex] += moles;
        TotalMol += moles;
        EnergyJ += energyJ;
        HeatCapacityJPerK += heatCapacityJPerK;
        if (isGas)
        {
            GasMol += moles;
        }
    }
}
