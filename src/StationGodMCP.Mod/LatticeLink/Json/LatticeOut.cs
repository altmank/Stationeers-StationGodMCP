#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Json;

/// <summary>
/// Lattice's values written as JSON in the forms LatticeIn reads (catalogue topic lattice): a union is {"case": payload}
/// or its bare name, cells are metres [x, y, z], turns rotation degrees, pieces, kits and items prefab names (the hash
/// where the game names none), structures {id, piece, at}, reasons {code, facts, game_message, from}.
/// </summary>
internal sealed class LatticeOut
{
    private readonly L.IGhostSystem _system;
    private readonly Func<int, string?> _itemName;

    internal LatticeOut(L.IGhostSystem system, Func<int, string?>? itemName = null)
    {
        _system = system;
        _itemName = itemName ?? (static _ => null);
    }

    // ---- values ------------------------------------------------------------------------------------------------

    internal static JArray Cell(L.Cell cell) => new JArray(Round(cell.X * 0.1), Round(cell.Y * 0.1), Round(cell.Z * 0.1));

    internal static JArray Offset(L.CellOffset offset) =>
        new JArray(Round(offset.X * 0.1), Round(offset.Y * 0.1), Round(offset.Z * 0.1));

    internal static JArray Point(L.WorldPoint point) => new JArray(Round(point.X), Round(point.Y), Round(point.Z));

    internal static JArray Vector(L.WorldVector vector) => new JArray(Round(vector.X), Round(vector.Y), Round(vector.Z));

    private static double Round(double value) => Math.Round(value, 3);

    internal static JArray Turn(L.Turn turn)
    {
        (int x, int y, int z) = LatticeGhosts.RotationOf(turn).EulerTurns();
        return new JArray(x * 90, y * 90, z * 90);
    }

    internal JToken Piece(L.PrefabKey piece) =>
        _system.Catalogue.Piece(piece).TryGet(out L.PieceEntry entry) ? new JValue(entry.PrefabName) : new JValue(piece.Hash);

    internal JToken Kit(L.KitKey kit) =>
        _system.Catalogue.Kit(kit).TryGet(out L.KitEntry entry) ? new JValue(entry.PrefabName) : new JValue(kit.Hash);

    internal JToken Item(L.ItemKey item) =>
        _itemName(item.Hash) is { } name ? new JValue(name) : new JValue(item.Hash);

    internal JObject Placement(L.Placement placement) => new JObject
    {
        ["piece"] = Piece(placement.Piece),
        ["at"] = Cell(placement.At),
        ["turn"] = Turn(placement.Turn),
    };

    internal JObject Structure(L.StructureRef structure) => new JObject
    {
        ["id"] = structure.Id.Value.ToString(CultureInfo.InvariantCulture),
        ["piece"] = Piece(structure.Piece),
        ["at"] = Cell(structure.At),
    };

    internal static JObject Reason(L.Reason reason)
    {
        JObject facts = new JObject();
        foreach (KeyValuePair<string, string> fact in reason.Facts)
        {
            facts[fact.Key] = fact.Value;
        }

        JObject written = new JObject { ["code"] = reason.Code.Value, ["text"] = LatticeText.Of(reason) };
        if (facts.Count > 0)
        {
            written["facts"] = facts;
        }

        if (reason.GameMessage.TryGet(out string message))
        {
            written["game_message"] = message;
        }

        written["from"] = reason.From.Value;
        return written;
    }

    internal static JToken Actor(L.Actor actor) => actor switch
    {
        L.Actor.Player player => new JObject { ["player"] = player.Client.Value },
        L.Actor.Agent agent => new JObject { ["agent"] = agent.Id.Value },
        L.Actor.Mod mod => new JObject { ["mod"] = mod.Id.Value },
        _ => new JValue(actor.GetType().Name)
    };

    internal static JToken Visibility(L.Visibility visibility) => visibility switch
    {
        L.Visibility.Everyone => new JValue("everyone"),
        L.Visibility.OwnerOnly => new JValue("owner_only"),
        L.Visibility.Team team => new JObject { ["team"] = team.Name.Value },
        _ => new JValue(visibility.GetType().Name)
    };

    internal static JToken Rgba(byte r, byte g, byte b, byte a) =>
        new JArray(Math.Round(r / 255.0, 3), Math.Round(g / 255.0, 3), Math.Round(b / 255.0, 3), Math.Round(a / 255.0, 3));

