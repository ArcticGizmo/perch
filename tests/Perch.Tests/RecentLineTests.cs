using Perch.Data;
using Perch.Data.Roost;
using Xunit;

namespace Perch.Tests;

/// <summary>The one Recent line both Recent lists show (the overlay's button and the Roost rail's row): its note, tone and
/// filters from a dormant session, and the seen-tracking behind the Recent badge.</summary>
public class RecentLineTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0);

    private static RoostDormant Dormant(RoostDormantKind kind, TimeSpan ago, string? title = null, bool beforeShutdown = false) =>
        new("s1", @"C:\src\gateway", "gateway", title, Now - ago, kind, JustBeforeShutdown: beforeShutdown);

    [Fact]
    public void ARestartsVictim_IsInterruptedAndBeforeShutdown_WithTheSpanInItsNote()
    {
        var line = RecentLine.From(Dormant(RoostDormantKind.Interrupted, TimeSpan.FromHours(14), beforeShutdown: true), Now);

        Assert.Equal("interrupted · 14h", line.Note);
        Assert.Equal(RecentTone.Flagged, line.Tone);
        Assert.True(line.Interrupted);
        Assert.True(line.BeforeShutdown);
        Assert.True(line.Badged);
    }

    [Fact]
    public void WhatPerchHadOpen_CountsAsInterrupted()
    {
        var line = RecentLine.From(Dormant(RoostDormantKind.WasOpenInPerch, TimeSpan.FromHours(1)), Now);

        Assert.Equal("was open", line.Note);
        Assert.Equal(RecentTone.Perch, line.Tone);
        Assert.True(line.Interrupted);
        Assert.False(line.BeforeShutdown);
    }

    [Theory]
    [InlineData(RoostDormantKind.BeforeShutdown, "before shutdown · 3m", RecentTone.Flagged)]
    [InlineData(RoostDormantKind.Ended, "3m ago", RecentTone.Normal)]
    [InlineData(RoostDormantKind.Exited, "3m ago", RecentTone.Faded)]
    [InlineData(RoostDormantKind.NotRunning, "not running", RecentTone.Normal)]
    internal void NoteAndTone_FollowTheKind(RoostDormantKind kind, string note, RecentTone tone)
    {
        var line = RecentLine.From(Dormant(kind, TimeSpan.FromMinutes(3)), Now);

        Assert.Equal(note, line.Note);
        Assert.Equal(tone, line.Tone);
        Assert.Equal(kind == RoostDormantKind.BeforeShutdown, line.BeforeShutdown);
        Assert.False(line.Interrupted);
    }

    [Fact]
    public void ATitledSession_KeepsItsFolderBeside_AnUntitledOneIsItsFolder()
    {
        var titled = RecentLine.From(Dormant(RoostDormantKind.Ended, TimeSpan.FromHours(2), "  Retry storm fix "), Now);
        var untitled = RecentLine.From(Dormant(RoostDormantKind.Ended, TimeSpan.FromHours(2)), Now);

        Assert.Equal(("Retry storm fix", "gateway"), (titled.Title, titled.Folder));
        Assert.Equal(("gateway", (string?)null), (untitled.Title, untitled.Folder));
    }

    [Theory]
    [InlineData(30, "just now", "just now")]
    [InlineData(12 * 60, "12m ago", "12m")]
    [InlineData(47 * 3600, "47h ago", "47h")]
    [InlineData(3 * 86400, "3d ago", "3d")]
    public void AgeLabels(int secondsAgo, string ago, string span)
    {
        var at = Now.AddSeconds(-secondsAgo);
        Assert.Equal(ago, RelativeTime.Ago(Now, at));
        Assert.Equal(span, RelativeTime.Span(Now, at));
    }

    [Fact]
    public void TheBadge_LightsForAnUnseenBadgedLine_AndStaysOutOnceSeen()
    {
        var seen = new RecentSeen();
        var interrupted = RecentLine.From(Dormant(RoostDormantKind.Interrupted, TimeSpan.FromHours(1)), Now);
        var ordinary = RecentLine.From(Dormant(RoostDormantKind.Ended, TimeSpan.FromHours(1)) with { SessionId = "s2" }, Now);

        Assert.False(seen.HasUnseen([ordinary]));
        Assert.True(seen.HasUnseen([ordinary, interrupted]));
        seen.MarkSeen([ordinary, interrupted]);
        Assert.False(seen.HasUnseen([ordinary, interrupted]));
    }
}
