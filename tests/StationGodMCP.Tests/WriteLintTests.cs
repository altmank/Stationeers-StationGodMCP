#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The write rules on write logic: a write that opens a vent, valve, pump or regulator runs the rules with "write" in
/// their on (no_oxidiser_vented_outdoors, fuel_oxidiser_mix_temperature) over the networks it opens to, every time; a
/// problem refuses (lint_refused) unless allow: ["lint"], and the report shows the refusal. The
/// game side (OpeningWrites, LogicOps.Check) only finds the device and its value and builds these same pieces from
/// the game's objects.
/// </summary>
public sealed class WriteLintTests
{
    private static readonly ReportConfirm Reporting = new ReportConfirm(true, false, null);

    private static JObject Acted(LogicWriteChecked written, double current) =>
        ((VerbOutcome.Answered)LogicWriteReplies.Acted("world",
            new LogicWriteResult[] { new LogicWriteResult.Written(0, "Vent", new LogicWrite(written, current)) },
            false)).Reply;

    // ---- the opening transition ----

    [Theory]
    [InlineData("Vent", "On", 0.0, 1.0, true)]
    [InlineData("Vent", "On", 0.0, 7.0, true)]
    [InlineData("Vent", "On", 0.0, 0.5, false)]
    [InlineData("Vent", "On", 1.0, 1.0, false)]
    [InlineData("Vent", "On", 1.0, 0.0, false)]
    [InlineData("Valve", "On", 0.0, 1.0, true)]
    [InlineData("Pump", "On", 0.0, 1.0, true)]
    [InlineData("Regulator", "On", 0.0, 1.0, true)]
    [InlineData("Valve", "Open", 0.0, 1.0, true)]
    [InlineData("Valve", "Open", 1.0, 0.0, false)]
    [InlineData("Vent", "Mode", 1.0, 0.0, true)]
    [InlineData("Vent", "Mode", 0.0, 1.0, false)]
    [InlineData("Vent", "Mode", 0.0, 0.0, false)]
    [InlineData("Pump", "Mode", 0.0, 1.0, true)]
    [InlineData("Pump", "Mode", 1.0, 5.0, false)]
    [InlineData("Valve", "Mode", 0.0, 1.0, false)]
    [InlineData("Pump", "Setting", 0.0, 10.0, true)]
    [InlineData("Pump", "Setting", 5.0, 10.0, false)]
    [InlineData("Regulator", "Setting", 0.0, 101.0, false)]
    [InlineData("None", "On", 0.0, 1.0, false)]
    [InlineData("Vent", "None", 0.0, 1.0, false)]
    public void AnOpeningIsTheWrittenValueAgainstTheCurrentOne(string device, string logic, double previous,
        double requested, bool opens)
    {
        Assert.Equal(opens, OpeningTransition.Opens(Enum.Parse<OpeningDevice>(device), Enum.Parse<OpeningLogic>(logic),
            previous, requested));
    }

    [Fact]
    public void AWriteOnlyTypeCountsAsClosedBeforeAndDevicesAreKnownByVentAndFlow()
    {
        Assert.True(OpeningTransition.Opens(OpeningDevice.Valve, OpeningLogic.On, null, 1.0));
        Assert.Equal(OpeningDevice.Vent, OpeningTransition.DeviceOf(true, null));
        Assert.Equal(OpeningDevice.Valve, OpeningTransition.DeviceOf(false, "valve"));
        Assert.Equal(OpeningDevice.Pump, OpeningTransition.DeviceOf(false, "pump"));
        Assert.Equal(OpeningDevice.Regulator, OpeningTransition.DeviceOf(false, "regulator"));
        Assert.Equal(OpeningDevice.None, OpeningTransition.DeviceOf(false, "filter"));
        Assert.Equal(OpeningLogic.Setting, OpeningTransition.LogicOf("Setting"));
        Assert.Equal(OpeningLogic.None, OpeningTransition.LogicOf("Lock"));
    }

    // ---- an outdoor vent on an oxidiser network ----

