#nullable enable

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// The large world every default reply is measured on: a long-played base, sized from live saves where a number
/// was seen live (a 1,257-member cable network, a 60 KB Lua hub chip) and generous elsewhere.
/// </summary>
internal static class LargeWorld
{
    internal const int CableNetworkMembers = 1257;
    internal const int PipeNetworkMembers = 400;
    internal const int Devices = 150;
    internal const int DataNetworkDevices = 120;
    internal const int Things = 6000;
    internal const int Items = 2500;
    internal const int ItemTypes = 180;
    internal const int Containers = 120;
    internal const int Frames = 3000;
    internal const int OuterFrames = 900;
    internal const int Rooms = 40;
    internal const int Plants = 48;
    internal const int Rockets = 4;
    internal const int MiningSites = 30;
    internal const int Gateways = 3;
    internal const int TraderContacts = 6;
    internal const int LandingPads = 3;
    internal const int Vaults = 2;
    internal const int WaterSources = 60;
    internal const int Foods = 300;
    internal const int Drinks = 200;
    internal const int LintFindings = 400;
    internal const int LintRules = 30;
    internal const int Methods = 93;
    internal const int ReflectedMembers = 180;
    internal const int ConsoleLines = 500;

    /// <summary>A long run: cells of one place_* request across a base.</summary>
    internal const int RunCells = 120;

    /// <summary>A dense survey page's pieces and devices (box (717,197,678)-(738,201,683), live 185 KB).</summary>
    internal const int SurveyPagePieces = 250;
    internal const int SurveyPageDevices = 40;

    /// <summary>Items riding a busy chute network (live: 15 KB of networks_before.items in a place_chutes dry run).</summary>
    internal const int ChuteRidingItems = 120;

    internal const int LuaSourceChars = 60000;

    /// <summary>A batch a caller sends to a batch tool (read_logic_many, label, paint...): its reply is that long.</summary>
    internal const int TypicalBatch = 8;

    internal static readonly string LuaSource = "-- hub\n" + new string('x', LuaSourceChars - 7);
}