    internal static JObject Finish(L.Finish finish)
    {
        JObject written = new JObject();
        if (finish.Colour.TryGet(out L.PaintColour colour))
        {
            written["colour"] = colour.Index;
        }

        if (finish.Name.TryGet(out L.ThingName name))
        {
            written["name"] = name.Value;
        }

        if (finish.Setting.TryGet(out L.Setting setting))
        {
            written["setting"] = setting.Value;
        }

        return written;
    }

    internal static JArray Ids(IEnumerable<L.GhostId> ids)
    {
        JArray written = new JArray();
        foreach (L.GhostId id in ids)
        {
            written.Add(id.Value);
        }

        return written;
    }

    internal static JObject Attached(L.ValueMap<L.AttachmentKey, L.Attachment> attached)
    {
        JObject written = new JObject();
        foreach (KeyValuePair<L.AttachmentKey, L.Attachment> entry in attached)
        {
            written[entry.Key.Value] = entry.Value.Value;
        }

        return written;
    }

    // ---- ghosts and plans --------------------------------------------------------------------------------------

    internal JObject Intent(L.Intent intent)
    {
        switch (intent)
        {
            case L.Intent.Build build:
            {
                JObject target = Placement(build.Target);
                if (build.Finish.TryGet(out L.Finish finish))
                {
                    target["finish"] = Finish(finish);
                }

                return new JObject { ["build"] = target };
            }
            case L.Intent.Remove remove:
                return new JObject { ["remove"] = Structure(remove.Target) };
            case L.Intent.Swap swap:
            {
                JObject written = new JObject { ["old"] = Structure(swap.Old), ["new"] = Placement(swap.New) };
                if (swap.Finish.TryGet(out L.Finish finish))
                {
                    written["finish"] = Finish(finish);
                }

                return new JObject { ["swap"] = written };
            }
            case L.Intent.FinishOnly only:
                return new JObject
                {
                    ["finish_only"] = new JObject { ["target"] = Structure(only.Target), ["finish"] = Finish(only.Finish) }
                };
            default:
                return new JObject { ["unknown"] = intent.Kind.ToString() };
        }
    }

    internal JObject Ghost(L.Ghost ghost)
    {
        JObject written = new JObject { ["id"] = ghost.Id.Value };
        foreach (KeyValuePair<string, JToken?> part in Intent(ghost.Intent))
        {
            written[part.Key] = part.Value;
        }

        written["plan"] = ghost.Plan.Value;
        written["revision"] = ghost.Revision.Value;
        written["state"] = ghost.State switch
        {
            L.GhostState.Planned => new JValue("planned"),
            L.GhostState.Started started => new JObject { ["started"] = Structure(started.PartBuilt) },
            L.GhostState.Stale stale => new JObject { ["stale"] = Reason(stale.Why) },
            _ => new JValue(ghost.State.GetType().Name)
        };
        written["placed_by"] = Actor(ghost.PlacedBy);
        written["made_by"] = ghost.MadeBy.Value;
        written["placed_at"] = ghost.PlacedAt.ToString("o", CultureInfo.InvariantCulture);
        written["visibility"] = Visibility(ghost.Visibility);
        if (ghost.Label.TryGet(out L.Label label))
        {
            written["label"] = label.Value;
        }

        if (ghost.Tint.TryGet(out L.Tint tint))
        {
            written["tint"] = Rgba(tint.R, tint.G, tint.B, tint.A);
        }

        if (ghost.Groups.Count > 0)
        {
            JArray groups = new JArray();
            foreach (L.GroupName group in ghost.Groups)
            {
                groups.Add(group.Value);
            }

            written["groups"] = groups;
        }

        if (ghost.Attached.Count > 0)
        {
            written["attached"] = Attached(ghost.Attached);
        }

        written["look"] = LatticeLooks.LookOf(ghost).Report();
        return written;
    }

    internal JObject Plan(L.Plan plan)
    {
        JObject written = new JObject
        {
            ["id"] = plan.Id.Value,
            ["name"] = plan.Name.Value,
            ["revision"] = plan.Revision.Value,
            ["owner"] = Actor(plan.Owner),
            ["made_by"] = plan.MadeBy.Value,
            ["visibility"] = Visibility(plan.Visibility),
            ["default"] = plan.Id == _system.Plans.Default,
        };
        if (plan.HigherLayerData.Count > 0)
        {
            written["data"] = Attached(plan.HigherLayerData);
        }

        return written;
    }

