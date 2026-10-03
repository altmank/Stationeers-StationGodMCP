#nullable enable

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// Rocket-to-rocket fuel transfer through umbilical sockets. In the Transfer action each paired gas or liquid socket
/// mixes its pipe network with its partner's every atmospheric tick (RocketGasUmbilicalFemale.OnAtmosphericTick,
/// RocketGasUmbilicalFemale.cs:142-148: AtmosphereHelper.Mix(..., All) shares everything by volume), and each side's
/// tanks keep mixing with their own pipe; left long enough, both sides hold the same moles per litre. Nothing pushes
/// past that: a pump inside the receiving rocket moves more.
/// </summary>
internal static class SocketTransfer
{
    /// <summary>The moles this side gains (negative: gives) once both sides hold the same moles per litre.</summary>
    internal static double Equalised(double ourMoles, double ourLitres, double theirMoles, double theirLitres)
    {
        double litres = ourLitres + theirLitres;
        return litres > 0.0 ? (ourMoles + theirMoles) * ourLitres / litres - ourMoles : 0.0;
    }
}
