#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Genetics;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.UI;
using HarmonyLib;
using Objects.Electrical;
using Util.Commands;

namespace StationGodMCP;

/// <summary>
/// A game member the mod reaches by reflection, or a Harmony target, that this build of the game no longer has. API
/// methods turn it into the error code game_changed, naming the member.
/// </summary>
internal sealed class GameChangedException : Exception
{
    internal GameChangedException(string member)
        : base(
            $"The game has changed: {member} is gone, so this StationGod MCP build cannot do this. It needs an update " +
                $"for this game version.")
    {
        Member = member;
    }

    internal string Member { get; }
}

/// <summary>
/// One reflected member, resolved once on first use. A required member that cannot be found stays missing and every
/// use throws GameChangedException; an optional one (another mod's API) is looked for again until it is found.
/// </summary>
internal abstract class GameMember
{
    private readonly object _gate = new object();
    private int _state;

    protected GameMember(string name, bool optional)
    {
        Name = name;
        Optional = optional;
    }

    internal string Name { get; }

    internal bool Optional { get; }

    internal bool TryResolve()
    {
        lock (_gate)
        {
            if (_state == 1 || (_state == 2 && !Optional))
            {
                return _state == 1;
            }

            bool found;
            try
            {
                found = ResolveCore();
            }
            catch (Exception)
            {
                // The resolver's reflection call (Type.GetField, AccessTools.Method...) on a type that fails to load.
                found = false;
            }

            _state = found ? 1 : 2;
            return found;
        }
    }

    protected void Require()
    {
        if (!TryResolve())
        {
            throw new GameChangedException(Name);
        }
    }

    protected abstract bool ResolveCore();
}

internal sealed class GameField : GameMember
{
    private readonly Func<FieldInfo?> _resolve;
    private FieldInfo? _field;

    internal GameField(string name, Func<FieldInfo?> resolve, bool optional = false) : base(name, optional)
    {
        _resolve = resolve;
    }

    internal FieldInfo Info
    {
        get
        {
            Require();
            return _field!;
        }
    }

    internal object GetValue(object? target) => Info.GetValue(target);

    protected override bool ResolveCore()
    {
        _field = _resolve();
        return _field != null;
    }
}

internal sealed class GameMethod : GameMember
{
    private readonly Func<MethodInfo?> _resolve;
    private MethodInfo? _method;

    internal GameMethod(string name, Func<MethodInfo?> resolve, bool optional = false) : base(name, optional)
    {
        _resolve = resolve;
    }

    internal MethodInfo Info
    {
        get
        {
            Require();
            return _method!;
        }
    }

    internal object Invoke(object? target, params object?[] arguments) => Info.Invoke(target, arguments);

    protected override bool ResolveCore()
    {
        _method = _resolve();
        return _method != null;
    }
}

internal sealed class GameType : GameMember
{
    private readonly Func<Type?> _resolve;
    private Type? _type;

    internal GameType(string name, Func<Type?> resolve, bool optional = false) : base(name, optional)
    {
        _resolve = resolve;
    }

    internal Type Info
    {
        get
        {
            Require();
            return _type!;
        }
    }

    /// <summary>The type, or null when missing (for optional types, whose absence is not a game change).</summary>
    internal Type? OrNull => TryResolve() ? _type : null;

    protected override bool ResolveCore()
    {
        _type = _resolve();
        return _type != null;
    }
}