    internal JObject Finished(L.FinishedPlan plan) => new JObject
    {
        ["id"] = plan.Id.Value,
        ["name"] = plan.Name.Value,
        ["owner"] = Actor(plan.Owner),
        ["finished_at"] = plan.FinishedAt.ToString("o", CultureInfo.InvariantCulture),
        ["pieces"] = plan.Pieces,
        ["box"] = new JArray(Cell(plan.Min), Cell(plan.Max)),
    };

    internal JObject Facts(L.PlanFacts facts) => new JObject
    {
        ["plan"] = facts.Plan.Value,
        ["at"] = facts.At.Value,
        ["valid_alone"] = Validity(facts.ValidAlone),
        ["valid_in_world"] = Validity(facts.ValidInWorld),
        ["buildable_next"] = Ids(facts.BuildableNext),
        ["progress"] = new JObject
        {
            ["done"] = facts.Progress.Done, ["started"] = facts.Progress.Started, ["remaining"] = facts.Progress.Remaining
        },
        ["consequences"] = Each(facts.Consequences, warning => new JObject
        {
            ["why"] = Reason(warning.Why), ["ghosts"] = Ids(warning.Ghosts)
        }),
        ["cost"] = Bill(facts.Cost),
    };

    private JToken Validity(L.Validity validity) => validity switch
    {
        L.Validity.Valid => new JValue("valid"),
        L.Validity.Invalid invalid => new JObject { ["invalid"] = Each(invalid.Failing, Finding) },
        _ => new JValue(validity.GetType().Name)
    };

    internal static string PlanState(L.PlanState state) => state switch
    {
        L.PlanState.Empty => "empty",
        L.PlanState.HasErrors => "has_errors",
        L.PlanState.Valid => "valid",
        L.PlanState.InProgress => "in_progress",
        L.PlanState.Complete => "complete",
        _ => state.GetType().Name
    };

    internal JObject Finding(L.Finding finding) => finding switch
    {
        L.Finding.Passes => new JObject { ["ghost"] = finding.Ghost.Value, ["passes"] = true },
        L.Finding.WaitsFor waits => new JObject { ["ghost"] = finding.Ghost.Value, ["waits_for"] = Ids(waits.Others) },
        L.Finding.WillFail fails => new JObject { ["ghost"] = finding.Ghost.Value, ["will_fail"] = Reason(fails.Why) },
        L.Finding.Warns warns => new JObject { ["ghost"] = finding.Ghost.Value, ["warns"] = Reason(warns.Why) },
        _ => new JObject { ["ghost"] = finding.Ghost.Value, ["unknown"] = finding.GetType().Name }
    };

    internal JObject Bill(L.Bill bill)
    {
        JArray tools = new JArray();
        foreach (L.ItemKey tool in bill.Tools)
        {
            tools.Add(Item(tool));
        }

        return new JObject
        {
            ["needed"] = Each(bill.Needed, Quantity),
            ["given_back"] = Each(bill.GivenBack, Quantity),
            ["net"] = Each(bill.Net, delta => new JObject { ["item"] = Item(delta.Item), ["change"] = delta.Change }),
            ["tools"] = tools,
        };
    }

    private JObject Quantity(L.ItemQuantity quantity) => new JObject { ["item"] = Item(quantity.Item), ["count"] = quantity.Count };

    internal JObject ChangeSet(L.ChangeSet changes) => new JObject
    {
        ["id"] = changes.Id.Value,
        ["command"] = changes.Command.Value.ToString(),
        ["by"] = Actor(changes.By),
        ["revision"] = changes.Revision.Value,
        ["at"] = changes.At.ToString("o", CultureInfo.InvariantCulture),
        ["added"] = Each(changes.Added, Ghost),
        ["changed"] = Each(changes.Changed, change => new JObject { ["before"] = Ghost(change.Before), ["after"] = Ghost(change.After) }),
        ["removed"] = Each(changes.Removed, removal => new JObject
        {
            ["ghost"] = Ghost(removal.Ghost), ["cause"] = Cause(removal.Cause)
        }),
        ["plans_opened"] = Each(changes.PlansOpened, Plan),
        ["plans_changed"] = Each(changes.PlansChanged, change => new JObject { ["before"] = Plan(change.Before), ["after"] = Plan(change.After) }),
        ["plans_finished"] = Each(changes.PlansFinished, plan => new JValue(plan.Value)),
    };

