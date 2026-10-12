#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The write rules on a logic write to a game device (OpeningLint): which device it is (ActiveVent, PoweredVent and
/// PassiveVent are vents; thing.flow names valves, pumps and regulators), whether the write opens it, and the rules
/// over its pipe networks as they hold now. A write of any other logic type, or to a thing that is not a structure (a
/// worn item), costs one switch. When the rules cannot run, the write goes ahead with the warning lint_unavailable.
/// Main thread only.
/// </summary>
internal static class OpeningWrites
{
    internal static OpeningVerdict Judge(ScopedTarget device, LogicType type, double? previous, double value)
    {
        OpeningLogic logic = LogicOf(type);
        if (logic == OpeningLogic.None || !(device.Thing is Structure structure))
        {
            return OpeningVerdict.Closed;
        }

        OpeningDevice kind = OpeningTransition.DeviceOf(IsVent(structure), ThingSubject.FlowOf(structure));
        int modes = logic == OpeningLogic.Mode && structure is Device modal ? modal.ModeStrings?.Length ?? 2 : 2;
        try
        {
            return OpeningLint.Judge(kind, logic, previous, value, modes, LintRuleFiles.Current,
                () => World(structure, kind));
        }
        catch (Exception error)
        {
            // A rule file or a game read that throws: the write is not held up by a check that cannot run.
            return new OpeningVerdict(true, new List<LintFinding>(), new List<LintFinding>
            {
                new LintFinding("unavailable", ConflictLevel.Warning, 0,
                    $"The write lint rules could not run on device {structure.ReferenceId}: " +
                    $"{error.GetType().Name}: {error.Message}", structure.ReferenceId, Vec3.Zero, null, null, null)
            }, 0.0);
        }
    }

    private static ILintWorld World(Structure structure, OpeningDevice kind)
    {
        GameLintWorld around = GameLintWorld.Around();
        ThingSubject subject = around.Thing(structure) ??
                               throw new InvalidOperationException("the device is being taken down");
        return new OpeningWorld(around, subject, kind);
    }

    private static bool IsVent(Structure structure) =>
        structure is ActiveVent || structure is PoweredVent || structure is PassiveVent;

    private static OpeningLogic LogicOf(LogicType type) => type switch
    {
        LogicType.On => OpeningLogic.On,
        LogicType.Open => OpeningLogic.Open,
        LogicType.Mode => OpeningLogic.Mode,
        LogicType.Setting => OpeningLogic.Setting,
        _ => OpeningLogic.None
    };
}
