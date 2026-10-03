#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The remote view: what a client's StationGod sends the server and what the server sends back to draw, byte for
/// byte; when a client sends; when a held view is no longer trusted; and that nothing is sent to a game that has not
/// shown it speaks the same protocol.
/// </summary>
public sealed class RemoteViewTests
{
    private static readonly Vec3 Eye = new Vec3(10.5, 2.25, -3.75);
    private static readonly Vec3 North = new Vec3(0, 0, 1);
    private static readonly Vec3 Up = new Vec3(0, 1, 0);
    private static readonly Vec3 Standing = new Vec3(10.5, 0.5, -3.75);

    private static ViewReport Report(int sequence = 1, Vec3? eye = null, Vec3? forward = null, ViewTarget? target = null,
        ViewHit? hit = null, Vec3? player = null, int session = 7, bool thirdPerson = false, bool seated = false) =>
        new ViewReport(session, sequence, eye ?? Eye, forward ?? North, Up, thirdPerson, seated, player ?? Standing,
            target, hit);

    private static ViewHit Wall(double z = 2.0, long thing = 4242) =>
        new ViewHit(new Vec3(10.5, 2.25, z), new Vec3(0, 0, -1), 5.75, thing);

    // ---- ViewWire ----

    [Fact]
    public void AViewWithTargetAndHitRoundTripsExactly()
    {
        ViewReport sent = Report(sequence: 41, target: new ViewTarget(123456789012L, 3), hit: Wall(),
            thirdPerson: true, seated: true);

        byte[] bytes = ViewWire.Encode(sent);
        ViewReport read = Assert.IsType<WireRead<ViewReport>.Read>(ViewWire.Decode(bytes)).Value;

        Assert.Equal(ViewWire.BaseLength + ViewWire.TargetLength + ViewWire.HitLength, bytes.Length);
        Assert.Equal(106, bytes.Length);
        Assert.Equal(sent.Session, read.Session);
        Assert.Equal(41, read.Sequence);
        Assert.Equal(sent.Eye, read.Eye);
        Assert.Equal(sent.Forward, read.Forward);
        Assert.Equal(sent.Up, read.Up);
        Assert.True(read.ThirdPerson);
        Assert.True(read.Seated);
        Assert.Equal(sent.PlayerPosition, read.PlayerPosition);
        Assert.Equal(123456789012L, read.Target!.ThingId);
        Assert.Equal(3, read.Target.InteractableId);
        Assert.Equal(sent.Hit!.Point, read.Hit!.Point);
        Assert.Equal(sent.Hit.Normal, read.Hit.Normal);
        Assert.Equal(5.75, read.Hit.DistanceM);
        Assert.Equal(4242L, read.Hit.ThingId);
    }

    [Fact]
    public void AViewWithNothingUnderTheCursorIs58Bytes()
    {
        byte[] bytes = ViewWire.Encode(Report());
        ViewReport read = Assert.IsType<WireRead<ViewReport>.Read>(ViewWire.Decode(bytes)).Value;

        Assert.Equal(58, bytes.Length);
        Assert.Null(read.Target);
        Assert.Null(read.Hit);
        Assert.False(read.ThirdPerson);
    }

    [Fact]
    public void ATargetWithoutAnInteractableKeepsNone()
    {
        byte[] bytes = ViewWire.Encode(Report(target: new ViewTarget(99, null)));
        ViewReport read = Assert.IsType<WireRead<ViewReport>.Read>(ViewWire.Decode(bytes)).Value;

        Assert.Equal(70, bytes.Length);
        Assert.Equal(99L, read.Target!.ThingId);
        Assert.Null(read.Target.InteractableId);
    }

    [Fact]
    public void ATerrainHitCarriesThingZero()
    {
        byte[] bytes = ViewWire.Encode(Report(hit: Wall(thing: 0)));

        Assert.Equal(94, bytes.Length);
        Assert.Equal(0L, Assert.IsType<WireRead<ViewReport>.Read>(ViewWire.Decode(bytes)).Value.Hit!.ThingId);
    }

    [Fact]
    public void EveryTruncationOfAViewIsRefused()
    {
        byte[] bytes = ViewWire.Encode(Report(target: new ViewTarget(1, 0), hit: Wall()));

        for (int length = 0; length < bytes.Length; length++)
        {
            byte[] cut = bytes.AsSpan(0, length).ToArray();
            Assert.IsType<WireRead<ViewReport>.Malformed>(ViewWire.Decode(cut));
        }
    }

    [Fact]
    public void BytesPastAViewsEndAreRefused()
    {
        byte[] bytes = ViewWire.Encode(Report());
        byte[] longer = new byte[bytes.Length + 1];
        bytes.CopyTo(longer, 0);

        Assert.IsType<WireRead<ViewReport>.Malformed>(ViewWire.Decode(longer));
    }