    private JToken Cause(L.RemovalCause cause) => cause switch
    {
        L.RemovalCause.Removed => new JValue("removed"),
        L.RemovalCause.Replaced replaced => new JObject { ["replaced"] = replaced.By.Value },
        L.RemovalCause.Done done => new JObject { ["done"] = Completion(done.How) },
        L.RemovalCause.TargetGone => new JValue("target_gone"),
        _ => new JValue(cause.GetType().Name)
    };

    internal JObject Completion(L.Completion completion)
    {
        JObject written = new JObject { ["at"] = completion.At.ToString("o", CultureInfo.InvariantCulture) };
        if (completion.Built.TryGet(out L.StructureRef built))
        {
            written["built"] = Structure(built);
        }

        return written;
    }

    internal JArray Refusals(L.ValueList<L.Refusal> refusals) => Each(refusals, refusal => new JObject
    {
        ["why"] = Reason(refusal.Why), ["ghosts"] = Ids(refusal.Ghosts)
    });

    // ---- previews, analysis, steps -----------------------------------------------------------------------------

    internal JObject Preview(L.Preview preview)
    {
        JObject written = new JObject
        {
            ["would_change"] = ChangeSet(preview.WouldChange),
            ["findings"] = Each(preview.Findings, Finding),
            ["bill"] = Bill(preview.Bill),
            ["effects"] = Analysis(preview.Effects),
        };
        if (preview.Run.TryGet(out L.RunReport run))
        {
            written["run"] = Run(run);
        }

        return written;
    }

    private JObject Run(L.RunReport run)
    {
        JObject written = new JObject
        {
            ["cells"] = Each(run.Cells, cell => Cell(cell)),
            ["pieces"] = Each(run.Pieces, Placement),
            ["cost"] = Bill(run.Cost),
            ["cost_as_singles"] = Bill(run.CostAsSingles),
            ["port_ends"] = Each(run.PortEnds, join =>
            {
                JObject end = new JObject
                {
                    ["end"] = PortEnd(join.End),
                    ["port"] = Port(join.Port),
                };
                if (join.JoinedBy.TryGet(out L.Placement by))
                {
                    end["joined_by"] = Placement(by);
                }

                return end;
            }),
        };
        if (run.FirstFailure.TryGet(out L.CellFailure failure))
        {
            written["first_failure"] = new JObject { ["at"] = Cell(failure.At), ["why"] = Reason(failure.Why) };
        }

        if (run.LaidWith.TryGet(out L.KitKey kit))
        {
            written["laid_with"] = Kit(kit);
        }

        return written;
    }

    internal static JToken PortEnd(L.PortEnd end) => end switch
    {
        L.PortEnd.OnGhost onGhost => new JObject { ["ghost"] = onGhost.Ghost.Value, ["port"] = onGhost.Port.Value },
        _ => new JValue(end.GetType().Name)
    };

    internal JObject Port(L.Port port)
    {
        JArray kinds = new JArray();
        foreach (L.PortKind kind in port.Kinds)
        {
            kinds.Add(kind.Value);
        }

        JObject written = new JObject
        {
            ["index"] = port.Index.Value,
            ["kinds"] = kinds,
            ["type"] = port.GameName,
            ["from"] = Cell(port.From),
            ["toward"] = Face(port.Toward),
            ["joins"] = Cell(port.Joins),
            ["joining_end"] = LatticeGhosts.IntoPort(port.Toward),
        };
        if (port.Role.TryGet(out L.PortRole role))
        {
            written["role"] = role.ToString();
        }

        return written;
    }

    internal static string Face(L.Face face) => face switch
    {
        L.Face.PlusX => "+x",
        L.Face.MinusX => "-x",
        L.Face.PlusY => "+y",
        L.Face.MinusY => "-y",
        L.Face.PlusZ => "+z",
        _ => "-z"
    };

    internal static string Axis(L.Axis axis) => axis switch { L.Axis.X => "x", L.Axis.Y => "y", _ => "z" };

