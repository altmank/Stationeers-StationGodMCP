#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Pictures;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// show picture without the game: the camera's pose (yaw about the thing's level front, pitch, the fitted distance),
/// the path rules that keep every picture inside the pictures folder (escapes by .., drives, rooted and UNC paths,
/// odd characters), the size caps and option refusals the catalogue's schema gives, the one-at-a-time gate, the scene
/// swap that puts neutral lighting back whatever throws, and the opaque pixels.
/// </summary>
public sealed class ShowPictureTests
{
    private const double Tolerance = 1e-9;

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sg-pictures-root");

    private static void Near(Vec3 expected, Vec3 actual)
    {
        Assert.True((expected - actual).Length < 1e-6, $"expected {expected}, got {actual}");
    }

    // ---- Pose ----

    [Fact]
    public void YawZeroStandsInFrontAndNinetyOnTheRightAndOneEightyBehind()
    {
        Vec3 centre = new Vec3(10, 2, 5);
        Vec3 north = new Vec3(0, 0, 1);

        Near(new Vec3(10, 2, 8), PicturePose.Of(centre, north, 1, 0, 0, 3, 40, 16.0 / 9).Position);
        Near(new Vec3(13, 2, 5), PicturePose.Of(centre, north, 1, 90, 0, 3, 40, 16.0 / 9).Position);
        Near(new Vec3(10, 2, 2), PicturePose.Of(centre, north, 1, 180, 0, 3, 40, 16.0 / 9).Position);
        Near(new Vec3(7, 2, 5), PicturePose.Of(centre, north, 1, 270, 0, 3, 40, 16.0 / 9).Position);
    }

    [Fact]
    public void YawFollowsTheThingsOwnFrontAndIgnoresItsTilt()
    {
        // A thing facing east, tilted up: its level front is east, its right is south.
        Vec3 tiltedEast = new Vec3(1, 1, 0);

        Near(new Vec3(2, 0, 0), PicturePose.Of(Vec3.Zero, tiltedEast, 1, 0, 0, 2, 40, 1).Position);
        Near(new Vec3(0, 0, -2), PicturePose.Of(Vec3.Zero, tiltedEast, 1, 90, 0, 2, 40, 1).Position);
        // Straight up has no level front: north is used.
        Near(new Vec3(0, 0, 2), PicturePose.Of(Vec3.Zero, new Vec3(0, 1, 0), 1, 0, 0, 2, 40, 1).Position);
    }

    [Fact]
    public void PitchRaisesTheCameraAndItStillLooksAtTheCentre()
    {
        PicturePose pose = PicturePose.Of(new Vec3(0, 1, 0), new Vec3(0, 0, 1), 1, 0, 30, 2, 40, 1);

        Near(new Vec3(0, 1 + 2 * Math.Sin(Math.PI / 6), 2 * Math.Cos(Math.PI / 6)), pose.Position);
        Near(new Vec3(0, 1, 0), pose.LookAt);
        Assert.Equal(2, (pose.LookAt - pose.Position).Length, 9);
        Assert.True(pose.Direction.Y < 0, "a camera above looks down");
    }

    [Fact]
    public void YawIsWrappedIntoZeroToThreeSixty()
    {
        Assert.Equal(270, PicturePose.Of(Vec3.Zero, new Vec3(0, 0, 1), 1, -90, 0, 2, 40, 1).Yaw, 9);
        Assert.Equal(90, PicturePose.Of(Vec3.Zero, new Vec3(0, 0, 1), 1, 450 - 360, 0, 2, 40, 1).Yaw, 9);
        Assert.Equal(0, PicturePose.Wrap(360), 9);
    }

    [Fact]
    public void ALeftOutDistanceFitsTheBoundsInTheNarrowerFieldOfView()
    {
        // Landscape: the vertical 40 degrees is the narrower.
        double wide = PicturePose.FitDistance(1, 40, 16.0 / 9);
        Assert.Equal(1.1 / Math.Sin(20 * Math.PI / 180), wide, 9);

        // Portrait: the horizontal field is narrower, so the camera stands further back.
        double tall = PicturePose.FitDistance(1, 40, 0.5);
        double horizontalHalf = Math.Atan(Math.Tan(20 * Math.PI / 180) * 0.5);
        Assert.Equal(1.1 / Math.Sin(horizontalHalf), tall, 9);
        Assert.True(tall > wide);

        // A tiny thing is not pictured from inside the near plane.
        Assert.Equal(PicturePose.MinimumFitDistance, PicturePose.FitDistance(0.001, 120, 1), 9);

        PicturePose fitted = PicturePose.Of(Vec3.Zero, new Vec3(0, 0, 1), 1, 0, 0, null, 40, 16.0 / 9);
        Assert.Equal(wide, fitted.Distance, 9);
    }