    [Fact]
    public void AnOutdoorVentOnAnOxidiserNetworkIsRefusedAndItsReportShowsIt()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 12.5 });

        ApiException refused = Assert.Throws<ApiException>(() => site.CheckVentOn(previous: 0.0, allow: false));

        Assert.Equal(WriteLint.RefusedCode, refused.Code);
        Assert.Contains("vents outdoors", refused.Message);
        Assert.DoesNotContain("allow", refused.Message);
        LintRefusedView data = Assert.IsType<LintRefusedView>(refused.Details);
        Assert.Equal("no_oxidiser_vented_outdoors", Assert.Single(data.Rules).Rule);
        Assert.Equal(new ThingId(60), data.Rules[0].ReferenceId);

        VerbOutcome report = WriteCheck<LogicWriteChecked>.Run(() => site.CheckVentOn(0.0, allow: false))
            .Outcome(Reporting, _ => throw new InvalidOperationException("a report never acts"),
                written => LogicWriteReplies.Reported("world",
                    new LogicWriteResult[] { new LogicWriteResult.Planned(0, "Vent", written) }));
        JObject wire = ((VerbOutcome.Answered)report).Reply;
        Assert.True((bool)wire["report_only"]!);
        Assert.Equal("lint_refused", (string?)wire["problems"]![0]!["code"]);
        Assert.Equal("no_oxidiser_vented_outdoors", (string?)wire["problems"]![0]!["data"]!["rules"]![0]!["rule"]);
    }

    [Fact]
    public void AllowLintLiftsTheRefusalAndTheProblemIsAWarning()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 12.5 });

        LogicWriteChecked written = site.CheckVentOn(previous: 0.0, allow: true);

        Assert.True(written.Opens);
        BuildIssueView warning = Assert.Single(written.Warnings);
        Assert.Equal("lint_no_oxidiser_vented_outdoors", warning.Code);
        Assert.Contains("let through by allow: [\"lint\"]", warning.Message);
        Assert.True(written.LintPassed);
        JObject wire = Acted(written, 1.0);
        Assert.Equal("lint_no_oxidiser_vented_outdoors", (string?)wire["warnings"]![0]!["code"]);
        Assert.Equal(new JArray("lint"), wire["passed"]);
    }

    [Fact]
    public void ACleanOpeningPassesAndItsReplyLeavesWarningsOut()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Nitrogen"] = 40.0 });

        LogicWriteChecked written = site.CheckVentOn(previous: 0.0, allow: false);

        Assert.True(written.Opens);
        Assert.Empty(written.Warnings);
        Assert.False(written.LintPassed);
        Assert.Null(Acted(written, 1.0)["warnings"]);
        Assert.Null(Acted(written, 1.0)["passed"]);
        Assert.Equal(1, site.Lints);
    }

    [Fact]
    public void AReliefLoopWhoseNetworkGainsOxygenBetweenTwoOpeningsIsRefusedOnTheSecond()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Nitrogen"] = 40.0 });

        Assert.Empty(site.CheckVentOn(previous: 0.0, allow: false).Warnings);
        Assert.False(site.CheckVentOn(previous: 1.0, allow: false, requested: 0.0).Opens);
        site.Fill(new Dictionary<string, double> { ["Nitrogen"] = 40.0, ["Oxygen"] = 0.4 });

        ApiException refused = Assert.Throws<ApiException>(() => site.CheckVentOn(previous: 0.0, allow: false));

        Assert.Equal(WriteLint.RefusedCode, refused.Code);
        Assert.Equal(2, site.Lints);
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(0.0, 0.0)]
    public void AWriteThatDoesNotOpenRunsNoLint(double previous, double requested)
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 12.5 });

        LogicWriteChecked written = site.CheckVentOn(previous, allow: false, requested: requested);

        Assert.False(written.Opens);
        Assert.Empty(written.Warnings);
        Assert.Equal(0, site.Lints);
        Assert.Equal(0, site.RuleReads);
    }

    [Fact]
    public void OpeningAValveIntoAnOutdoorVentsNetworkLintsBothSides()
    {
        Site site = Site.ValveToOutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 30.0 });

        ApiException refused = Assert.Throws<ApiException>(() => site.CheckValveOn(allow: false));

        LintRefusedView data = Assert.IsType<LintRefusedView>(refused.Details);
        Assert.Equal(new ThingId(60), Assert.Single(data.Rules).ReferenceId);
        Assert.Contains("network 8", data.Rules[0].Message);
    }

    [Fact]
    public void AHotFuelMixOnTheOpenedNetworkIsAWarningNotARefusal()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Methane"] = 10.0, ["Nitrogen"] = 5.0 },
            outdoors: false);
        site.Fill(new Dictionary<string, double> { ["Methane"] = 10.0, ["Oxygen"] = 20.0 }, 650.0);

        LogicWriteChecked written = site.CheckVentOn(previous: 0.0, allow: false);

        Assert.Equal("lint_fuel_oxidiser_mix_temperature", Assert.Single(written.Warnings).Code);
    }

    // ---- several writes: refusal per entry ----

    [Fact]
    public void AListsLintRefusalFailsItsEntryAndTheReportListsItWithItsIndexAndRules()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 12.5 });
        LogicWriteChecked closing = site.CheckVentOn(1.0, allow: false, requested: 0.0);
        ApiException refused = Assert.Throws<ApiException>(() => site.CheckVentOn(0.0, allow: false));

        JObject wire = LogicWriteReplies.Reported("world", new LogicWriteResult[]
        {
            new LogicWriteResult.Planned(0, "Vent", closing),
            new LogicWriteResult.Failed(1, "60", VerbError.Of(refused))
        });

        Assert.True((bool)wire["report_only"]!);
        JToken problem = Assert.Single(wire["problems"]!);
        Assert.Equal(1, (int)problem["index"]!);
        Assert.Equal("60", (string?)problem["reference_id"]);
        Assert.Equal("lint_refused", (string?)problem["code"]);
        Assert.Equal("no_oxidiser_vented_outdoors", (string?)problem["data"]!["rules"]![0]!["rule"]);
        Assert.Equal("[60, Vent (On), 1 -> 0] [undo]", (string?)Assert.Single(wire["targets"]!)["text"]);
    }

    [Fact]
    public void AListsWarningsCarryTheirEntryIndex()
    {
        Site site = Site.OutdoorVent(new Dictionary<string, double> { ["Oxygen"] = 12.5 });

        LogicWriteChecked written = site.CheckVentOn(0.0, allow: true, index: 3);

        Assert.Equal(3, Assert.Single(written.Warnings).Index);
    }

    /// <summary>
    /// A vent (60) on pipe network 7, optionally behind a valve (61) from network 8, with rules and world counted:
    /// Lints is how many times the rules ran, RuleReads how many times the rule set was asked for.
    /// </summary>
    private sealed class Site
    {
        private readonly LintRuleSet _rules = LintTestKit.Defaults();
        private readonly Dictionary<string, LintValue> _ventNetwork;
        private readonly Dictionary<string, LintValue>? _otherNetwork;
        private readonly LintRecord _vent;
        private readonly LintRecord? _valve;

        private Site(Dictionary<string, LintValue> ventNetwork, Dictionary<string, LintValue>? otherNetwork,
            LintRecord vent, LintRecord? valve)
        {
            _ventNetwork = ventNetwork;
            _otherNetwork = otherNetwork;
            _vent = vent;
            _valve = valve;
        }

        internal int Lints { get; private set; }

        internal int RuleReads { get; private set; }

        internal static Site OutdoorVent(Dictionary<string, double> gases, bool outdoors = true)
        {
            Dictionary<string, LintValue> network = Network(7, gases);
            LintRecord vent = Vent(outdoors, network);
            network["devices"] = LintValue.Objects(new List<ILintObject> { vent });
            return new Site(network, null, vent, null);
        }

        internal static Site ValveToOutdoorVent(Dictionary<string, double> upstreamGases)
        {
            Dictionary<string, LintValue> ventSide = Network(7, new Dictionary<string, double> { ["Nitrogen"] = 2.0 });
            Dictionary<string, LintValue> otherSide = Network(8, upstreamGases);
            LintRecord ventNet = new LintRecord(LintModel.Network, "network:7", ventSide);
            LintRecord otherNet = new LintRecord(LintModel.Network, "network:8", otherSide);
            LintRecord vent = Vent(true, ventSide, ventNet);
            LintRecord valve = new LintRecord(LintModel.Thing, "thing:61", new Dictionary<string, LintValue>
            {
                ["reference_id"] = LintValue.Of(61), ["prefab"] = LintValue.Of("StructureValve"),
                ["display_name"] = LintValue.Of("Valve"), ["kind"] = LintValue.Of("device"),
                ["runtime_types"] = LintValue.Strings(new List<string> { "Valve", "Device" }),
                ["flow"] = LintValue.Of("valve"), ["outdoors"] = LintValue.False,
                ["networks"] = LintValue.Objects(new List<ILintObject> { otherNet, ventNet }),
                ["ports"] = LintValue.Objects(new List<ILintObject> { Port(otherNet), Port(ventNet) })
            });
            ventSide["devices"] = LintValue.Objects(new List<ILintObject> { vent, valve });
            otherSide["devices"] = LintValue.Objects(new List<ILintObject> { valve });
            return new Site(ventSide, otherSide, vent, valve);
        }

        internal void Fill(Dictionary<string, double> gases, double temperature = 290.0)
        {
            _ventNetwork["gases"] = Gases(gases);
            _ventNetwork["temperature"] = LintValue.Of(temperature);
        }

        internal LogicWriteChecked CheckVentOn(double previous, bool allow, double requested = 1.0, int? index = null) =>
            Check(_vent, OpeningDevice.Vent, previous, requested, allow, index);

        internal LogicWriteChecked CheckValveOn(bool allow) =>
            Check(_valve!, OpeningDevice.Valve, 0.0, 1.0, allow, null);

        // What LogicOps.Check does once it has the device and its value.
        private LogicWriteChecked Check(LintRecord device, OpeningDevice kind, double previous, double requested,
            bool allow, int? index)
        {
            OpeningVerdict verdict = OpeningLint.Judge(kind, OpeningLogic.On, previous, requested, 2,
                () =>
                {
                    RuleReads++;
                    return _rules;
                },
                () =>
                {
                    Lints++;
                    return new OpeningWorld(new TestLintWorld(), device, kind);
                });
            LogicRead requestedRead = new LogicRead(new ThingId(device.ReferenceId!.Value), new LogicTypeView(28, "On"),
                requested);
            return WriteLint.Checked(requestedRead, previous, verdict, allow, index, null);
        }

        private static Dictionary<string, LintValue> Network(long id, Dictionary<string, double> gases) =>
            new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["id"] = LintValue.Of(id), ["kind"] = LintValue.Of("pipe"), ["gases"] = Gases(gases),
                ["temperature"] = LintValue.Of(290.0)
            };

        private static LintRecord Vent(bool outdoors, Dictionary<string, LintValue> network, LintRecord? record = null)
        {
            LintRecord net = record ?? new LintRecord(LintModel.Network, "network:" + network["id"].AsNumber, network);
            return new LintRecord(LintModel.Thing, "thing:60", new Dictionary<string, LintValue>
            {
                ["reference_id"] = LintValue.Of(60), ["prefab"] = LintValue.Of("StructureActiveVent"),
                ["display_name"] = LintValue.Of("Active Vent"), ["kind"] = LintValue.Of("device"),
                ["runtime_types"] = LintValue.Strings(new List<string> { "ActiveVent", "Device" }),
                ["outdoors"] = LintValue.Of(outdoors),
                ["networks"] = LintValue.Objects(new List<ILintObject> { net })
            });
        }

        private static LintRecord Port(LintRecord network) =>
            new LintRecord(LintModel.Port, "port:61/" + network.Key, new Dictionary<string, LintValue>
            {
                ["network"] = LintValue.Of(network)
            });

        private static LintValue Gases(Dictionary<string, double> gases)
        {
            Dictionary<string, LintValue> map = new Dictionary<string, LintValue>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> gas in gases)
            {
                map[gas.Key] = LintValue.Of(gas.Value);
            }

            return LintValue.Of(map);
        }
    }
}