    internal JObject Analysis(L.AnalysisReport report) => new JObject
    {
        ["networks"] = Each(report.Networks, effect => (JToken)(effect switch
        {
            L.NetworkEffect.Joins joins => new JObject { ["joins"] = new JArray(joins.A.Value, joins.B.Value) },
            L.NetworkEffect.Splits splits => new JObject { ["splits"] = splits.Network.Value },
            L.NetworkEffect.Load load => new JObject
            {
                ["load"] = new JObject
                {
                    ["network"] = load.Network.Value, ["before_w"] = load.Before.Value, ["after_w"] = load.After.Value,
                    ["rating_w"] = load.Rating.TryGet(out L.Watts rating) ? rating.Value : null
                }
            },
            _ => new JObject { ["unknown"] = effect.GetType().Name }
        })),
        ["rooms"] = Each(report.Rooms, effect => (JToken)(effect switch
        {
            L.RoomEffect.Opens opens => new JObject { ["opens"] = opens.Room.Value },
            L.RoomEffect.Seals seals => new JObject { ["seals"] = new JObject { ["cells"] = seals.Cells } },
            L.RoomEffect.Resizes resizes => new JObject
            {
                ["resizes"] = new JObject { ["room"] = resizes.Room.Value, ["before"] = resizes.Before, ["after"] = resizes.After }
            },
            _ => new JObject { ["unknown"] = effect.GetType().Name }
        })),
        ["gas"] = Each(report.Gas, effect => (JToken)(effect switch
        {
            L.GasEffect.Releases releases => new JObject
            {
                ["releases"] = new JObject
                {
                    ["at"] = Cell(releases.At),
                    ["into"] = releases.Into.TryGet(out L.RoomRef room) ? room.Value : null,
                    ["gas"] = GasMix(releases.Gas)
                }
            },
            L.GasEffect.Pollutes pollutes => new JObject
            {
                ["pollutes"] = new JObject { ["room"] = pollutes.Room.Value, ["mol"] = pollutes.Amount.Value }
            },
            _ => new JObject { ["unknown"] = effect.GetType().Name }
        })),
        ["facts"] = Each(report.Facts, fact =>
        {
            JObject values = new JObject();
            foreach (KeyValuePair<string, string> value in fact.Values)
            {
                values[value.Key] = value.Value;
            }

            return new JObject { ["from"] = fact.From.Value, ["code"] = fact.Code.Value, ["values"] = values };
        }),
    };

    private static JObject GasMix(L.GasMix mix) => new JObject
    {
        ["gases"] = Each(mix.Gases, gas => new JObject { ["gas"] = gas.Gas.Value, ["mol"] = gas.Amount.Value }),
        ["temperature_k"] = mix.Temperature.Value,
    };

    internal JObject Steps(L.StepPlan plan) => new JObject
    {
        ["ghost"] = plan.Ghost.Value,
        ["steps"] = Each(plan.Steps, Step),
        ["constraints"] = Each(plan.Constraints, constraint => (JToken)(constraint switch
        {
            L.Constraint.OnePipeJoinPerTick => new JValue("one_pipe_join_per_tick"),
            L.Constraint.NetworkOff off => new JObject { ["network_off"] = off.Network.Value },
            L.Constraint.After after => new JObject { ["after"] = after.Ghost.Value },
            _ => new JObject { ["unknown"] = constraint.GetType().Name }
        })),
    };

