#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// get_ic_source, set_ic_source, get_ic_status, control_ic_execution and resolve_ic_selectors: the old StationApi and
/// IcRuntimeInspector shapes against the new views; no renames. Added for Lua chips: language, holder, chip,
/// source_length and lua.
/// </summary>
public sealed class IcWireTests
{
    private static readonly Dictionary<string, string> NoRenames = new Dictionary<string, string>();

    private static readonly IcPlace Place =
        new IcPlace("world", new ThingId(300), new ThingView(new ThingId(300), "StructureCircuitHousing", "Housing"));

    private static readonly ChipState Chip = new ChipState(3.0, false, "", "None", "OK");

    private static readonly IcChip Ic10Chip = new IcChip(IcChip.Ic10,
        new ThingView(new ThingId(301), "ItemIntegratedCircuit10", "Integrated Circuit (IC10)"), "yield", null);

    private static object OldPin() => new
    {
        index = 0, name = "d0", reference_id = "100", prefab_name = "StructureGasSensor", display_name = "Gas Sensor",
        label = (string?)null, reachable = true
    };

    private static List<IcPinView> NewPins() => new List<IcPinView>
    {
        new IcPinView(0, new ThingView(new ThingId(100), "StructureGasSensor", "Gas Sensor"), null, true)
    };

    [Fact]
    public void SourceSameWire()
    {
        WireCheck.SameAfterRenames(
            new { gateway_id = "world", reference_id = "300", source = "yield", line_number = 3.0 },
            new IcSourceView(Place, Ic10Chip, 3.0), NoRenames, "language", "holder", "chip", "source_length", "lua");
        WireCheck.SameAfterRenames(
            new
            {
                gateway_id = "world", reference_id = "300", source = "yield", line_number = 3.0,
                compilation_error = false, error_line = "", error_type = "None", error_code = "OK"
            },
            new IcSourceSetView(Place, Ic10Chip, Chip, new List<SourceNote>()), NoRenames, "compile_error_line",
            "compile_error_type", "language", "holder", "chip", "source_length", "lua", "warnings");
    }

    [Fact]
    public void StatusSameWire()
    {
        WireCheck.SameAfterRenames(
            new { gateway_id = "world", reference_id = "300", has_chip = false, pins = new List<object> { OldPin() } },
            new IcNoChipView(Place, NewPins()), NoRenames, "holder");
        var old = new
        {
            gateway_id = "world", reference_id = "300", has_chip = true, source = "yield", line_number = 3.0,
            compilation_error = false, error_line = "", error_type = "None", error_code = "OK",
            housing = new { kind = "ic_housing", on = true, powered = true, operable = true },
            pins = new List<object> { OldPin() },
            runtime = new
            {
                execution = new { next_address = 3, current_line = "yield", paused = false },
                registers = new List<object>
                {
                    new { index = 0, name = "r0", aliases = new List<string>(), value = (object)1.5 },
                    new { index = 16, name = "r16", aliases = new List<string> { "sp" }, value = (object)"NaN" }
                },
                register_count = 2, stack_pointer_register = 16, stack_pointer = (object)"NaN",
                return_address_register = 17, return_address = (object?)null,
                stack = new { size = 512, start_address = 0, values = new List<object> { 0.0 }, count = 1 },
                aliases = new List<object> { new { name = "sensor", target = "Device", index = (object)0 } },
                defines = new List<object> { new { name = "Max", value = (object)10.0 } },
                jump_tags = new List<object> { new { name = "loop", value = (object)2 } }
            }
        };
        IcRuntimeView runtime = new IcRuntimeView(new IcExecutionView(3, "yield", false),
            new List<RegisterView>
            {
                new RegisterView(0, new List<string>(), 1.5),
                new RegisterView(16, new List<string> { "sp" }, double.NaN)
            },
            new SpecialRegisters(16, double.NaN, 17, null),
            new StackWindowView(512, 0, new List<double> { 0.0 }),
            new IcSymbols(new List<AliasView> { new AliasView("sensor", "Device", 0) },
                new List<DefineView> { new DefineView("Max", 10.0) },
                new List<JumpTagView> { new JumpTagView("loop", 2) }));
        IcStatusView view = new IcStatusView(Place, Ic10Chip, Chip, new IcHolderView("ic_housing", true, true, true),
            new IcRuntimeParts(NewPins(), runtime));
        WireCheck.SameAfterRenames(old, view, NoRenames, "compile_error_line", "compile_error_type", "language", "holder",
            "chip", "source_length", "lua");
    }

    [Fact]
    public void ControlAndSelectorsSameWire()
    {
        WireCheck.SameAfterRenames(
            new
            {
                gateway_id = "world", reference_id = "300", action = "step", paused = true, previous_line = 2.0,
                line_number = 3.0, compilation_error = false, error_line = "", error_type = "None", error_code = "OK"
            },
            new IcControlView(Place, new IcControlOutcome("step", true, 2.0), Chip, Ic10Chip), NoRenames,
            "compile_error_line", "compile_error_type", "language", "holder", "chip", "lua");
        var old = new
        {
            gateway_id = "world", reference_id = "300", db = DeviceWireTests.OldDevice(),
            pins = new List<object>
            {
                new { selector = "d0", connected = true, target = DeviceWireTests.OldDevice() },
                new { selector = "d1", connected = false, target = (object?)null }
            },
            aliases = new List<object>(),
            stable_selectors = new List<object>
            {
                new
                {
                    reference_id = "100", display_name = "Gas Sensor", prefab_name = "StructureGasSensor",
                    prefab_hash = -1252983604, name_hash = (int?)12345, selector_kind = "prefab_and_name_hash",
                    unique = true, collision_count = 1,
                    ic10_example = "lbn r0 -1252983604 12345 <LogicType> Average"
                }
            },
            stable_selector_count = 1,
            note = "A prefab/name-hash selector is unique only while exactly one visible device has that pair. " +
                "Renaming a device changes its NameHash."
        };
        IcSelectorsView view = new IcSelectorsView(Place, DeviceWireTests.NewDevice(),
            new List<PinTargetView>
            {
                new PinTargetView(0, true, DeviceWireTests.NewDevice()), new PinTargetView(1, false, null)
            },
            new List<AliasView>(),
            new List<StableSelectorView>
            {
                new StableSelectorView(new ThingView(new ThingId(100), "StructureGasSensor", "Gas Sensor"),
                    -1252983604, 12345, 1, reachable: true)
            }, 4);
        WireCheck.SameAfterDrops(old, view, NoRenames, new[] { "note" }, "stable_selectors[].reachable",
            "batch_device_count", "note");
    }
}