    // ---- The front ----

    [Fact]
    public void AnEntitysBodyIsItsFrontNotItsTransformWhichTheGameHoldsNorth()
    {
        // A player facing south: ThingTransform.forward stays north, the body's forward is what counts.
        Vec3 front = PictureFront.Of(new Vec3(0, 0, -1), new Vec3(0, 0, 1));

        Near(new Vec3(0, 0, -1), front);
        Near(new Vec3(0, 0, -3), PicturePose.Of(Vec3.Zero, front, 1, 0, 0, 3, 40, 1).Position);
        Near(new Vec3(0, 0, 3), PicturePose.Of(Vec3.Zero, front, 1, 180, 0, 3, 40, 1).Position);
        // Facing south, its right is west.
        Near(new Vec3(-3, 0, 0), PicturePose.Of(Vec3.Zero, front, 1, 90, 0, 3, 40, 1).Position);
    }

    [Fact]
    public void AThingWithNoBodyKeepsItsOwnFrontAndNothingAtAllIsNorth()
    {
        Near(new Vec3(1, 0, 0), PictureFront.Of(null, new Vec3(1, 0, 0)));
        Near(PictureFront.North, PictureFront.Of(null, null));
    }

    [Fact]
    public void TheFrontIsLevelledAndStraightUpIsNorth()
    {
        Vec3 tilted = PictureFront.Of(new Vec3(3, 4, 0), null);

        Near(new Vec3(1, 0, 0), tilted);
        Near(PictureFront.North, PictureFront.Of(new Vec3(0, 1, 0), null));
        // PicturePose levels the same way, so a levelled front moves nothing.
        Near(PicturePose.Of(Vec3.Zero, new Vec3(3, 4, 0), 1, 30, 10, 2, 40, 1).Position,
            PicturePose.Of(Vec3.Zero, tilted, 1, 30, 10, 2, 40, 1).Position);
    }

    // ---- Paths ----

    [Theory]
    [InlineData(null, "suit.png", "suit.png")]
    [InlineData("suits", "suit-front.png", "suits/suit-front.png")]
    [InlineData("aegis/run 1", "Suit_Back.PNG", "aegis/run 1/Suit_Back.PNG")]
    [InlineData("a\\b", "x.png", "a/b/x.png")]
    [InlineData("", "x.png", "x.png")]
    public void APlainNameAndFolderResolveInsideTheRoot(string? folder, string file, string expected)
    {
        string? path = PicturePaths.Resolve(Root, folder, file, out string? problem);

        Assert.Null(problem);
        Assert.Equal(Path.GetFullPath(Path.Combine(Root, expected)), path);
        Assert.True(PicturePaths.IsUnder(Path.GetFullPath(Root), path!));
    }

    [Theory]
    [InlineData(null, "../escape.png")]
    [InlineData(null, "..\\escape.png")]
    [InlineData(null, "sub/x.png")]
    [InlineData(null, "C:\\Windows\\x.png")]
    [InlineData(null, "\\\\server\\share\\x.png")]
    [InlineData(null, "x.jpg")]
    [InlineData(null, ".png")]
    [InlineData(null, ".hidden.png")]
    [InlineData(null, "x.png.")]
    [InlineData(null, "x:y.png")]
    [InlineData(null, "con<1>.png")]
    [InlineData(null, "")]
    [InlineData(null, null)]
    [InlineData("..", "x.png")]
    [InlineData("a/../..", "x.png")]
    [InlineData("a/../../b", "x.png")]
    [InlineData("C:\\Windows", "x.png")]
    [InlineData("/etc", "x.png")]
    [InlineData("\\\\server\\share", "x.png")]
    [InlineData("a//b", "x.png")]
    [InlineData("a/./b", "x.png")]
    [InlineData("a/b/c/d/e", "x.png")]
    [InlineData("a\tb", "x.png")]
    public void AnythingThatIsNotPlainOrEscapesTheRootIsRefused(string? folder, string? file)
    {
        string? path = PicturePaths.Resolve(Root, folder, file, out string? problem);

        Assert.Null(path);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void ALongNameIsRefusedAndARelativeRootIsRefused()
    {
        Assert.Null(PicturePaths.Resolve(Root, null, new string('a', 97) + ".png", out _));
        Assert.NotNull(PicturePaths.Resolve(Root, null, new string('a', 96) + ".png", out _));
        Assert.Null(PicturePaths.Resolve("relative\\pictures", null, "x.png", out string? problem));
        Assert.Contains("[Pictures] Folder", problem);
    }

    [Fact]
    public void UnderNeedsASeparatorAfterTheRoot()
    {
        string root = Path.GetFullPath(Root);

        Assert.False(PicturePaths.IsUnder(root, root + "-sibling" + Path.DirectorySeparatorChar + "x.png"));
        Assert.False(PicturePaths.IsUnder(root, root));
        Assert.True(PicturePaths.IsUnder(root, Path.Combine(root, "x.png")));
        Assert.True(PicturePaths.IsUnder(root.ToUpperInvariant(), Path.Combine(root, "x.png")));
    }

    // ---- Options: the catalogue's caps and refusals ----

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("show")!, JObject.Parse(options), out _);