    private JObject Step(L.Step step) => step switch
    {
        L.Step.PlaceKit place => new JObject
        {
            ["place_kit"] = new JObject
            {
                ["kit"] = Kit(place.Kit), ["option"] = place.Option.Index, ["uses"] = Quantity(place.Uses),
                ["at"] = Placement(place.At)
            }
        },
        L.Step.Advance advance => new JObject
        {
            ["advance"] = new JObject
            {
                ["on"] = Target(advance.On), ["tool"] = Item(advance.Tool),
                ["material"] = advance.Material.TryGet(out L.ItemQuantity used) ? Quantity(used) : null
            }
        },
        L.Step.TakeBack back => new JObject
        {
            ["take_back"] = new JObject
            {
                ["on"] = Structure(back.On), ["tool"] = Item(back.Tool), ["returns"] = Each(back.Returns, Quantity)
            }
        },
        L.Step.Merge merge => new JObject
        {
            ["merge"] = new JObject
            {
                ["into"] = Structure(merge.Into), ["becomes"] = Piece(merge.Becomes), ["offhand_tool"] = Item(merge.OffhandTool),
                ["extra"] = merge.Extra.TryGet(out L.ItemQuantity extra) ? Quantity(extra) : null
            }
        },
        L.Step.Paint paint => new JObject { ["paint"] = new JObject { ["on"] = Target(paint.On), ["colour"] = paint.Colour.Index } },
        L.Step.Rename rename => new JObject { ["rename"] = new JObject { ["on"] = Target(rename.On), ["to"] = rename.To.Value } },
        L.Step.SetValue set => new JObject { ["set_value"] = new JObject { ["on"] = Target(set.On), ["to"] = set.To.Value } },
        _ => new JObject { ["unknown"] = step.GetType().Name }
    };

    private JToken Target(L.StepTarget target) => target switch
    {
        L.StepTarget.Standing standing => new JObject { ["standing"] = Structure(standing.Structure) },
        L.StepTarget.PlacedBy placed => new JObject { ["placed_by_step"] = placed.Step },
        _ => new JValue(target.GetType().Name)
    };

    internal JObject Swap(L.SwapOption option)
    {
        JObject written = new JObject { ["new"] = Placement(option.New) };
        if (option.Face.TryGet(out L.FaceLoad face))
        {
            written["face"] = new JObject
            {
                ["cell"] = Cell(face.Cell), ["normal"] = Axis(face.Normal), ["rating_kpa"] = face.Rating.Value,
                ["difference_kpa"] = face.Difference.Value, ["over"] = face.Over
            };
        }

        return written;
    }

    internal JObject Story(L.GhostStory story) => new JObject
    {
        ["ghost"] = story.Ghost.Value,
        ["entries"] = Each(story.Entries, Entry),
    };

    internal JObject Story(L.PlanStory story) => new JObject
    {
        ["plan"] = story.Plan.Value,
        ["entries"] = Each(story.Entries, Entry),
        ["branches"] = Each(story.Branches, branch =>
        {
            JArray saves = new JArray();
            foreach (L.SaveName save in branch.Saves)
            {
                saves.Add(save.Value);
            }

            return new JObject { ["follows"] = branch.Follows.Value, ["head"] = branch.Head.Value, ["saves"] = saves };
        }),
    };

    private static JObject Entry(L.HistoryEntry entry) => new JObject
    {
        ["line"] = entry.Line.Value,
        ["follows"] = entry.Follows.TryGet(out L.LineNumber follows) ? follows.Value : null,
        ["command"] = entry.Command.Value.ToString(),
        ["by"] = Actor(entry.By),
        ["at"] = entry.At.ToString("o", CultureInfo.InvariantCulture),
        ["written"] = entry.Written,
    };

    internal JObject StructureInfo(L.StructureInfo info)
    {
        JObject written = Structure(info.Ref);
        written["turn"] = Turn(info.Turn);
        written["state"] = info.State switch
        {
            L.BuildState.Building building => new JObject { ["building"] = building.At.Value },
            L.BuildState.Built => new JValue("built"),
            L.BuildState.Broken => new JValue("broken"),
            _ => new JValue(info.State.GetType().Name)
        };
        if (info.Colour.TryGet(out L.PaintColour colour))
        {
            written["colour"] = colour.Index;
        }

        if (info.Name.TryGet(out L.ThingName name))
        {
            written["name"] = name.Value;
        }

        if (info.Setting.TryGet(out L.Setting setting))
        {
            written["setting"] = setting.Value;
        }

        return written;
    }

    internal JObject PieceEntry(L.PieceEntry piece) => new JObject
    {
        ["prefab_name"] = piece.PrefabName,
        ["hash"] = piece.Key.Hash,
        ["name"] = piece.Name,
        ["grid"] = piece.Grid == L.PieceGrid.Small ? "small" : "large",
        ["kits"] = Each(piece.Kits, kit => new JObject { ["kit"] = Kit(kit.Kit), ["option"] = kit.Option.Index }),
    };

