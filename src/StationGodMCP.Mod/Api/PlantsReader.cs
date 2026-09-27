#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Genetics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Genetics;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// Reads one plant. Genes shift a plant's needs; reading them through the game's accessors adds an empty gene set to
/// a plant that has none (Plant.Genes), so such a plant is read at gene value 0 (PlantStat.Base) instead.
/// </summary>
internal static class PlantReader
{
    internal static PlantView Read(Plant plant)
    {
        PlantFacts facts = new PlantFacts(plant);
        PlantIdentity identity = new PlantIdentity(GameLookup.ViewOf(plant), NameOf(plant), plant.IsPlanted,
            TrayView(facts.Tray));
        PlantCare care = new PlantCare(Efficiency(plant, facts), PlantConditions.States(plant),
            facts.HasNeeds ? PlantConditions.Read(plant, facts.HasGenes) : null,
            facts.HasNeeds && facts.HasGenes ? plant.PlantStatus.CanHeal(plant) : null, Health(plant.DamageState));
        PlantSupply supply = new PlantSupply(Harvest(plant, facts), Fertiliser(plant, facts), Light(plant),
            PlantAir.Breathing(plant.BreathingAtmosphere), PlantAir.Water(facts.Tray),
            facts.HasNeeds ? PlantNeeds.Read(plant, facts.HasGenes) : null);
        PlantHistory history = new PlantHistory(Record(plant.PlantRecord), Genes(plant, facts.HasGenes),
            PlantForecast.Read(facts));
        return new PlantView(identity, Growth(plant, facts), care, supply, history);
    }

    private static string? NameOf(Plant plant) =>
        string.IsNullOrEmpty(plant.CustomName) ? Localization.GetThingName(plant.PrefabName) : plant.CustomName;

    private static PlantTrayView? TrayView(IGrower? tray)
    {
        if (tray == null)
        {
            return null;
        }

        Thing? thing = tray as Thing;
        ThingView view = new ThingView(new ThingId(tray.ReferenceId), thing != null ? thing.PrefabName : null,
            tray.DisplayName);
        return new PlantTrayView(view, tray.IsLitByGrowLight);
    }

    private static PlantGrowth Growth(Plant plant, PlantFacts facts)
    {
        List<StageView> stages = new List<StageView>(facts.Stages.Count);
        for (int index = 0; index < facts.Stages.Count; index++)
        {
            PlantStage stage = facts.Stages[index];
            stages.Add(new StageView(index, stage.Length, stage.Mature, stage.Seed, stage.Dead));
        }

        StageMarks marks = new StageMarks(facts.FirstMature, facts.FirstSeeding, facts.DeadIndex, plant.MatureIndex,
            plant.SeedingIndex);
        PlantStage? current = facts.Current;
        PlantStageView stageView = new PlantStageView(facts.Stage, facts.Stages.Count, marks, facts.Progress,
            current != null ? current.Length : null);
        bool ready = !facts.Dead && plant.WillHarvest(out Thing _);
        PlantPhase phase = new PlantPhase(plant.IsMature, plant.IsSeeding, facts.Dead, facts.Perennial, ready);
        return new PlantGrowth(stageView, stages, plant.MaturityRatio, plant.SeedingRatio, phase);
    }

    private static PlantEfficiencyView Efficiency(Plant plant, PlantFacts facts)
    {
        PlantStatus status = plant.PlantStatus;
        PlantFactors factors = new PlantFactors(status.BreathingEfficiency, status.TemperatureEfficiency,
            status.HydrationEfficiency, status.PressureEfficiency, status.LightEfficiency, plant.GrowthEfficiencyRNG);
        float? geneSpeed = facts.HasNeeds ? Stat(plant.lifeRequirements.GrowthSpeedMultiplier, facts.HasGenes) : null;
        return new PlantEfficiencyView(FloatOrNull(facts.Efficiency), plant.GrowthEfficiencyPercent, factors,
            geneSpeed);
    }

    // At MaxDamage the plant dies: a mature one turns to its dead stage, any other is destroyed
    // (Plant.OnDamageDestroyed).
    private static PlantHealthView? Health(IndestructableDamageState? damage)
    {
        if (damage == null)
        {
            return null;
        }

        float total = damage.Total;
        float max = damage.MaxDamage;
        double ratio = max > 0f ? Math.Min(1.0, Math.Max(0.0, (double)total / max)) : 0.0;
        int percent = Mathf.RoundToInt((float)(100.0 - ratio * 100.0));
        DamagePartsView parts = new DamagePartsView(damage.Brute, damage.Burn, damage.Oxygen, damage.Hydration,
            damage.Starvation, damage.Toxic, damage.Radiation, damage.Decay, damage.Stun);
        return new PlantHealthView(Math.Round(ratio, 4), percent, total, max, parts);
    }

    private static PlantHarvestView Harvest(Plant plant, PlantFacts facts)
    {
        HarvestCounts counts = new HarvestCounts(plant.HarvestQuantity, plant.HarvestQuantityMax, plant.SeedQuantity,
            plant.FertilizerHarvestQuantityBoost);
        HarvestForecastView? forecast = facts.HasNeeds ? PlantForecast.Harvest(plant, facts.HasGenes) : null;
        return new PlantHarvestView(counts, forecast, plant.NutritionValue);
    }