    [Fact]
    public void DefaultsAreTheFullHdThirdPersonPictureOfThePlayer()
    {
        JObject options = JObject.Parse("""{"what": "picture", "file": "x.png"}""");
        Assert.Null(OptionCheck.Check(VerbSurface.Verb("show")!, options, out WhatSpec? what));
        OptionReader given = new OptionReader(what!, options);
        ShowPictureOptions bound = ShowPictureOptions.Bind(given);

        Assert.Equal((1920L, 1080L), (bound.Width, bound.Height));
        Assert.Equal((0d, 10d, 40d), (bound.Yaw, bound.Pitch, bound.Fov));
        Assert.Null(bound.Distance);
        Assert.Equal("scene", bound.Lighting);
        Assert.False(bound.Overwrite);
        Assert.Equal("player", given.DefaultText("id"));
    }

    [Theory]
    [InlineData("""{"what": "picture", "file": "x.png", "width": 4097}""", "width")]
    [InlineData("""{"what": "picture", "file": "x.png", "height": 15}""", "height")]
    [InlineData("""{"what": "picture", "file": "x.png", "width": 0}""", "width")]
    [InlineData("""{"what": "picture", "file": "x.png", "width": 1920.5}""", "width")]
    [InlineData("""{"what": "picture", "file": "x.png", "fov": 130}""", "fov")]
    [InlineData("""{"what": "picture", "file": "x.png", "pitch": 90}""", "pitch")]
    [InlineData("""{"what": "picture", "file": "x.png", "distance": 0}""", "distance")]
    [InlineData("""{"what": "picture", "file": "x.png", "lighting": "studio"}""", "lighting")]
    [InlineData("""{"what": "picture"}""", "file")]
    [InlineData("""{"what": "picture", "file": "x.png", "id": ["1", "2"]}""", "id")]
    public void SizesBeyondTheCapsAndBadOptionsAreRefusedBeforeAnythingRuns(string options, string path)
    {
        VerbError error = Check(options)!;

        Assert.Equal("invalid_argument", error.Code);
        Assert.Contains(path, error.Message);
    }

    [Fact]
    public void TheLargestPictureIsAccepted()
    {
        Assert.Null(Check("""{"what": "picture", "file": "x.png", "width": 4096, "height": 4096, "lighting": "neutral"}"""));
    }

    // ---- One picture at a time ----

    private sealed class Held : IDisposable
    {
        internal int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void ASecondPictureIsRefusedUntilTheFirstIsReleased()
    {
        PictureGate gate = new PictureGate();
        object first = new object();
        object second = new object();

        Assert.True(gate.TryTake(first, 0));
        Assert.False(gate.TryTake(second, 1));
        gate.Release(second);
        Assert.True(gate.Taken);
        gate.Release(first);
        gate.Release(first);
        Assert.True(gate.TryTake(second, 2));
    }

    [Fact]
    public void AStaleHolderIsDisposedAndTheGatePasses()
    {
        PictureGate gate = new PictureGate();
        Held dropped = new Held();
        object next = new object();

        Assert.True(gate.TryTake(dropped, 100));
        Assert.False(gate.TryTake(next, 100 + PictureGate.StaleAfterSeconds - 0.1));
        Assert.Equal(0, dropped.Disposed);
        Assert.True(gate.TryTake(next, 100 + PictureGate.StaleAfterSeconds));
        Assert.Equal(1, dropped.Disposed);
        gate.Release(dropped);
        Assert.True(gate.Taken);
    }

    // ---- Neutral lighting is always put back ----

    private sealed class Scene
    {
        internal bool Sun { get; set; } = true;

        internal bool Moon { get; set; } = true;

        internal string Ambient { get; set; } = "skybox";

        internal int KeyLights { get; set; }
    }

