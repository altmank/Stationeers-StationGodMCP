#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI.ImGuiUi;
using Reagents;
using StationeersMods.Interface;
using UnityEngine;

namespace StationGodMCP;

/// <summary>
/// Registers the StationGod Gateway: a structure and its kit cloned from the game's Logic Memory
/// (StructureLogicMemory, ItemKitLogicMemory) with the LogicMemory component swapped for StationGodGateway, its name
/// and description, the kit in the inventory lists and the creative menu, and an Electronics Printer recipe.
/// </summary>
internal static class PrefabRegistrar
{
    internal const string StructurePrefabName = "StructureStationGodGateway";
    internal const string KitPrefabName = "ItemKitStationGodGateway";

    private const string SourceStructurePrefabName = "StructureLogicMemory";
    private const string SourceKitPrefabName = "ItemKitLogicMemory";
    private const string PrinterPrefabName = "StructureElectronicsPrinter";

    // The kit's Electronics Printer recipe: seconds, joules and grams of copper and gold.
    private const float RecipeSeconds = 10f;
    private const float RecipeEnergy = 1000f;
    private const double RecipeCopper = 1.0;
    private const double RecipeGold = 1.0;

    private static readonly object RegistrationLock = new object();
    private static bool _registered;
    private static GameObject? _templateRoot;

    internal static void TryRegister(ModBehaviour owner)
    {
        lock (RegistrationLock)
        {
            if (_registered || Prefab.Find(StructurePrefabName) != null)
            {
                _registered = true;
                return;
            }

            LogicMemory? sourceStructure = Prefab.Find(SourceStructurePrefabName) as LogicMemory;
            MultiConstructor? sourceKit = Prefab.Find(SourceKitPrefabName) as MultiConstructor;
            if (sourceStructure == null || sourceKit == null || sourceStructure.BuildStates == null ||
                sourceStructure.BuildStates.Count == 0)
            {
                return;
            }

            try
            {
                Register(owner, sourceStructure, sourceKit);
                _registered = true;
                StationGodMod.Log($"Registered {KitPrefabName} and {StructurePrefabName}.");
            }
            catch (Exception exception)
            {
                // Object.Instantiate, Prefab.RegisterExisting and the recipe calls: a failed registration is logged
                // and the mod runs without the gateway; the API does not need it.
                StationGodMod.LogError("Prefab registration failed", exception);
            }
        }
    }

    private static void Register(ModBehaviour owner, LogicMemory sourceStructure, MultiConstructor sourceKit)
    {
        _templateRoot = new GameObject("StationGodTemplates");
        _templateRoot.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(_templateRoot);

        StationGodGateway structure = CloneStructure(owner, sourceStructure);
        MultiConstructor kit = CloneKit(owner, sourceKit, structure);
        ReplaceConstructionKit(structure, sourceKit, kit);

        Prefab.RegisterExisting(structure);
        Prefab.RegisterExisting(kit);
        RegisterLocalization();
        RegisterInventoryAndCreativeEntries(kit);
        RegisterElectronicsPrinterRecipe(kit);
    }

    private static StationGodGateway CloneStructure(ModBehaviour owner, LogicMemory source)
    {
        bool wasActive = source.gameObject.activeSelf;
        source.gameObject.SetActive(false);
        GameObject clone;
        try
        {
            clone = owner.Instantiate(source.gameObject);
        }
        finally
        {
            source.gameObject.SetActive(wasActive);
        }

        if (clone == null)
        {
            throw new InvalidOperationException("StationGodMCP could not clone the Logic Memory prefab.");
        }

        clone.SetActive(false);
        clone.name = StructurePrefabName;
        clone.transform.SetParent(_templateRoot!.transform, false);

        LogicMemory? oldComponent = clone.GetComponent<LogicMemory>();
        StationGodGateway? newComponent = owner.AddComponent<StationGodGateway>(clone);
        if (oldComponent == null || newComponent == null)
        {
            throw new InvalidOperationException("Could not replace the cloned LogicMemory component.");
        }

        ReflectionClone.CopyMatchingFields(oldComponent, newComponent);
        ReflectionClone.ReplaceComponentReferences(clone, oldComponent, newComponent);
        UnityEngine.Object.DestroyImmediate(oldComponent);

        newComponent.GridBounds = GridBoundsFor(source, newComponent);
        newComponent.PrefabName = StructurePrefabName;
        newComponent.PrefabHash = Animator.StringToHash(StructurePrefabName);
        clone.tag = "NotSpawnable";
        clone.SetActive(true);
        return newComponent;
    }

    /// <summary>
    /// The grid bounds the gateway registers with. GridBounds is not serialized, so Instantiate leaves the clone an
    /// empty one (IsValid false), and the game fills a prefab's only in OnPrefabLoad, which ran before the clone
    /// existed. The gateway has the Logic Memory's body, so it takes the source's bounds; should those not be cached
    /// yet, they are worked out from the copied Bounds as Structure.CachePrefabBounds does.
    /// </summary>
    private static GridBounds GridBoundsFor(LogicMemory source, StationGodGateway gateway) =>
        source.GridBounds != null && source.GridBounds.IsValid() ? source.GridBounds : new GridBounds(gateway);