    private static PlantFertiliserView Fertiliser(Plant plant, PlantFacts facts)
    {
        Fertiliser? waiting = facts.Waiting;
        WaitingFertiliserView? waitingView = waiting == null
            ? null
            : new WaitingFertiliserView(new ThingId(waiting.ReferenceId), waiting.PrefabName, waiting.GrowthSpeed,
                waiting.HarvestBoost, waiting.Cycles);
        return new PlantFertiliserView(plant.IsFertilized, facts.Boost, plant.FertilizerHarvestQuantityBoost,
            waitingView);
    }

    // A Hydroponics Tray: 0.8 while a linked grow light is on and powered, plus the sun's share where daylight
    // reaches (HydroponicTray.CurrentLightExposure). Light efficiency is 1 / (1 + PlantRecord.LightStress).
    private static PlantLightView Light(Plant plant)
    {
        PlantRecord? record = plant.PlantRecord;
        return new PlantLightView(plant.CurrentLightExposure, plant.PlantStatus.GetCurrentState(PlantStatusType.Lit),
            record?.TimeLitRatio, record?.TimeDarknessRatio, record?.LightStress);
    }

    private static PlantRecordView? Record(PlantRecord? record)
    {
        if (record == null)
        {
            return null;
        }

        PressureTimes pressure = new PressureTimes(record.TimeSuffocated, record.TimeLowPressure,
            record.TimeHighPressure, record.TimePolluted);
        return new PlantRecordView(record.Age, record.TimeDehydrated, record.TimeFrozen, record.TimeOverHeated,
            pressure);
    }

    private static SortedDictionary<string, float>? Genes(Plant plant, bool hasGenes)
    {
        if (!hasGenes)
        {
            return null;
        }

        GeneCollection top = plant.StackedGeneCollections[plant.StackedGeneCollections.Count - 1];
        SortedDictionary<string, float> genes = new SortedDictionary<string, float>(StringComparer.Ordinal);
        foreach (KeyValuePair<Gene, GeneWrapper> pair in top.Lookup)
        {
            genes[pair.Key.ToString()] = pair.Value.Value;
        }

        return genes;
    }

    internal static float Stat(PlantStat stat, bool hasGenes) => hasGenes ? (float)stat : stat.Base;

    internal static float? FloatOrNull(float value) => float.IsNaN(value) ? null : value;
}

/// <summary>
/// What the reader needs of a plant more than once: its stages, progress, genes, tray and growth rate.
/// </summary>
internal sealed class PlantFacts
{
    internal PlantFacts(Plant plant)
    {
        Stages = plant.GrowthStates ?? new List<PlantStage>();
        Stage = plant.Stage;
        Current = Stage >= 0 && Stage < Stages.Count ? Stages[Stage] : null;
        Progress = (float)GameMembers.PlantStageTime.GetValue(plant)!;
        Perennial = (bool)GameMembers.PlantPerennial.GetValue(plant)!;
        Boost = (float)GameMembers.PlantFertilizerBoost.GetValue(plant)!;
        FirstMature = IndexOf(Stages, StageKind.Mature);
        FirstSeeding = IndexOf(Stages, StageKind.Seed);
        DeadIndex = IndexOf(Stages, StageKind.Dead);
        HasGenes = plant.StackedGeneCollections != null && plant.StackedGeneCollections.Count > 0;
        HasNeeds = plant.lifeRequirements != null && plant.lifeRequirements.Data != null;
        Efficiency = HasNeeds && HasGenes ? plant.lifeRequirements!.GrowthEfficiency() : float.NaN;
        Rate = float.IsNaN(Efficiency) || Boost == null ? float.NaN : Efficiency * Boost.Value;
        Slot? slot = plant.ParentSlot;
        Tray = slot != null && slot.Parent is IGrower holder ? holder : plant.ParentTray;
        Waiting = WaitingFertiliser(plant, Tray);
        Dead = plant.IsDead;
    }

    private enum StageKind
    {
        Mature,
        Seed,
        Dead
    }

    internal List<PlantStage> Stages { get; }

    internal int Stage { get; }

    internal PlantStage? Current { get; }

    internal float? Progress { get; }

    internal bool? Perennial { get; }

    internal float? Boost { get; }

    internal int FirstMature { get; }

    internal int FirstSeeding { get; }

    internal int DeadIndex { get; }

    internal bool HasGenes { get; }

    internal bool HasNeeds { get; }

    internal float Efficiency { get; }

    internal float Rate { get; }

    internal IGrower? Tray { get; }

    internal Fertiliser? Waiting { get; }

    internal bool Dead { get; }

    private static int IndexOf(List<PlantStage> stages, StageKind kind)
    {
        for (int index = 0; index < stages.Count; index++)
        {
            PlantStage stage = stages[index];
            bool match = kind == StageKind.Mature ? stage.Mature : kind == StageKind.Seed ? stage.Seed : stage.Dead;
            if (match)
            {
                return index;
            }
        }

        return -1;
    }

    private static Fertiliser? WaitingFertiliser(Plant plant, IGrower? tray)
    {
        if (tray == null || plant.ParentSlot == null)
        {
            return null;
        }

        try
        {
            Slot? fertiliserSlot = tray.PlantToFertiliserSlotMapping(plant.ParentSlot.Action).FertiliserSlot;
            return fertiliserSlot != null ? fertiliserSlot.Get() as Fertiliser : null;
        }
        catch (Exception)
        {
            // IGrower.PlantToFertiliserSlotMapping throws for a grower with no fertiliser slot for this plant slot.
            return null;
        }
    }
}