    internal JObject KitEntry(L.KitEntry kit) => new JObject
    {
        ["prefab_name"] = kit.PrefabName,
        ["hash"] = kit.Key.Hash,
        ["name"] = kit.Name,
        ["options"] = Each(kit.Options, piece => Piece(piece)),
    };

    // ---- styles ------------------------------------------------------------------------------------------------

    /// <summary>A resolved style: every property Lattice gives an opinion on, in Style's JSON form.</summary>
    internal static JObject Style(L.Style style)
    {
        JObject written = new JObject();
        if (style.Presence.TryGet(out L.Presence presence)) written["presence"] = presence == L.Presence.Drawn ? "drawn" : "hidden";
        if (style.Fill.TryGet(out L.FillMode fill)) written["fill"] = fill.ToString().ToLowerInvariant();
        if (style.FillPaint.TryGet(out L.Paint fillPaint)) written["fill_paint"] = Paint(fillPaint);
        if (style.FillOpacity.TryGet(out L.Fraction opacity)) written["fill_opacity"] = Round(opacity.Value);
        if (style.Edge.TryGet(out L.EdgeLook edge))
        {
            written["edge"] = edge is L.EdgeLook.Line line
                ? new JObject
                {
                    ["line"] = new JObject
                    {
                        ["style"] = line.Style.ToString().ToLowerInvariant(), ["width"] = Round(line.Width.Value), ["paint"] = Paint(line.Paint)
                    }
                }
                : new JValue("none");
        }

        if (style.Pattern.TryGet(out L.Pattern pattern))
        {
            written["pattern"] = pattern switch
            {
                L.Pattern.Hatch => "hatch", L.Pattern.CrossHatch => "cross_hatch", L.Pattern.Dots => "dots", _ => "none"
            };
        }

        if (style.Glow.TryGet(out L.GlowLook glow))
        {
            written["glow"] = glow is L.GlowLook.Lit lit
                ? new JObject { ["lit"] = new JObject { ["paint"] = Paint(lit.Paint), ["strength"] = Round(lit.Strength.Value) } }
                : new JValue("none");
        }

        if (style.Effect.TryGet(out L.EffectLook effect))
        {
            written["effect"] = effect switch
            {
                L.EffectLook.Pulse pulse => new JObject
                {
                    ["pulse"] = new JObject { ["period"] = Round(pulse.Period.Seconds), ["low"] = Round(pulse.Low.Value) }
                },
                L.EffectLook.Blink blink => new JObject
                {
                    ["blink"] = new JObject { ["period"] = Round(blink.Period.Seconds), ["duty"] = Round(blink.Duty.Value) }
                },
                _ => new JValue("steady")
            };
        }

        if (style.Enter.TryGet(out L.EnterLook enter))
        {
            written["enter"] = enter is L.EnterLook.Flash flash
                ? new JObject { ["flash"] = new JObject { ["count"] = flash.Count.Count, ["period"] = Round(flash.Period.Seconds) } }
                : new JValue("none");
        }

        if (style.Ease.TryGet(out L.Duration ease)) written["ease"] = Round(ease.Seconds);
        if (style.Appear.TryGet(out L.Duration appear)) written["appear"] = Round(appear.Seconds);
        if (style.Vanish.TryGet(out L.Duration vanish)) written["vanish"] = Round(vanish.Seconds);
        if (style.SeeThrough.TryGet(out L.SeeThroughLook seeThrough))
        {
            written["see_through"] = seeThrough is L.SeeThroughLook.Through through
                ? new JObject { ["through"] = new JObject { ["strength"] = Round(through.Strength.Value), ["range"] = Round(through.Range.Value) } }
                : new JValue("none");
        }

        if (style.BeyondClip.TryGet(out L.BeyondClipLook beyond))
        {
            written["beyond_clip"] = beyond switch
            {
                L.BeyondClipLook.Dim dim => new JObject { ["dim"] = Round(dim.By.Value) },
                L.BeyondClipLook.Hidden => new JValue("hidden"),
                L.BeyondClipLook.Wire => new JValue("wire"),
                _ => new JValue("drawn")
            };
        }

        if (style.DistanceFade.TryGet(out L.FadeRange fade)) written["distance_fade"] = new JArray(Round(fade.Start.Value), Round(fade.End.Value));
        if (style.NearLimit.TryGet(out L.Metres near)) written["near_limit"] = Round(near.Value);
        if (style.Dim.TryGet(out L.Fraction dimmed)) written["dim"] = Round(dimmed.Value);
        if (style.Label.TryGet(out L.LabelLook label))
        {
            written["label"] = label is L.LabelLook.Shown shown
                ? new JObject
                {
                    ["shown"] = new JObject
                    {
                        ["says"] = LabelText(shown.Says), ["style"] = shown.Style.Value,
                        ["anchor"] = shown.Anchor.ToString().ToLowerInvariant(), ["priority"] = shown.Priority,
                        ["range"] = Round(shown.Range.Value)
                    }
                }
                : new JValue("none");
        }

        if (style.Icon.TryGet(out L.IconLook icon)) written["icon"] = icon is L.IconLook.Shown shownIcon ? shownIcon.Icon.Value : "none";
        if (style.OpenEnds.TryGet(out L.ShowMode openEnds)) written["open_ends"] = openEnds == L.ShowMode.Shown;
        if (style.Ports.TryGet(out L.ShowMode ports)) written["ports"] = ports == L.ShowMode.Shown;
        if (style.FlowArrows.TryGet(out L.ShowMode flow)) written["flow_arrows"] = flow == L.ShowMode.Shown;
        if (style.WaitsLinks.TryGet(out L.ShowMode waits)) written["waits_links"] = waits == L.ShowMode.Shown;
        if (style.Mark.TryGet(out L.MarkLook mark)) written["mark"] = mark is L.MarkLook.Shown shownMark ? shownMark.Mark.Value : "none";
        if (style.Material.TryGet(out L.MaterialKey material)) written["material"] = material.Value;
        if (style.Pickable.TryGet(out L.Pickable pickable)) written["pickable"] = pickable == L.Pickable.Yes;
        return written;
    }