    [Fact]
    public void AViewOfAnotherProtocolIsNotRead()
    {
        byte[] bytes = ViewWire.Encode(Report());
        bytes[0] = ViewProtocol.Current + 1;

        WireRead<ViewReport>.OtherProtocol other = Assert.IsType<WireRead<ViewReport>.OtherProtocol>(ViewWire.Decode(bytes));
        Assert.Equal(ViewProtocol.Current + 1, other.Protocol);
    }

    // ---- DrawWire ----

    private static readonly Rgba Green = new Rgba(0.2f, 1f, 0.3f, 1f);

    [Fact]
    public void PreviewBoxesRoundTripExactly()
    {
        PreviewBox box = new PreviewBox(new Box3(new Vec3(1, 2, 3), new Vec3(1.5, 2.5, 3.5)), Green, 30f, "footprint",
            true);
        DrawCommand sent = new DrawCommand.Previews(true, new List<PreviewBox> { box });

        byte[] bytes = DrawWire.Encode(sent);
        DrawCommand.Previews read =
            Assert.IsType<DrawCommand.Previews>(Assert.IsType<WireRead<DrawCommand>.Read>(DrawWire.Decode(bytes)).Value);

        // protocol, kind, replace, count; then min, max, colour, seconds, name (2 + 9), xray
        Assert.Equal(1 + 1 + 1 + 4 + 12 + 12 + 16 + 4 + 2 + 9 + 1, bytes.Length);
        Assert.True(read.Replace);
        PreviewBox back = Assert.Single(read.Boxes);
        Assert.Equal(box.Box.Min, back.Box.Min);
        Assert.Equal(box.Box.Max, back.Box.Max);
        Assert.Equal(Green, back.Color);
        Assert.Equal(30f, back.Seconds);
        Assert.Equal("footprint", back.Name);
        Assert.True(back.XRay);
    }

    [Fact]
    public void HighlightMarksRoundTripExactly()
    {
        DrawCommand sent = new DrawCommand.Highlights(false, new List<MarkSpec>
        {
            new MarkSpec.Point(new Vec3(100, 5, -20), Green, "the pump", true, 60f),
            new MarkSpec.Things(new List<long> { 11, 22, long.MaxValue }, new Rgba(1, 0, 1, 1), null, false, 60f)
        });

        byte[] bytes = DrawWire.Encode(sent);
        DrawCommand.Highlights read =
            Assert.IsType<DrawCommand.Highlights>(Assert.IsType<WireRead<DrawCommand>.Read>(DrawWire.Decode(bytes)).Value);

        int point = 1 + 16 + 1 + 4 + (2 + 8) + 12;
        int things = 1 + 16 + 1 + 4 + 2 + 4 + 3 * 8;
        Assert.Equal(7 + point + things, bytes.Length);
        Assert.False(read.Replace);
        MarkSpec.Point pointBack = Assert.IsType<MarkSpec.Point>(read.Marks[0]);
        Assert.Equal(new Vec3(100, 5, -20), pointBack.At);
        Assert.Equal("the pump", pointBack.Label);
        Assert.True(pointBack.Pulse);
        MarkSpec.Things thingsBack = Assert.IsType<MarkSpec.Things>(read.Marks[1]);
        Assert.Equal(new long[] { 11, 22, long.MaxValue }, thingsBack.Ids);
        Assert.Null(thingsBack.Label);
        Assert.Equal(new Rgba(1, 0, 1, 1), thingsBack.Tint);
        Assert.Equal(60f, thingsBack.Seconds);
    }

    [Fact]
    public void AClearIsAReplaceWithNothing()
    {
        byte[] bytes = DrawWire.Encode(new DrawCommand.Highlights(true, new List<MarkSpec>()));
        DrawCommand read = Assert.IsType<WireRead<DrawCommand>.Read>(DrawWire.Decode(bytes)).Value;

        Assert.Equal(7, bytes.Length);
        Assert.True(read.Replace);
        Assert.Empty(Assert.IsType<DrawCommand.Highlights>(read).Marks);
    }