    private static void Neutral(SceneSwap swap, Scene scene, bool throwMidway = false)
    {
        swap.Set(() => scene.Sun, value => scene.Sun = value, false);
        swap.Set(() => scene.Moon, value => scene.Moon = value, false);
        scene.KeyLights++;
        swap.Hold(() => scene.KeyLights--);
        if (throwMidway)
        {
            throw new InvalidOperationException("apply failed");
        }

        swap.Set(() => scene.Ambient, value => scene.Ambient = value, "flat");
    }

    private static void Unchanged(Scene scene)
    {
        Assert.True(scene.Sun);
        Assert.True(scene.Moon);
        Assert.Equal("skybox", scene.Ambient);
        Assert.Equal(0, scene.KeyLights);
    }

    [Fact]
    public void TheRenderSeesTheNeutralSceneAndTheSceneIsPutBackAfter()
    {
        Scene scene = new Scene();
        bool rendered = false;

        SceneSwap.Run(swap => Neutral(swap, scene), () =>
        {
            Assert.False(scene.Sun);
            Assert.False(scene.Moon);
            Assert.Equal("flat", scene.Ambient);
            Assert.Equal(1, scene.KeyLights);
            rendered = true;
        });

        Assert.True(rendered);
        Unchanged(scene);
    }

    [Fact]
    public void ARenderThatThrowsStillPutsTheLightsBack()
    {
        Scene scene = new Scene();

        Assert.Throws<InvalidOperationException>(() =>
            SceneSwap.Run(swap => Neutral(swap, scene), () => throw new InvalidOperationException("render failed")));

        Unchanged(scene);
    }

    [Fact]
    public void AnApplyThatThrowsPartWayPutsBackWhatItChangedAndNeverRenders()
    {
        Scene scene = new Scene();
        bool rendered = false;

        Assert.Throws<InvalidOperationException>(() =>
            SceneSwap.Run(swap => Neutral(swap, scene, throwMidway: true), () => rendered = true));

        Assert.False(rendered);
        Unchanged(scene);
    }

    [Fact]
    public void ARestorerThatThrowsDoesNotStopTheOthers()
    {
        Scene scene = new Scene();
        SceneSwap swap = new SceneSwap();
        swap.Set(() => scene.Sun, value => scene.Sun = value, false);
        swap.Hold(() => throw new InvalidOperationException("light destroyed"));
        swap.Set(() => scene.Moon, value => scene.Moon = value, false);

        Assert.Throws<InvalidOperationException>(swap.Restore);

        Assert.True(scene.Sun);
        Assert.True(scene.Moon);
        Assert.Equal(0, swap.Held);
        swap.Restore();
    }

    [Fact]
    public void TheFirstChangeHeldIsPutBackLast()
    {
        // The ambient probe is held before the ambient mode changes, so it is set back after the mode is.
        List<string> order = new List<string>();
        SceneSwap swap = new SceneSwap();
        swap.Hold(() => order.Add("probe"));
        swap.Set(() => "skybox", value => order.Add("mode " + value), "flat");
        swap.Set(() => "grey0", value => order.Add("light " + value), "grey");
        order.Clear();

        swap.Restore();

        Assert.Equal(new[] { "light grey0", "mode skybox", "probe" }, order);
    }

    // ---- Path gaps: device names, long paths, links ----

    [Theory]
    [InlineData(null, "CON.png")]
    [InlineData(null, "con.png")]
    [InlineData(null, "nul.png")]
    [InlineData(null, "Prn.png")]
    [InlineData(null, "aux.png")]
    [InlineData(null, "COM1.png")]
    [InlineData(null, "lpt9.png")]
    [InlineData(null, "con.suit.png")]
    [InlineData("CON", "x.png")]
    [InlineData("a/nul", "x.png")]
    [InlineData("com3.d", "x.png")]
    public void DeviceNamesAreRefused(string? folder, string file)
    {
        Assert.Null(PicturePaths.Resolve(Root, folder, file, out string? problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Theory]
    [InlineData("console.png")]
    [InlineData("com10.png")]
    [InlineData("com0.png")]
    [InlineData("lpt.png")]
    [InlineData("nullable.png")]
    public void NamesThatOnlyLookLikeDevicesAreKept(string file)
    {
        Assert.NotNull(PicturePaths.Resolve(Root, null, file, out _));
    }

    [Fact]
    public void APathTooLongForTheSystemIsRefusedNotThrown()
    {
        // Each name is within the limits; the whole is not, on a root that is already long.
        string root = "C:\\" + string.Join("\\", new string('r', 99), new string('r', 99), new string('r', 99));
        string folder = string.Join("/", new string('f', 99), new string('f', 99), new string('f', 99), new string('f', 99));

        string? path = PicturePaths.Resolve(root, folder, new string('n', 96) + ".png", out string? problem);

        // .NET 8 takes long paths; the game's runtime may not. Either it resolves inside the root or it is refused.
        if (path == null)
        {
            Assert.False(string.IsNullOrEmpty(problem));
        }
        else
        {
            Assert.True(PicturePaths.IsUnder(root, path));
        }
    }

    [Fact]
    public void AJunctionBetweenTheRootAndTheFileIsFound()
    {
        string root = Path.GetFullPath(Root);
        string full = Path.Combine(root, "a", "b", "x.png");
        HashSet<string> links = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "a", "b") };
        List<string> asked = new List<string>();

        string? found = PicturePaths.FirstReparsePoint(Root, full, dir =>
        {
            asked.Add(dir);
            return links.Contains(dir);
        });

        Assert.Equal(Path.Combine(root, "a", "b"), found);
        Assert.Equal(new[] { Path.Combine(root, "a"), Path.Combine(root, "a", "b") }, asked);
        Assert.Null(PicturePaths.FirstReparsePoint(Root, full, _ => false));
        // The file itself (a link someone made under the old name) counts too; the root never does.
        Assert.Equal(full, PicturePaths.FirstReparsePoint(Root, full, dir => dir == full));
        Assert.Null(PicturePaths.FirstReparsePoint(Root, full, dir => dir == root));
    }