    internal static JToken Paint(L.Paint paint) => paint switch
    {
        L.Paint.Fixed fixedColour => Rgba(fixedColour.Colour.R, fixedColour.Colour.G, fixedColour.Colour.B, fixedColour.Colour.A),
        L.Paint.Token token => new JValue(token.Key.Value),
        L.Paint.GhostTint tint => new JObject { ["ghost_tint"] = tint.Fallback.Value },
        L.Paint.OwnerColour => new JValue("owner"),
        L.Paint.FinishColour finish => new JObject { ["finish_colour"] = finish.Fallback.Value },
        _ => new JValue(paint.GetType().Name)
    };

    private static JToken LabelText(L.LabelText says) => says switch
    {
        L.LabelText.GhostLabel => new JValue("ghost_label"),
        L.LabelText.FinishName => new JValue("finish_name"),
        L.LabelText.FindingReason => new JValue("finding_reason"),
        L.LabelText.PlanTitle => new JValue("plan_title"),
        L.LabelText.StepNumber => new JValue("step_number"),
        L.LabelText.BillCount => new JValue("bill_count"),
        L.LabelText.Literal literal => new JObject { ["literal"] = literal.Words.Value },
        _ => new JValue(says.GetType().Name)
    };

    /// <summary>A style story: the resolved style, and per property the rule that set it.</summary>
    internal static JObject Story(L.StyleStory story)
    {
        JObject sources = new JObject();
        foreach (KeyValuePair<L.StyleProperty, L.StyleSource> source in story.Sources)
        {
            sources[Snake(source.Key.ToString())] = source.Value switch
            {
                L.StyleSource.Layer layer => $"{layer.Id.Value} rule {layer.Rule} ({layer.Band.ToString().ToLowerInvariant()})",
                L.StyleSource.Library library => $"library rule {library.Rule}",
                L.StyleSource.Default => "default",
                _ => source.Value.ToString()
            };
        }

        return new JObject { ["style"] = Style(story.Resolved), ["sources"] = sources };
    }

    internal static string Snake(string pascal)
    {
        System.Text.StringBuilder text = new System.Text.StringBuilder(pascal.Length + 4);
        for (int index = 0; index < pascal.Length; index++)
        {
            char letter = pascal[index];
            if (char.IsUpper(letter) && index > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(letter));
        }

        return text.ToString();
    }

    internal static JArray Each<T>(IEnumerable<T> values, Func<T, JToken> write)
    {
        JArray written = new JArray();
        foreach (T value in values)
        {
            written.Add(write(value));
        }

        return written;
    }
}
