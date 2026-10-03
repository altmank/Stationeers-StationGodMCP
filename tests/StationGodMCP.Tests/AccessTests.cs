#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Access;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Server;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Stage 6: keys, levels, the owner's approvals for cheat, the old protocol's levels, and version 2 over TCP.
/// </summary>
public sealed class AccessTests
{
    private const string ReadKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string WriteKey = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=";
    private const string CheatKey = "QEFCQ0RFRkdISUpLTE1OT1BRUlNUVVZXWFlaW1xdXl8=";

    private static readonly StationGodMCP.Pure.Catalogue.Catalogue Catalogue = TestCatalogue.File.Value.Catalogue;

    // ---- the permission decision ----

    [Theory]
    [InlineData((int)AccessLevel.Read, (int)MethodClass.Read, false, false, false, "allowed")]
    [InlineData((int)AccessLevel.Read, (int)MethodClass.Write, false, false, false, "write")]
    [InlineData((int)AccessLevel.Read, (int)MethodClass.Cheat, false, false, false, "cheat")]
    [InlineData((int)AccessLevel.Write, (int)MethodClass.Write, false, false, false, "allowed")]
    [InlineData((int)AccessLevel.Write, (int)MethodClass.Cheat, false, false, false, "cheat")]
    [InlineData((int)AccessLevel.Write, (int)MethodClass.Cheat, true, false, false, "not_armed")]
    [InlineData((int)AccessLevel.Write, (int)MethodClass.Cheat, true, true, false, "allowed")]
    [InlineData((int)AccessLevel.Write, (int)MethodClass.Cheat, true, false, true, "allowed")]
    [InlineData((int)AccessLevel.Read, (int)MethodClass.Write, true, false, false, "allowed")]
    [InlineData((int)AccessLevel.Cheat, (int)MethodClass.Cheat, false, false, false, "not_armed")]
    [InlineData((int)AccessLevel.Cheat, (int)MethodClass.Cheat, false, true, false, "allowed")]
    [InlineData((int)AccessLevel.Cheat, (int)MethodClass.Cheat, false, false, true, "allowed")]
    [InlineData((int)AccessLevel.Cheat, (int)MethodClass.Write, false, false, false, "allowed")]
    [InlineData((int)AccessLevel.Cheat, (int)MethodClass.Read, false, false, false, "allowed")]
    public void TheDecisionForEveryLevelClassGrantAndApproval(int level, int effective, bool granted, bool armed,
        bool standing, string expected)
    {
        ConnectionAccess access = new ConnectionAccess((AccessLevel)level, granted ? new[] { "m" } : null, standing);

        Permission decision = Permission.Decide(access, "m", (MethodClass)effective, armed);

        string outcome = decision switch
        {
            Permission.Denied denied => AccessLevels.Name(denied.Required),
            Permission.NotArmed _ => "not_armed",
            _ => "allowed"
        };
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("place_cables", "{}", (int)MethodClass.Read)]
    [InlineData("place_cables", """{"dry_run":false}""", (int)MethodClass.Write)]
    [InlineData("place_structure", """{"free":true,"dry_run":false}""", (int)MethodClass.Cheat)]
    [InlineData("place_structure", """{"free":true}""", (int)MethodClass.Read)]
    [InlineData("place_structure", """{"dry_run":false}""", (int)MethodClass.Write)]
    [InlineData("move_gas", """{"dry_run":true}""", (int)MethodClass.Read)]
    [InlineData("move_gas", "{}", (int)MethodClass.Cheat)]
    [InlineData("plant_genes", """{"reference_id":"5"}""", (int)MethodClass.Read)]
    [InlineData("plant_genes", """{"reference_id":"5","genes":{}}""", (int)MethodClass.Cheat)]
    [InlineData("paste_blueprint", """{"status":true}""", (int)MethodClass.Read)]
    [InlineData("paste_blueprint", """{"name":"x"}""", (int)MethodClass.Cheat)]
    [InlineData("write_memory", "{}", (int)MethodClass.Cheat)]
    [InlineData("run_console_command", "{}", (int)MethodClass.Cheat)]
    [InlineData("move_item", "{}", (int)MethodClass.Write)]
    [InlineData("trader_buy", "{}", (int)MethodClass.Write)]
    [InlineData("vault_deposit", """{"dry_run":false}""", (int)MethodClass.Write)]
    public void TheClassRulesGiveTheEffectiveClass(string method, string arguments, int expected)
    {
        Assert.True(Catalogue.TryGet(method, out CatalogueMethod? entry));
        Assert.Equal((MethodClass)expected, entry!.ClassAt(JObject.Parse(arguments)));
    }