    // ---- The file: written only when the call answers ----

    private static string Fresh()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sg-picture-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Settle(PictureWrite write) => Assert.True(write.Wait(TimeSpan.FromSeconds(10)));

    [Fact]
    public void TheFileAppearsOnlyAtCommit()
    {
        string folder = Fresh();
        string path = Path.Combine(folder, "x.png");
        PictureWrite write = new PictureWrite(path, false, _ => new byte[] { 1, 2, 3 });
        Settle(write);

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(write.Temporary));
        Assert.Equal(3, write.Commit());
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void ADroppedPictureLeavesNoFileWhenDroppedBeforeOrAfterTheWrite()
    {
        string folder = Fresh();
        using (ManualResetEventSlim release = new ManualResetEventSlim(false))
        {
            bool sawCancel = false;
            PictureWrite before = new PictureWrite(Path.Combine(folder, "before.png"), false, token =>
            {
                release.Wait();
                sawCancel = token.IsCancellationRequested;
                return new byte[] { 1 };
            });
            before.Dispose();
            release.Set();
            Settle(before);
            Assert.True(sawCancel, "produce is told the call was dropped");
        }

        PictureWrite after = new PictureWrite(Path.Combine(folder, "after.png"), false, _ => new byte[] { 1 });
        Settle(after);
        after.Dispose();

        SpinWait.SpinUntil(() => Directory.GetFiles(folder).Length == 0, TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Throws<InvalidOperationException>(() => after.Commit());
    }

    [Fact]
    public void ProduceIsAlwaysCalledSoItCanFreeItsInputs()
    {
        string folder = Fresh();
        int called = 0;
        PictureWrite write = new PictureWrite(Path.Combine(folder, "x.png"), false, token =>
        {
            Interlocked.Increment(ref called);
            return token.IsCancellationRequested ? null : new byte[] { 1 };
        });
        write.Dispose();
        Settle(write);

        Assert.Equal(1, called);
    }

    [Fact]
    public void ATakenNameIsRefusedUnlessOverwriteAndTheTemporaryGoes()
    {
        string folder = Fresh();
        string path = Path.Combine(folder, "x.png");
        File.WriteAllBytes(path, new byte[] { 9 });

        PictureWrite refused = new PictureWrite(path, false, _ => new byte[] { 1, 2 });
        Settle(refused);
        Assert.Throws<PictureWrite.FileExistsException>(() => refused.Commit());
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));

        PictureWrite replaced = new PictureWrite(path, true, _ => new byte[] { 1, 2 });
        Settle(replaced);
        Assert.Equal(2, replaced.Commit());
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void AFailedEncodeIsRethrownAtCommitAndLeavesNothing()
    {
        string folder = Fresh();
        PictureWrite write = new PictureWrite(Path.Combine(folder, "x.png"), false,
            _ => throw new InvalidOperationException("encode failed"));
        Settle(write);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => write.Commit());
        Assert.Equal("encode failed", error.Message);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void TheRefusalCodesAreInTheCatalogue()
    {
        foreach (string code in new List<string> { "bad_path", "file_exists", "render_failed", "busy", "not_rendered" })
        {
            Assert.True(VerbDocument.Embedded.Errors.ContainsKey(code), code);
        }
    }
}