/// <summary>
/// Every game member the mod reaches by reflection, and every Harmony target, in one place. Nothing here resolves at
/// type initialisation: each entry is looked up on first use, so a renamed member costs the methods that need it
/// (error game_changed) and nothing else. CheckAll resolves them all once at load and logs each missing one once.
/// </summary>
internal static class GameMembers
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags PublicStatic = BindingFlags.Static | BindingFlags.Public;

    private static readonly List<GameMember> All = new List<GameMember>();

    // ---- The T-Ray SPU's see-through material (XRay: highlight, show_preview xray) ----
    internal static readonly GameField MesonScannerMaterial = Field(typeof(SPUMesonScanner), "_material");

    // ---- ProgrammableChip runtime state (IcRuntimeInspector) ----
    internal static readonly GameField ChipRegisters = Field(typeof(ProgrammableChip), "_Registers");
    internal static readonly GameField ChipStack = Field(typeof(ProgrammableChip), "_Stack");
    internal static readonly GameField ChipStackPointerIndex = Field(typeof(ProgrammableChip), "_StackPointerIndex");
    internal static readonly GameField ChipReturnAddressIndex = Field(typeof(ProgrammableChip), "_ReturnAddressIndex");
    internal static readonly GameField ChipNextAddress = Field(typeof(ProgrammableChip), "_NextAddr");
    internal static readonly GameField ChipCompileErrorLine =
        Field(typeof(ProgrammableChip), "_compileErrorLineNumber");
    internal static readonly GameField ChipCompileErrorType = Field(typeof(ProgrammableChip), "_compileErrorType");
    internal static readonly GameField ChipAliases = Field(typeof(ProgrammableChip), "_Aliases");
    internal static readonly GameField ChipDefines = Field(typeof(ProgrammableChip), "_Defines");
    internal static readonly GameField ChipJumpTags = Field(typeof(ProgrammableChip), "_JumpTags");
    internal static readonly GameField ChipLinesOfCode = Field(typeof(ProgrammableChip), "_LinesOfCode");
    internal static readonly GameType ChipAliasValue = Nested(typeof(ProgrammableChip), "_AliasValue");
    internal static readonly GameField ChipAliasTarget =
        NestedField(ChipAliasValue, "ProgrammableChip._AliasValue.Target", "Target");
    internal static readonly GameField ChipAliasIndex =
        NestedField(ChipAliasValue, "ProgrammableChip._AliasValue.Index", "Index");
    internal static readonly GameType ChipLineOfCodeType = Nested(typeof(ProgrammableChip), "_LineOfCode");
    internal static readonly GameField ChipLineOfCodeText =
        NestedField(ChipLineOfCodeType, "ProgrammableChip._LineOfCode.LineOfCode", "LineOfCode");

    // ---- IC Housing ----
    internal static readonly GameMethod HousingIsOperable = Register(new GameMethod(
        "CircuitHousing.IsOperable (getter)", () => AccessTools.PropertyGetter(typeof(CircuitHousing), "IsOperable")));
    internal static readonly GameField HousingDeviceLabels = Field(typeof(CircuitHousing), "_DeviceLabels");

    // ---- World and devices ----
    internal static readonly GameField DeviceDensePool = Field(typeof(Device), "_deviceDensePool");
    internal static readonly GameField SolarPanelArms = Field(typeof(SolarPanel), "_panelArms");
    internal static readonly GameField DishMinWattage = Field(typeof(SatelliteDish), "minWattage");
    internal static readonly GameField DishMaxWattage = Field(typeof(SatelliteDish), "maxWattage");
    internal static readonly GameField DishFieldOfView = Field(typeof(SatelliteDish), "dishFov");
    internal static readonly GameField SmallDishHorizontalPivot = Field(typeof(SmallSatelliteDish), "_horizontalPivot");

    // ---- Structures (replace_walls: a prefab's face points before it is placed, as OnAssignedReference reads them) ----
    internal static readonly GameField StructureBlockingGrids = Field(typeof(Structure), "blockingGrids");

    // ---- Rockets: a rocket's small cells and their types (RocketNetwork.AddSmallCellOwnership), and the partner each
    // umbilical holds (set by RocketUmbilicalHelper.FindAndSetOtherUmbilical; CrewModule's is a public field) ----
    internal static readonly GameField RocketSmallCells =
        Field(typeof(Networks.RocketNetwork), "_smallGridsOccupied");
    internal static readonly GameField GasUmbilicalMalePartner =
        Field(typeof(global::Objects.Rockets.RocketGasUmbilicalMale), "_partnerUmbilical");
    internal static readonly GameField GasUmbilicalFemalePartner =
        Field(typeof(global::Objects.Rockets.RocketGasUmbilicalFemale), "_partnerUmbilical");
    internal static readonly GameField PowerUmbilicalPartner =
        Field(typeof(global::Objects.Rockets.RocketPowerUmbilical), "_partnerUmbilical");
    internal static readonly GameField ChuteUmbilicalMalePartner =
        Field(typeof(global::Objects.Rockets.RocketChuteUmbilicalMale), "_partnerUmbilical");
    internal static readonly GameField ChuteUmbilicalFemalePartner =
        Field(typeof(global::Objects.Rockets.RocketChuteUmbilicalFemale), "_partnerUmbilical");
    internal static readonly GameField CrewUmbilicalPartner =
        Field(typeof(global::Objects.Rockets.RocketCrewUmbilical), "_partnerUmbilical");

    // ---- Rocket flight tools: the pipe network an engine draws from (RocketEngineBase.CheckConnections), the landing
    // autopilot's own thrust peak (Rocket.HandleAutomatedLanding), a miner's drill head (its power multiplier) ----
    internal static readonly GameField EngineInputNetwork1 = Field(typeof(RocketEngineBase), "_inputNetwork1");
    internal static readonly GameField RocketHighestRecordedThrust =
        Field(typeof(global::Objects.Rockets.Rocket), "_highestRecordedThrust");
    internal static readonly GameField RocketMinerHead = Field(typeof(RocketMiner), "_miningHead");

    // ---- remove_structure: the game's own refusal to deconstruct (Cable, Pipe, Silo, LaunchMount, fuselage) ----
    internal static readonly GameMethod StructureCanDeconstruct = Register(new GameMethod("Structure.CanDeconstruct",
        () => typeof(Structure).GetMethod("CanDeconstruct", PrivateInstance, null, Type.EmptyTypes, null)));

    // remove_structure: the game's air and room checks when a structure's build state changes (FreedCells).
    internal static readonly GameMethod StructureWorldChangeChecks = Register(new GameMethod(
        "Structure.WorldChangeChecks",
        () => typeof(Structure).GetMethod("WorldChangeChecks", PrivateInstance, null, Type.EmptyTypes, null)));

    // ---- Plants ----
    internal static readonly GameField PlantStageTime = Field(typeof(Plant), "_stageTime");
    internal static readonly GameField PlantPerennial = Field(typeof(Plant), "_isPerennial");
    internal static readonly GameField PlantFertilizerBoost = Field(typeof(Plant), "_fertilizerBoost");
    internal static readonly GameField PlantAggregateStates = Field(typeof(PlantStatus), "_aggregateStates");
    internal static readonly GameField PlantResilience =
        Field(typeof(PlantLifeRequirements), "PlantResiliance", PrivateStatic);
    internal static readonly GameField PlantStatInvert = Field(typeof(PlantStat), "_invert");

    // ---- Survival ----
    internal static readonly GameField HumanHydrationLossPerTick =
        Field(typeof(Human), "_hydrationLossPerTick", PrivateStatic);
    internal static readonly GameField HumanThirstScaleRate = Field(typeof(Human), "scaleRate", PrivateStatic);

    // ---- Planet ----
    internal static readonly GameField PlanetLiquidClouds =
        Field(typeof(PlanetaryAtmosphereSimulation), "_liquidClouds", PrivateStatic);
    internal static readonly GameField PlanetIceClouds =
        Field(typeof(PlanetaryAtmosphereSimulation), "_iceClouds", PrivateStatic);
    internal static readonly GameField PlanetIceCaps =
        Field(typeof(PlanetaryAtmosphereSimulation), "_iceCaps", PrivateStatic);
    internal static readonly GameField PlanetGlobalInteraction =
        Field(typeof(PlanetaryAtmosphereSimulation), "GlobalInteraction", PrivateStatic);

    // ---- Terraforming Reloaded: another mod, optional; its absence is not a game change ----
    internal static readonly GameType TerraformingGate = Optional("TerraformingReloaded.Patching.Gate");
    internal static readonly GameType TerraformingClimate = Optional("TerraformingReloaded.Patching.Climate");
    internal static readonly GameType TerraformingPlugin = Optional("TerraformingReloaded.Plugin");
    internal static readonly GameType TerraformingRockets = Optional("TerraformingReloaded.Patching.Rockets");
    internal static readonly GameMethod TerraformingCombustionRate = Register(new GameMethod(
        "TerraformingReloaded.Patching.Rockets.CombustionRate",
        () => TerraformingRockets.OrNull?.GetMethod("CombustionRate", AnyStatic, null, Type.EmptyTypes, null),
        optional: true));
    internal static readonly GameMethod TerraformingDescribe = Register(new GameMethod(
        "TerraformingReloaded.Patching.Gate.Describe",
        () => TerraformingGate.OrNull?.GetMethod("Describe", PublicStatic),
        optional: true));
    internal static readonly GameMethod TerraformingLastAdjustment = Register(new GameMethod(
        "TerraformingReloaded.Patching.Climate.LastAdjustment (getter)",
        () => TerraformingClimate.OrNull?.GetProperty("LastAdjustment", PublicStatic)?.GetGetMethod(),
        optional: true));
    internal static readonly GameField TerraformingVersion = Register(new GameField(
        "TerraformingReloaded.Plugin.PluginVersion",
        () => TerraformingPlugin.OrNull?.GetField("PluginVersion", PublicStatic),
        optional: true));

    // ---- StationeersLua (ScriptedScreens' Lua chips): another mod, optional; its absence is not a game change ----
    internal static readonly GameType LuaChipIds = Optional("StationeersLua.LuaChipIds");
    internal static readonly GameType LuaRuntimeManager = Optional("StationeersLua.LuaChipRuntimeManager");
    internal static readonly GameMethod LuaIsLuaChip = LuaMethod(LuaChipIds, "LuaChipIds", "IsLuaProgrammableChip");
    internal static readonly GameMethod LuaIsCompiling = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "IsCompilationInProgress");
    internal static readonly GameMethod LuaIsInitComplete = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "IsChipInitComplete");
    internal static readonly GameMethod LuaSourceVersion = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "GetSourceVersion");
    internal static readonly GameMethod LuaLogText = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "GetChipLogText");
    internal static readonly GameMethod LuaClearFailedSource = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "ClearFailedSourceHash");
    internal static readonly GameMethod LuaDebugSnapshot = LuaMethod(LuaRuntimeManager, "LuaChipRuntimeManager",
        "TryGetDebugSnapshot");
    internal static readonly GameType LuaSnapshotType = Register(new GameType(
        "StationeersLua.LuaChipRuntimeManager.LuaChipDebugSnapshot",
        () => LuaRuntimeManager.OrNull?.GetNestedType("LuaChipDebugSnapshot",
            BindingFlags.NonPublic | BindingFlags.Public),
        optional: true));
    internal static readonly GameField LuaSnapshotHasRuntime = LuaSnapshotField("HasRuntime");
    internal static readonly GameField LuaSnapshotIsLibrary = LuaSnapshotField("IsLibraryChip");
    internal static readonly GameField LuaSnapshotErrorKind = LuaSnapshotField("LastErrorKind");
    internal static readonly GameField LuaSnapshotErrorLine = LuaSnapshotField("LastErrorLine");
    internal static readonly GameField LuaSnapshotErrorMessage = LuaSnapshotField("LastErrorMessage");
    internal static readonly GameField LuaSnapshotErrorTraceback = LuaSnapshotField("LastErrorTraceback");

    // ---- IngotVault (vault_* tools): another mod, optional; its absence is not a game change ----
    internal static readonly GameType VaultType = Optional("IngotVault.StructureIngotVault");
    internal static readonly GameType RemoteVaultType = Optional("IngotVault.StructureRemoteVault");
    internal static readonly GameField VaultAll = VaultField(VaultType, "AllVaults", PublicStatic);
    internal static readonly GameField VaultSupportedReagents =
        VaultField(VaultType, "SupportedReagents", PublicStatic);
    internal static readonly GameField VaultOres =
        VaultField(VaultType, "_storedOresAndIces", BindingFlags.Instance | BindingFlags.Public);
    internal static readonly GameField VaultPendingVends =
        VaultField(VaultType, "PendingVends", BindingFlags.Instance | BindingFlags.Public);
    internal static readonly GameMethod VaultShowContents = VaultMethod(VaultType, "PreviousContentsShow",
        BindingFlags.Instance | BindingFlags.Public);
    internal static readonly GameMethod VaultIngotHashOf = VaultMethod(VaultType, "GetPrefabHashForReagent",
        PublicStatic);
    internal static readonly GameField RemoteVaultAll = VaultField(RemoteVaultType, "AllRemoteVaults", PublicStatic);
    internal static readonly GameMethod RemoteVaultConnected = VaultMethod(RemoteVaultType, "FindConnectedVault",
        BindingFlags.Instance | BindingFlags.Public);
    internal static readonly GameMethod RemoteVaultConnectionError = VaultMethod(RemoteVaultType,
        "GetConnectionError", BindingFlags.Instance | BindingFlags.Public);

    // ---- BlueprintMod (paste_blueprint): another mod, optional; its absence is not a game change ----
    internal static readonly GameType BlueprintCommands = Optional("BlueprintMod.BlueprintCommands");
    internal static readonly GameType BlueprintSerializer = Optional("BlueprintMod.BlueprintSerializer");
    internal static readonly GameType BlueprintModPlugin = Optional("BlueprintMod.BlueprintMod");
    internal static readonly GameType BlueprintData = Optional("BlueprintMod.BlueprintData");
    internal static readonly GameType BlueprintPasteOperation = Register(new GameType(
        "BlueprintMod.BlueprintCommands.StaggeredPasteOperation",
        () => BlueprintCommands.OrNull?.GetNestedType("StaggeredPasteOperation", BindingFlags.Public),
        optional: true));
    internal static readonly GameMethod BlueprintStartPaste = Register(new GameMethod(
        "BlueprintMod.BlueprintCommands.StartStaggeredPaste(BlueprintData, Vector3, float, float)",
        () => BlueprintData.OrNull == null
            ? null
            : BlueprintCommands.OrNull?.GetMethod("StartStaggeredPaste", PublicStatic, null,
                new[] { BlueprintData.OrNull, typeof(UnityEngine.Vector3), typeof(float), typeof(float) }, null),
        optional: true));
    internal static readonly GameMethod BlueprintUndo = Register(new GameMethod(
        "BlueprintMod.BlueprintCommands.Undo(string[])",
        () => BlueprintCommands.OrNull?.GetMethod("Undo", PublicStatic, null, new[] { typeof(string[]) }, null),
        optional: true));
    internal static readonly GameMethod BlueprintActivePaste = Register(new GameMethod(
        "BlueprintMod.BlueprintCommands.ActivePaste (getter)",
        () => BlueprintCommands.OrNull?.GetProperty("ActivePaste", PublicStatic)?.GetGetMethod(),
        optional: true));
    internal static readonly GameMethod BlueprintLoad = Register(new GameMethod(
        "BlueprintMod.BlueprintSerializer.Load(string)",
        () => BlueprintSerializer.OrNull?.GetMethod("Load", PublicStatic, null, new[] { typeof(string) }, null),
        optional: true));
    internal static readonly GameField BlueprintDirectory = Register(new GameField(
        "BlueprintMod.BlueprintMod.BlueprintDirectory",
        () => BlueprintModPlugin.OrNull?.GetField("BlueprintDirectory", PublicStatic),
        optional: true));
    internal static readonly GameField BlueprintCopyYAngle =
        BlueprintField(BlueprintData, "BlueprintData", "CopyYAngle");
    internal static readonly GameField BlueprintEntries = BlueprintField(BlueprintData, "BlueprintData", "Entries");
    internal static readonly GameField PasteThings = PasteField("PastedThings");
    internal static readonly GameField PasteCreated = PasteField("Created");
    internal static readonly GameField PasteFailed = PasteField("Failed");
    internal static readonly GameField PasteSkipped = PasteField("Skipped");
    internal static readonly GameField PasteFingerprint = PasteField("Fingerprint");
    internal static readonly GameField PasteCancelled = PasteField("Cancelled");
    internal static readonly GameField PasteComplete = PasteField("Complete");

    // ---- Trading ----
    internal static readonly GameMethod TradeSellItemQuantity = Register(new GameMethod(
        "TradeDataHelper.GetSellItemQuantity",
        () =>
            typeof(TraderUI.TradeDataHelper).GetMethod(
                "GetSellItemQuantity",
                BindingFlags.NonPublic | BindingFlags.Static)));

    // ---- Saving (upgrade_cables and upgrade_pipes hold the game tick, as a save does) ----
    internal static readonly GameMethod SaveIsSaving = Register(new GameMethod(
        "SaveHelper.IsSaving (getter)",
        () => AccessTools.PropertyGetter(typeof(Assets.Scripts.Serialization.SaveHelper), "IsSaving")));

    // ---- Console ----
    internal static readonly GameMethod ConsoleColorOf = Register(new GameMethod(
        "ConsoleWindow.GetConsoleColor(uint)", () =>
            AccessTools.Method(typeof(ConsoleWindow), "GetConsoleColor", new[] { typeof(uint) })));

    // ---- Construction cursor and localisation (used while loading, not by API methods) ----
    internal static readonly GameField ConstructionCursors =
        Field(typeof(InventoryManager), "_constructionCursors", AnyStatic);
    internal static readonly GameField ConstructionCursorParent =
        Field(typeof(InventoryManager), "_constructionCursorParent");
    internal static readonly GameMethod HandleStructurePrefab = Register(new GameMethod(
        "InventoryManager.HandleStructurePrefab", () =>
            typeof(InventoryManager).GetMethod(
                "HandleStructurePrefab",
                PrivateInstance | BindingFlags.Public | BindingFlags.Static)));
    internal static readonly GameField ThingLocalized = Field(typeof(Localization), "ThingLocalized", AnyStatic);
    internal static readonly GameField FallbackThingsLocalized =
        Field(typeof(Localization), "FallbackThingsLocalized", AnyStatic);

    // ---- Harmony targets ----
    internal static readonly GameMethod PatchConsoleLineSet =
        Target("ConsoleLine.Set", () => AccessTools.Method(typeof(ConsoleLine), nameof(ConsoleLine.Set)));
    internal static readonly GameMethod PatchConsoleLineSetSegments =
        Target(
            "ConsoleLine.SetSegments",
            () => AccessTools.Method(typeof(ConsoleLine), nameof(ConsoleLine.SetSegments)));
    internal static readonly GameMethod PatchSetupConstructionCursors =
        Target("InventoryManager.SetupConstructionCursors", () => AccessTools.Method(
            typeof(InventoryManager),
            "SetupConstructionCursors"));
    internal static readonly GameMethod PatchUpdatePlacementStructure =
        Target("InventoryManager.UpdatePlacement(Structure)", () => AccessTools.Method(
            typeof(InventoryManager),
            "UpdatePlacement",
            new[] { typeof(Structure) }));
    internal static readonly GameMethod PatchUpdatePlacementConstructor =
        Target("InventoryManager.UpdatePlacement(Constructor)", () => AccessTools.Method(
            typeof(InventoryManager),
            "UpdatePlacement",
            new[] { typeof(Constructor) }));
    internal static readonly GameMethod PatchCircuitHousingExecute =
        Target(
            "CircuitHousing.Execute",
            () => AccessTools.Method(typeof(CircuitHousing), nameof(CircuitHousing.Execute)));
    internal static readonly GameMethod PatchSuitBaseExecute =
        Target("SuitBase.Execute", () => AccessTools.Method(typeof(SuitBase), nameof(SuitBase.Execute)));
    internal static readonly GameMethod PatchAdvancedSuitExecute =
        Target("AdvancedSuit.Execute", () => AccessTools.Method(typeof(AdvancedSuit), nameof(AdvancedSuit.Execute)));
    internal static readonly GameMethod PatchItemManufactured =
        Target("DynamicThing.ItemManufactured",
            () => AccessTools.Method(typeof(DynamicThing), nameof(DynamicThing.ItemManufactured)));
    internal static readonly GameMethod PatchOnSplitStack =
        Target("Stackable.OnSplitStack",
            () => AccessTools.Method(typeof(Assets.Scripts.Objects.Items.Stackable), "OnSplitStack"));
    internal static readonly GameMethod PatchHandleMainThreadEvents =
        Target("AtmosphericsController.HandleMainThreadEvents",
            () => AccessTools.Method(typeof(AtmosphericsController),
                nameof(AtmosphericsController.HandleMainThreadEvents)));

    private static GameField Field(Type type, string name, BindingFlags flags = PrivateInstance) =>
        Register(new GameField($"{type.Name}.{name}", () => type.GetField(name, flags)));

    private static GameType Nested(Type type, string name) =>
        Register(new GameType(
            $"{type.Name}.{name}",
            () => type.GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public)));

    private static GameField NestedField(GameType owner, string label, string name) =>
        Register(new GameField(label,
            () => owner.OrNull?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)));

    private static GameType Optional(string fullName) =>
        Register(new GameType(fullName, () => FindType(fullName), optional: true));

    // A static method of one of StationeersLua's internal classes, public or not; none of them is overloaded.
    private static GameMethod LuaMethod(GameType owner, string ownerName, string name) =>
        Register(new GameMethod($"StationeersLua.{ownerName}.{name}",
            () => owner.OrNull?.GetMethod(name, AnyStatic), optional: true));

    private static GameField LuaSnapshotField(string name) =>
        Register(new GameField($"StationeersLua.LuaChipRuntimeManager.LuaChipDebugSnapshot.{name}",
            () => LuaSnapshotType.OrNull?.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            optional: true));

    private static GameField BlueprintField(GameType owner, string ownerName, string name) =>
        Register(new GameField($"BlueprintMod.{ownerName}.{name}",
            () => owner.OrNull?.GetField(name, BindingFlags.Instance | BindingFlags.Public), optional: true));

    // A member of one of IngotVault's types; none of the methods named is overloaded.
    private static GameField VaultField(GameType owner, string name, BindingFlags flags) =>
        Register(new GameField($"{owner.Name}.{name}", () => owner.OrNull?.GetField(name, flags), optional: true));

    private static GameMethod VaultMethod(GameType owner, string name, BindingFlags flags) =>
        Register(new GameMethod($"{owner.Name}.{name}", () => owner.OrNull?.GetMethod(name, flags), optional: true));

    private static GameField PasteField(string name) =>
        BlueprintField(BlueprintPasteOperation, "BlueprintCommands.StaggeredPasteOperation", name);

    private static GameMethod Target(string name, Func<MethodInfo?> resolve) =>
        Register(new GameMethod("Harmony target " + name, resolve));

    private static T Register<T>(T member) where T : GameMember
    {
        lock (All)
        {
            All.Add(member);
        }

        return member;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static List<GameMember> Snapshot()
    {
        lock (All)
        {
            return new List<GameMember>(All);
        }
    }

    /// <summary>Resolve every member once at load and log each required one that is missing; nothing else.</summary>
    internal static void CheckAll()
    {
        foreach (GameMember member in Snapshot())
        {
            if (!member.TryResolve() && !member.Optional)
            {
                StationGodMod.LogWarning(
                    $"Game member missing: {member.Name}. Methods that need it answer game_changed.");
            }
        }
    }

    /// <summary>For mod_info: every member and whether it resolved, and how many required ones are missing.</summary>
    internal static ReflectionReport Report()
    {
        List<Api.Views.ReflectionMemberView> members = new List<Api.Views.ReflectionMemberView>();
        int missing = 0;
        foreach (GameMember member in Snapshot())
        {
            bool resolved = member.TryResolve();
            if (!resolved && !member.Optional)
            {
                missing++;
            }

            members.Add(new Api.Views.ReflectionMemberView(member.Name, resolved, member.Optional));
        }

        return new ReflectionReport(members, missing);
    }
}

internal sealed class ReflectionReport
{
    internal ReflectionReport(List<Api.Views.ReflectionMemberView> members, int missing)
    {
        Members = members;
        Missing = missing;
    }

    internal List<Api.Views.ReflectionMemberView> Members { get; }

    internal int Missing { get; }
}