    // ---- proofs ----

    [Fact]
    public void TheProofVectorsAreTheClientLibrarysToo()
    {
        string path = Path.Combine(CatalogueFiles.RepositoryRoot(), "clients", "fixtures", "hmac", "vectors.json");
        JArray cases = (JArray)JObject.Parse(File.ReadAllText(path))["cases"]!;
        Assert.NotEmpty(cases);
        foreach (JToken vector in cases)
        {
            byte[] key = Convert.FromBase64String((string)vector["key"]!);
            string proof = KeyProof.Compute(key, (string)vector["nonce"]!, (string)vector["client"]!, (string)vector["transport"]!);
            Assert.Equal((string)vector["proof"]!, proof);
            Assert.True(KeyProof.Matches(key, (string)vector["nonce"]!, (string)vector["client"]!, (string)vector["transport"]!, proof));
            Assert.False(KeyProof.Matches(key, (string)vector["nonce"]!, (string)vector["client"]!, "other", proof));
        }
    }

    // ---- the clients file ----

    [Fact]
    public void AWrongEntryDisablesOnlyThatClientAndIsLoggedWithoutItsKey()
    {
        string shortKey = Convert.ToBase64String(new byte[16]);
        ClientsFile file = ClientsFile.Parse(new JObject
        {
            ["clients"] = new JArray(
                Entry("good", ReadKey, "read"),
                Entry("short", shortKey, "write"),
                Entry("odd-level", WriteKey, "superuser"),
                Entry("twice", CheatKey, "cheat"),
                Entry("twice", CheatKey, "read"),
                Entry("bad-transport", WriteKey, "write", transports: new JArray("pipe", "udp")),
                Entry("bad name!", WriteKey, "write"))
        }.ToString());

        Assert.Equal(new[] { "good" }, file.Clients.Keys);
        Assert.Equal(5, file.Problems.Count);
        string log = string.Join("\n", file.Problems);
        foreach (string key in new[] { ReadKey, WriteKey, CheatKey, shortKey })
        {
            Assert.DoesNotContain(key, log);
        }

        Assert.Contains(ClientKey.FingerprintOf(Convert.FromBase64String(WriteKey)), log);
        Assert.Contains("'twice'", log);
    }

    [Fact]
    public void AnEntryReadsItsDefaults()
    {
        ClientsFile file = ClientsFile.Parse(new JObject { ["clients"] = new JArray(new JObject { ["name"] = "d", ["key"] = WriteKey, ["level"] = "write" }) }.ToString());

        ClientKey key = file.Clients["d"];
        Assert.Equal(new[] { "pipe" }, key.Transports);
        Assert.Empty(key.Grants);
        Assert.False(key.StandingCheat);
        Assert.False(file.AnyTcp);
    }

    // ---- signing in ----

    [Fact]
    public async Task AKeySignsInWithItsNameLevelAndGrants()
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Open(rig);

        JObject welcome = client.SignIn("probe-write", WriteKey, "pipe");