    private static MultiConstructor CloneKit(ModBehaviour owner, MultiConstructor source, StationGodGateway structure)
    {
        bool wasActive = source.gameObject.activeSelf;
        source.gameObject.SetActive(false);
        GameObject clone;
        try
        {
            clone = owner.Instantiate(source.gameObject);
        }
        finally
        {
            source.gameObject.SetActive(wasActive);
        }

        if (clone == null)
        {
            throw new InvalidOperationException("StationGodMCP could not clone the Logic Memory kit prefab.");
        }

        clone.SetActive(false);
        clone.name = KitPrefabName;
        clone.transform.SetParent(_templateRoot!.transform, false);

        MultiConstructor? kit = clone.GetComponent<MultiConstructor>();
        if (kit == null)
        {
            throw new InvalidOperationException("The cloned Logic Memory kit has no MultiConstructor component.");
        }

        kit.PrefabName = KitPrefabName;
        kit.PrefabHash = Animator.StringToHash(KitPrefabName);
        kit.Constructables = new List<Structure> { structure };
        kit.LastSelectedIndex = 0;
        clone.tag = "Untagged";

        Thing.Deregister(kit);
        clone.SetActive(true);
        return kit;
    }

    private static void ReplaceConstructionKit(Structure structure, MultiConstructor sourceKit,
        MultiConstructor customKit)
    {
        foreach (BuildState buildState in structure.BuildStates)
        {
            if (buildState == null || buildState.Tool == null)
            {
                continue;
            }

            ToolUse clonedTool = new ToolUse();
            ReflectionClone.CopyAllFields(buildState.Tool, clonedTool);

            if (IsSourceKit(clonedTool.ToolEntry, sourceKit))
            {
                clonedTool.ToolEntry = customKit;
                clonedTool.EntryQuantity = 1;
            }

            if (IsSourceKit(clonedTool.ToolEntry2, sourceKit))
            {
                clonedTool.ToolEntry2 = customKit;
                clonedTool.EntryQuantity2 = 1;
            }

            if (IsSourceKit(clonedTool.ToolExit, sourceKit))
            {
                clonedTool.ToolExit = customKit;
                clonedTool.ExitQuantity = 1;
            }

            buildState.Tool = clonedTool;
        }
    }

    private static bool IsSourceKit(Thing? candidate, MultiConstructor sourceKit)
    {
        return candidate != null &&
               (ReferenceEquals(candidate, sourceKit) || candidate.PrefabName == sourceKit.PrefabName);
    }

    private static void RegisterLocalization()
    {
        Localization.LocalizationThingDat kitLocalization = new Localization.LocalizationThingDat
        {
            PrefabName = "Kit (StationGod Gateway)",
            Description = "Contains the components needed to construct a StationGod Gateway."
        };
        Localization.LocalizationThingDat structureLocalization = new Localization.LocalizationThingDat
        {
            PrefabName = "StationGod Gateway",
            Description = "Grants the local StationGod MCP server access to devices on its connected data networks."
        };

        AddLocalizationEntry("ThingLocalized", Animator.StringToHash(KitPrefabName), kitLocalization);
        AddLocalizationEntry("ThingLocalized", Animator.StringToHash(StructurePrefabName), structureLocalization);
        AddLocalizationEntry("FallbackThingsLocalized", Animator.StringToHash(KitPrefabName), kitLocalization);
        AddLocalizationEntry(
            "FallbackThingsLocalized",
            Animator.StringToHash(StructurePrefabName),
            structureLocalization);
    }

    private static void AddLocalizationEntry(string fieldName, int hash, Localization.LocalizationThingDat localization)
    {
        GameField field =
            fieldName == "ThingLocalized" ? GameMembers.ThingLocalized : GameMembers.FallbackThingsLocalized;

        // A missing field was logged once at load (GameMembers.CheckAll); the name is then left to the game's fallback.
        if (field.TryResolve() && field.GetValue(null) is IDictionary dictionary)
        {
            dictionary[hash] = localization;
        }
    }

    private static void RegisterInventoryAndCreativeEntries(MultiConstructor kit)
    {
        if (InventoryManager.DynamicThingPrefabs != null &&
            !InventoryManager.DynamicThingPrefabs.Contains(KitPrefabName))
        {
            InventoryManager.DynamicThingPrefabs.Add(KitPrefabName);
        }

        if (!GameManager.IsBatchMode)
        {
            ImguiCreativeSpawnMenu.AddDynamicItem(kit);
        }

        ConstructionCursorRegistration.EnsureRegistered(InventoryManager.Instance);
    }

    private static void RegisterElectronicsPrinterRecipe(MultiConstructor kit)
    {
        Recipe recipe = new Recipe
        {
            Time = RecipeSeconds,
            Energy = RecipeEnergy,
            Copper = RecipeCopper,
            Gold = RecipeGold
        };

        WorldManager.RecipeData recipeData = new WorldManager.RecipeData
        {
            PrefabName = KitPrefabName,
            Recipe = recipe,
            RecipeTier = MachineTier.Undefined,
            Output = 1f
        };

        ElectronicsPrinter.RecipeComparable.AddRecipe(recipeData, null);
        ElectronicsPrinter.RecipeComparable.GenerateRecipieList();
        SynchronizeRecipeReferenceList(ElectronicReader.ElectronicsPrinterRecipes, kit, recipe);
        SynchronizeRecipeReferenceList(ElectronicReader.AllRecipes, kit, recipe);
    }

    private static void SynchronizeRecipeReferenceList(List<RecipeReference> recipes, MultiConstructor kit,
        Recipe recipe)
    {
        for (int index = recipes.Count - 1; index >= 0; index--)
        {
            RecipeReference? existing = recipes[index];
            if (existing != null && existing.DynamicThing != null && existing.DynamicThing.PrefabHash == kit.PrefabHash)
            {
                recipes.RemoveAt(index);
            }
        }

        recipes.Add(new RecipeReference(kit, recipe, PrinterPrefabName));
    }
}