    [Fact]
    public void EveryTruncationOfADrawingIsRefused()
    {
        byte[] bytes = DrawWire.Encode(new DrawCommand.Highlights(false, new List<MarkSpec>
        {
            new MarkSpec.Things(new List<long> { 1, 2 }, Green, "pipes", false, 5f),
            new MarkSpec.Point(new Vec3(0, 0, 0), Green, null, false, 5f)
        }));

        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.IsType<WireRead<DrawCommand>.Malformed>(DrawWire.Decode(bytes.AsSpan(0, length).ToArray()));
        }
    }

    [Fact]
    public void ACountPastItsCapIsRefusedWithoutReadingOn()
    {
        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        writer.Byte(2);
        writer.Bool(false);
        writer.Int32(DrawWire.MaximumMarks + 1);

        Assert.IsType<WireRead<DrawCommand>.Malformed>(DrawWire.Decode(writer.ToArray()));
    }

    [Fact]
    public void ADrawingOfAnotherProtocolIsNotRead()
    {
        byte[] bytes = DrawWire.Encode(new DrawCommand.Previews(true, new List<PreviewBox>()));
        bytes[0] = 9;

        Assert.Equal(9, Assert.IsType<WireRead<DrawCommand>.OtherProtocol>(DrawWire.Decode(bytes)).Protocol);
    }

    [Fact]
    public void ALongTextIsCutToWholeCharacters()
    {
        WireWriter writer = new WireWriter();
        writer.Text(new string('é', 400));
        WireReader reader = new WireReader(writer.ToArray());

        string? text = reader.Text();

        Assert.True(reader.Complete);
        Assert.Equal(new string('é', WireWriter.MaximumTextBytes / 2), text);
    }

    // ---- ViewChangeRule ----

    [Fact]
    public void TheFirstViewIsSentAtOnce()
    {
        Assert.True(ViewChangeRule.ShouldSend(null, 0.0, Report()));
    }

    [Fact]
    public void NothingIsSentMoreOftenThanEveryTenthOfASecond()
    {
        ViewReport moved = Report(eye: Eye + new Vec3(1, 0, 0));

        Assert.False(ViewChangeRule.MayTake(0.05));
        Assert.False(ViewChangeRule.ShouldSend(Report(), 0.09, moved));
        Assert.True(ViewChangeRule.MayTake(0.1));
        Assert.True(ViewChangeRule.ShouldSend(Report(), 0.1, moved));
    }

    [Fact]
    public void AnUnchangedViewWaitsForTheHeartbeat()
    {
        Assert.False(ViewChangeRule.ShouldSend(Report(), 0.99, Report()));
        Assert.True(ViewChangeRule.ShouldSend(Report(), 1.0, Report()));
    }

    [Fact]
    public void ATurnCountsPastOneDegree()
    {
        Vec3 half = new Vec3(Math.Sin(0.5 * Math.PI / 180), 0, Math.Cos(0.5 * Math.PI / 180));
        Vec3 two = new Vec3(Math.Sin(2 * Math.PI / 180), 0, Math.Cos(2 * Math.PI / 180));

        Assert.False(ViewChangeRule.Changed(Report(), Report(forward: half)));
        Assert.True(ViewChangeRule.Changed(Report(), Report(forward: two)));
    }

    [Fact]
    public void TheEyeCountsPastFiveCentimetres()
    {
        Assert.False(ViewChangeRule.Changed(Report(), Report(eye: Eye + new Vec3(0.04, 0, 0))));
        Assert.True(ViewChangeRule.Changed(Report(), Report(eye: Eye + new Vec3(0.06, 0, 0))));
    }

    [Fact]
    public void AnotherTargetOrInteractableCounts()
    {
        ViewReport onButton = Report(target: new ViewTarget(5, 1));

        Assert.True(ViewChangeRule.Changed(Report(), onButton));
        Assert.True(ViewChangeRule.Changed(onButton, Report(target: new ViewTarget(5, 2))));
        Assert.True(ViewChangeRule.Changed(onButton, Report(target: new ViewTarget(6, 1))));
        Assert.False(ViewChangeRule.Changed(onButton, Report(target: new ViewTarget(5, 1))));
    }

    [Fact]
    public void TheHitCountsByThingCellAndFaceNotByDrift()
    {
        ViewReport hit = Report(hit: Wall());
        ViewHit drift = new ViewHit(new Vec3(10.6, 2.2, 2.0), new Vec3(0, 0, -1), 5.8, 4242);
        ViewHit nextCell = new ViewHit(new Vec3(11.0, 2.25, 2.0), new Vec3(0, 0, -1), 5.8, 4242);
        ViewHit floor = new ViewHit(new Vec3(10.5, 2.25, 2.0), new Vec3(0, 1, 0), 5.8, 4242);

        Assert.False(ViewChangeRule.Changed(hit, Report(hit: drift)));
        Assert.True(ViewChangeRule.Changed(hit, Report(hit: nextCell)));
        Assert.True(ViewChangeRule.Changed(hit, Report(hit: floor)));
        Assert.True(ViewChangeRule.Changed(hit, Report(hit: Wall(thing: 7))));
        Assert.True(ViewChangeRule.Changed(hit, Report()));
    }

    [Fact]
    public void TheCameraModeCounts()
    {
        Assert.True(ViewChangeRule.Changed(Report(), Report(thirdPerson: true)));
        Assert.True(ViewChangeRule.Changed(Report(), Report(seated: true)));
    }

    // ---- ViewFreshness ----

    [Fact]
    public void APlayerWhoMovedMoreThan30CentimetresMakesTheViewStale()
    {
        Assert.IsType<ViewFreshness.Fresh>(ViewFreshness.Judge(Standing, Standing + new Vec3(0.29, 0, 0), 0.2));
        ViewFreshness.Stale stale =
            Assert.IsType<ViewFreshness.Stale>(ViewFreshness.Judge(Standing, Standing + new Vec3(0.31, 0, 0), 0.2));
        Assert.Contains("0.31 m", stale.Reason);
    }

    [Fact]
    public void AnOldViewOfAPlayerWhoStoodStillStaysFresh()
    {
        Assert.IsType<ViewFreshness.Fresh>(ViewFreshness.Judge(Standing, Standing, 600.0));
        Assert.IsType<ViewFreshness.Fresh>(ViewFreshness.Judge(Standing, Standing + new Vec3(0.04, 0, 0), 600.0));
    }

    [Fact]
    public void AnOldViewOfAPlayerWhoMovedAtAllIsStale()
    {
        Assert.IsType<ViewFreshness.Fresh>(ViewFreshness.Judge(Standing, Standing + new Vec3(0.1, 0, 0), 5.0));
        Assert.IsType<ViewFreshness.Stale>(ViewFreshness.Judge(Standing, Standing + new Vec3(0.1, 0, 0), 5.1));
    }

    // ---- ViewBook ----

    [Fact]
    public void AnOlderSequenceIsIgnored()
    {
        ViewBook<long> book = new ViewBook<long>();

        Assert.True(book.Offer(1, Report(sequence: 5), now: 10));
        Assert.False(book.Offer(1, Report(sequence: 4), now: 11));
        Assert.False(book.Offer(1, Report(sequence: 5), now: 11));

        ReceivedView held = book.Find(1)!;
        Assert.Equal(5, held.Report.Sequence);
        Assert.Equal(10, held.ReceivedAt);
        Assert.Equal(2.5, held.AgeAt(12.5));
    }

    [Fact]
    public void ANewerSequenceReplacesTheView()
    {
        ViewBook<long> book = new ViewBook<long>();
        book.Offer(1, Report(sequence: 5), now: 10);

        Assert.True(book.Offer(1, Report(sequence: 6), now: 12));
        Assert.Equal(6, book.Find(1)!.Report.Sequence);
    }

    [Fact]
    public void AClientThatRestartedIsTakenWhateverItsSequence()
    {
        ViewBook<long> book = new ViewBook<long>();
        book.Offer(1, Report(sequence: 500, session: 7), now: 10);

        Assert.True(book.Offer(1, Report(sequence: 1, session: 8), now: 11));
        Assert.Equal(8, book.Find(1)!.Report.Session);
    }

    [Fact]
    public void PlayersAreKeptApart()
    {
        ViewBook<long> book = new ViewBook<long>();
        book.Offer(1, Report(sequence: 9), now: 10);

        Assert.True(book.Offer(2, Report(sequence: 1), now: 10));
        Assert.True(book.Remove(1));
        Assert.Null(book.Find(1));
        Assert.Equal(1, book.Find(2)!.Report.Sequence);
    }

    // ---- the compatibility gate ----

    [Fact]
    public void WithoutAnAnnouncementNothingIsSent()
    {
        Assert.False(HostLink.None.MaySend);
    }

    [Fact]
    public void AnAnnouncementOfTheSameProtocolOpensTheLink()
    {
        byte[] bytes = Announcement.Encode(new Announcement(ViewProtocol.Current, "1.12.0"));
        HostLink link = HostLink.From(Announcement.Decode(bytes)!);

        Assert.True(link.MaySend);
        Assert.Equal("1.12.0", Assert.IsType<HostLink.Compatible>(link).Version);
    }

    [Fact]
    public void AnAnnouncementOfAnotherProtocolKeepsItClosedAndSaysWhy()
    {
        byte[] bytes = Announcement.Encode(new Announcement(ViewProtocol.Current + 1, "2.0.0"));
        HostLink link = HostLink.From(Announcement.Decode(bytes)!);

        Assert.False(link.MaySend);
        string line = Assert.IsType<HostLink.Incompatible>(link).Describe("1.12.0");
        Assert.Contains("2.0.0", line);
        Assert.Contains($"protocol {ViewProtocol.Current + 1}", line);
    }

    [Fact]
    public void AnAnnouncementThatDoesNotReadIsNone()
    {
        byte[] bytes = Announcement.Encode(new Announcement(ViewProtocol.Current, "1.12.0"));

        Assert.Null(Announcement.Decode(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
        Assert.Null(Announcement.Decode(Array.Empty<byte>()));
    }
}