        Assert.Equal("welcome", (string?)welcome["type"]);
        Assert.Equal("probe-write", (string?)welcome["client"]);
        Assert.Equal("write", (string?)welcome["level"]);
        Assert.Equal(new[] { "write_memory" }, welcome["grants"]!.Values<string>());
    }

    [Theory]
    [InlineData("probe-write", WriteKey, "probe-read", null)]
    [InlineData("probe-write", ReadKey, null, null)]
    [InlineData("nobody", WriteKey, null, null)]
    [InlineData("probe-write", WriteKey, null, "00")]
    [InlineData("tcp-only", CheatKey, null, null)]
    public async Task ABadSignInIsUnauthorizedAndCloses(string name, string key, string? authName, string? badProof)
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Open(rig);

        JObject answer = client.SignIn(name, key, "pipe", authName, badProof);

        Assert.Equal("unauthorized", (string?)answer["error"]!["code"]);
        Assert.True(client.WaitClosed(5000));
    }

    [Fact]
    public async Task ASignInNotFinishedInTimeIsClosed()
    {
        using PipeRig rig = new PipeRig(settings: new ProtocolSettings(32, firstLineTimeoutMilliseconds: 500), access: Access());
        using V2Client client = await V2Client.Open(rig);
        client.Hello("probe-write", key: true);
        Assert.Equal("challenge", (string?)client.Next()["type"]);

        JObject refused = client.Next(3000);

        Assert.Equal("protocol_error", (string?)refused["error"]!["code"]);
        Assert.True(client.WaitClosed(5000));
    }

    [Fact]
    public async Task AKeylessPipeConnectionGetsTheAnonymousLevel()
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Connect(rig);

        Assert.Equal("anonymous", (string?)client.Welcome["client"]);
        Assert.Equal("write", (string?)client.Welcome["level"]);
        JObject real = client.Call("g", "move_gas", JObject.Parse("""{"from":"1","to":"2","gases":["Oxygen"],"amount_mol":1}"""));
        Assert.Equal("permission_denied", (string?)real["error"]!["code"]);
        Assert.Equal("cheat", (string?)real["error"]!["data"]!["required"]);
        Assert.Equal("write", (string?)real["error"]!["data"]!["level"]);
        Assert.Equal("move_gas", (string?)real["error"]!["data"]!["method"]);
        JObject dry = client.Call("d", "move_gas", JObject.Parse("""{"from":"1","to":"2","gases":["Oxygen"],"amount_mol":1,"dry_run":true}"""));
        Assert.True((bool)dry["ok"]!, dry.ToString());
    }

    [Fact]
    public async Task AnonymousNoneRefusesKeylessConnections()
    {
        using PipeRig rig = new PipeRig(access: new AccessControl(
            new AccessSettings(AccessLevel.None, false, AccessLevel.Cheat, AccessLevel.Cheat, false), null));
        using V2Client client = await V2Client.Open(rig);

        Assert.Equal("unauthorized", (string?)client.SignIn("x", null, "pipe")["error"]!["code"]);
    }

    [Fact]
    public async Task AReadKeyMayReadAndDryRunButNotWrite()
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Open(rig);
        client.SignIn("probe-read", ReadKey, "pipe");

        Assert.True((bool)client.Call("r", "read_logic", JObject.Parse("""{"reference_id":"5","logic_type":"On"}"""))["ok"]!);
        JObject write = client.Call("w", "write_logic", JObject.Parse("""{"reference_id":"5","logic_type":"On","value":1}"""));
        Assert.Equal("permission_denied", (string?)write["error"]!["code"]);
        Assert.Equal("write", (string?)write["error"]!["data"]!["required"]);
        Assert.True((bool)client.Call("p", "place_cables", JObject.Parse("""{"waypoints":[[0,0,0],[1,0,0]]}"""))["ok"]!);
        JObject real = client.Call("q", "place_cables", JObject.Parse("""{"waypoints":[[0,0,0],[1,0,0]],"dry_run":false,"confirm":true}"""));
        Assert.Equal("permission_denied", (string?)real["error"]!["code"]);
    }

    [Fact]
    public async Task AGrantReachesOneMethodBeyondTheLevel()
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Open(rig);
        client.SignIn("probe-write", WriteKey, "pipe");

        JObject memory = client.Call("m", "write_memory", JObject.Parse("""{"reference_id":"5","start_address":0,"values":[1]}"""));

        Assert.Equal("cheat_not_armed", (string?)memory["error"]!["code"]);
    }

    [Fact]
    public async Task CheatNeedsTheOwnersApprovalForThatConnectionOrItsKey()
    {
        DateTime now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        AccessControl access = Access(new Arming(() => now));
        using PipeRig rig = new PipeRig(access: access);
        using V2Client first = await V2Client.Open(rig);
        using V2Client second = await V2Client.Open(rig);
        first.SignIn("probe-cheat", CheatKey, "pipe");
        second.SignIn("probe-cheat", CheatKey, "pipe");
        string firstId = (string)first.Welcome["client_id"]!;
        Assert.False((bool)first.Welcome["cheat"]!["armed"]!);
        JObject move = JObject.Parse("""{"from":"1","to":"2","gases":["Oxygen"],"amount_mol":1}""");

        JObject refused = first.Call("a", "move_gas", move);
        Assert.Equal("cheat_not_armed", (string?)refused["error"]!["code"]);
        Assert.Equal(firstId, (string?)refused["error"]!["data"]!["client_id"]);
        Assert.Contains($"stationgod allow {firstId}", (string?)refused["error"]!["message"]);

        Assert.StartsWith("Cheat armed for " + firstId, StationGodConsole.Execute(new[] { "allow", firstId, "1" }, access));
        JObject armed = first.Next();
        Assert.Equal("cheat_armed", (string?)armed["event"]);
        Assert.Equal("2026-10-02T12:01:00Z", (string?)armed["until_utc"]);
        Assert.True((bool)first.Call("b", "move_gas", move)["ok"]!);
        Assert.Equal("cheat_not_armed", (string?)second.Call("c", "move_gas", move)["error"]!["code"]);

        now = now.AddMinutes(2);
        access.RefreshCheat();
        Assert.Equal("cheat_disarmed", (string?)first.Next()["event"]);
        Assert.Equal("cheat_not_armed", (string?)first.Call("d", "move_gas", move)["error"]!["code"]);

        StationGodConsole.Execute(new[] { "allow", "probe-cheat", "5" }, access);
        Assert.Equal("cheat_armed", (string?)first.Next()["event"]);
        Assert.Equal("cheat_armed", (string?)second.Next()["event"]);
        StationGodConsole.Execute(new[] { "deny", "probe-cheat" }, access);
        Assert.Equal("cheat_disarmed", (string?)second.Next()["event"]);
    }

    [Fact]
    public void ArmingClampsItsMinutesAndEnds()
    {
        DateTime now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Arming arming = new Arming(() => now);

        DateTime until = arming.ArmKey("k", 999);
        Assert.Equal(now.AddMinutes(Arming.MaximumMinutes), until);
        Assert.Equal(until, arming.ArmedUntil("c1", "k"));
        Assert.Null(arming.ArmedUntil("c1", "other"));
        now = until;
        Assert.Null(arming.ArmedUntil("c1", "k"));
    }

    [Fact]
    public void TheCommandRefusesToArmFromTheToolConsole()
    {
        AccessControl access = Access();
        using (StationGodConsole.ToolConsole())
        {
            Assert.StartsWith("Refused", StationGodConsole.Execute(new[] { "allow", "probe-cheat", "1" }, access));
            Assert.StartsWith("Refused", StationGodConsole.Execute(new[] { "deny", "probe-cheat" }, access));
        }

        AccessControl testServer = new AccessControl(
            new AccessSettings(AccessLevel.Write, false, AccessLevel.Cheat, AccessLevel.Cheat, true), null,
            clients: Keys());
        using (StationGodConsole.ToolConsole())
        {
            Assert.StartsWith("Cheat armed", StationGodConsole.Execute(new[] { "allow", "probe-cheat", "1" }, testServer));
        }

        Assert.True(StationGodConsole.RefusedFromToolConsole("stationgod", access));
        Assert.False(StationGodConsole.RefusedFromToolConsole("stationgod", testServer));
        Assert.False(StationGodConsole.RefusedFromToolConsole("pause", access));
        Assert.True(StationGodConsole.IsStationGod("stationgod"));
        Assert.True(StationGodConsole.IsStationGod("-StationGod"));
        Assert.False(StationGodConsole.IsStationGod("stationgodx"));
    }

    [Fact]
    public async Task AKeyRemovedOrLoweredIsRevoked()
    {
        AccessControl access = Access();
        using PipeRig rig = new PipeRig(access: access);
        using V2Client write = await V2Client.Open(rig);
        using V2Client read = await V2Client.Open(rig);
        write.SignIn("probe-write", WriteKey, "pipe");
        read.SignIn("probe-read", ReadKey, "pipe");

        access.Replace(ClientsFile.Parse(new JObject
        {
            ["clients"] = new JArray(Entry("probe-write", WriteKey, "read"), Entry("probe-read", ReadKey, "read"))
        }.ToString()));

        Assert.Equal("revoked", (string?)write.Next()["reason"]);
        Assert.True(write.WaitClosed(5000));
        Assert.True((bool)read.Call("r", "game_clock", new JObject())["ok"]!);
    }

    // ---- the old protocol ----

    [Fact]
    public async Task TheOldPipeProtocolKeepsItsLevelAndAtWriteRefusesCheat()
    {
        using PipeRig rig = new PipeRig(access: new AccessControl(
            new AccessSettings(AccessLevel.Write, false, AccessLevel.Write, AccessLevel.Cheat, false), null));
        using V2Client client = await V2Client.Open(rig);
        string cheat = """{"id":"1","method":"run_console_command","params":{"command":"help"}}""";
        string read = """{"id":"2","method":"game_clock","params":{}}""";

        client.Send(cheat);
        JObject refused = client.Next();
        client.Send(read);

        Assert.Equal("1", (string?)refused["id"]);
        Assert.False((bool)refused["ok"]!);
        Assert.Equal("permission_denied", (string?)refused["error"]!["code"]);
        Assert.Equal(JObject.Parse(FakeMod.ReplyTo(read)), client.Next());
    }

    [Fact]
    public async Task TheOldPipeProtocolAtNoneIsRefused()
    {
        using PipeRig rig = new PipeRig(access: new AccessControl(
            new AccessSettings(AccessLevel.Write, false, AccessLevel.None, AccessLevel.Cheat, false), null));
        using V2Client client = await V2Client.Open(rig);
        client.Send("""{"id":"1","method":"game_clock","params":{}}""");

        Assert.Equal("unauthorized", (string?)client.Next()["error"]!["code"]);
        Assert.True(client.WaitClosed(5000));
    }

    [Fact]
    public async Task TheDefaultsKeepEveryOldPipeClientAtCheat()
    {
        using PipeRig rig = new PipeRig(access: Access());
        using V2Client client = await V2Client.Open(rig);
        string line = """{"id":"1","method":"run_console_command","params":{"command":"help"}}""";
        client.Send(line);

        Assert.Equal(JObject.Parse(FakeMod.ReplyTo(line)), client.Next());
    }

    // ---- TCP ----

    [Theory]
    [InlineData(true, "secret", false, true)]
    [InlineData(true, "", true, true)]
    [InlineData(true, "", false, false)]
    [InlineData(false, "secret", true, false)]
    public void TcpListensWithASecretOrATcpKey(bool enabled, string secret, bool anyTcpKey, bool listens)
    {
        Assert.Equal(listens, TcpAcceptor.ShouldListen(enabled, secret, anyTcpKey));
    }

    [Fact]
    public void ATcpKeyIsSeen()
    {
        Assert.True(Keys().AnyTcp);
    }

    [Fact]
    public async Task VersionTwoOverTcpNeedsAKey()
    {
        using TcpRig rig = new TcpRig(Access(), secret: null);
        using V2Client anonymous = rig.Connect();
        Assert.Equal("unauthorized", (string?)anonymous.SignIn("x", null, "tcp")["error"]!["code"]);

        using V2Client keyed = rig.Connect();
        JObject welcome = keyed.SignIn("tcp-only", CheatKey, "tcp");
        Assert.Equal("welcome", (string?)welcome["type"]);
        Assert.Equal("tcp", (string?)welcome["server"]!["transport"]);

        using V2Client pipeKey = rig.Connect();
        Assert.Equal("unauthorized", (string?)pipeKey.SignIn("probe-write", WriteKey, "tcp")["error"]!["code"]);
        await Task.CompletedTask;
    }

    [Fact]
    public void WithoutASecretTheOldTcpSignInIsRefused()
    {
        using TcpRig rig = new TcpRig(Access(), secret: null);
        using V2Client client = rig.Connect();
        client.Send("""{"type":"auth","secret":"anything"}""");

        JObject refused = client.Next();
        Assert.False((bool)refused["ok"]!);
        Assert.Equal("unauthorized", (string?)refused["error"]!["code"]);
    }

    [Theory]
    [InlineData((int)AccessLevel.Cheat, true)]
    [InlineData((int)AccessLevel.Write, false)]
    public void TheOldTcpSignInGetsTheLegacyLevelAndIsLogged(int legacy, bool consoleAllowed)
    {
        ConcurrentQueue<string> log = new ConcurrentQueue<string>();
        Action<string> previous = ProtocolLog.InfoSink;
        ProtocolLog.InfoSink = line => log.Enqueue(line);
        try
        {
            using TcpRig rig = new TcpRig(new AccessControl(
                new AccessSettings(AccessLevel.Write, false, AccessLevel.Cheat, (AccessLevel)legacy, false), null, clients: Keys()), "s3cret");
            using V2Client client = rig.Connect();
            client.Send("""{"type":"auth","secret":"s3cret"}""");
            Assert.True((bool)client.Next()["ok"]!);
            string line = """{"id":"1","method":"run_console_command","params":{"command":"help"}}""";
            client.Send(line);

            JObject reply = client.Next();

            if (consoleAllowed)
            {
                Assert.Equal(JObject.Parse(FakeMod.ReplyTo(line)), reply);
            }
            else
            {
                Assert.Equal("permission_denied", (string?)reply["error"]!["code"]);
            }

            Assert.Contains(log, entry => entry.StartsWith("Legacy TCP sign-in from 127.0.0.1"));
        }
        finally
        {
            ProtocolLog.InfoSink = previous;
        }
    }

    // ---- key new ----

    [Fact]
    public void KeyNewWritesAnEntryTheModLoadsAndShowsTheKeyOnlyToItsOwnOutput()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sgm-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            StringWriter output = new StringWriter();
            StringWriter error = new StringWriter();
            int? exit = KeyCommand.TryRun(new[] { "key", "new", "probe-cheat", "cheat", "--config", folder,
                "--transports", "pipe,tcp", "--grants", "write_memory" }, output, error);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, error.ToString());
            string path = Path.Combine(folder, KeyCommand.FileName);
            ClientsFile file = ClientsFile.Parse(File.ReadAllText(path));
            ClientKey key = Assert.Single(file.Clients.Values);
            Assert.Equal(AccessLevel.Cheat, key.Level);
            Assert.True(key.Transports.SetEquals(new[] { "pipe", "tcp" }));
            Assert.Contains("write_memory", key.Grants);
            Assert.Contains(Convert.ToBase64String(key.Key), output.ToString());
            Assert.Equal(32, key.Key.Length);

            Assert.Equal(2, KeyCommand.TryRun(new[] { "key", "new", "probe-cheat", "read", "--config", folder }, new StringWriter(), error));
            Assert.Equal(0, KeyCommand.TryRun(new[] { "key", "new", "probe-cheat", "read", "--config", folder, "--replace" },
                new StringWriter(), new StringWriter()));
            Assert.Equal(AccessLevel.Read, ClientsFile.Parse(File.ReadAllText(path)).Clients["probe-cheat"].Level);
            Assert.Equal(2, KeyCommand.TryRun(new[] { "key", "new", "x y", "god" }, new StringWriter(), new StringWriter()));
            Assert.Null(KeyCommand.TryRun(new[] { "--pipe", "x" }, new StringWriter(), new StringWriter()));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // ---- helpers ----

    private static AccessControl Access(Arming? arming = null) =>
        new AccessControl(AccessSettings.Defaults, null, arming, Keys());

    private static ClientsFile Keys() => ClientsFile.Parse(new JObject
    {
        ["clients"] = new JArray(
            Entry("probe-read", ReadKey, "read"),
            Entry("probe-write", WriteKey, "write", grants: new JArray("write_memory")),
            Entry("probe-cheat", CheatKey, "cheat"),
            Entry("tcp-only", CheatKey, "cheat", transports: new JArray("tcp")))
    }.ToString());

    private static JObject Entry(string name, string key, string level, JArray? grants = null, JArray? transports = null) =>
        new JObject
        {
            ["name"] = name, ["key"] = key, ["level"] = level, ["grants"] = grants ?? new JArray(),
            ["transports"] = transports ?? new JArray("pipe")
        };
}

/// <summary>A TCP listener on a free loopback port with the fake mod behind it.</summary>
internal sealed class TcpRig : IDisposable
{
    private readonly DeadlineWatch _deadlines = new DeadlineWatch();
    private readonly TcpAcceptor _acceptor;
    private readonly FakeMod _mod;

    internal TcpRig(AccessControl access, string? secret)
    {
        _mod = new FakeMod(true, _deadlines);
        ProtocolHost host = new ProtocolHost(new ProtocolSettings(8), _mod, _deadlines, TestCatalogue.File.Value, access, secret);
        _acceptor = new TcpAcceptor("127.0.0.1", 0, 8, host);
        _acceptor.Start();
    }

    internal V2Client Connect()
    {
        TcpClient client = new TcpClient();
        client.Connect("127.0.0.1", _acceptor.BoundPort);
        client.NoDelay = true;
        return new V2Client(client.GetStream());
    }

    public void Dispose()
    {
        _acceptor.Dispose();
        _mod.Dispose();
        _deadlines.Dispose();
    }
}
