using Perch.Data;
using Xunit;

namespace Perch.Tests;

/// <summary>
/// The session host skips posting a scan to the UI when <see cref="SessionListComparer"/> says nothing changed, so a
/// "same" answer must mean every field the UI reads is equal, including the list fields a scan rebuilds each time.
/// </summary>
public sealed class SessionListComparerTests
{
    private static readonly DateTime T = new(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc);

    // A fresh session with fresh lists each call, as a scan produces.
    private static ClaudeSession Make(string activity = "Reading Foo.cs", string agentActivity = "Searching",
        TaskState task = TaskState.InProgress, string? childActivity = null) =>
        new("100", "s1", SessionStatus.Running, @"C:\src\perch", "perch", T,
            SubAgents: [new SubAgent("a1", "Explore", "Explore", Activity: agentActivity,
                Children: childActivity is null ? null : [new SubAgent("c1", "Child", "Explore", Activity: childActivity)])],
            Activity: activity,
            Artifacts: [new Artifact("https://claude.ai/artifact/x", "X")],
            Tasks: [new TaskItem("Do it", "Doing it", task)])
        {
            BackgroundTasks = [new RunningBackgroundTask("t1", BackgroundTaskKind.Shell, T, null)],
        };

    [Fact]
    public void FreshListsWithTheSameContentAreTheSame() =>
        Assert.True(SessionListComparer.Same([Make(childActivity: "Reading")], [Make(childActivity: "Reading")]));

    [Fact]
    public void NoPreviousScanIsNeverTheSame() => Assert.False(SessionListComparer.Same(null, [Make()]));

    [Fact]
    public void AScalarFieldDiffers() =>
        Assert.False(SessionListComparer.Same([Make()], [Make(activity: "Editing Bar.cs")]));

    [Fact]
    public void ASubAgentDiffers() =>
        Assert.False(SessionListComparer.Same([Make()], [Make(agentActivity: "Reading")]));

    [Fact]
    public void ANestedSubAgentDiffers() =>
        Assert.False(SessionListComparer.Same([Make(childActivity: "Reading")], [Make(childActivity: "Writing")]));

    [Fact]
    public void ATaskDiffers() =>
        Assert.False(SessionListComparer.Same([Make()], [Make(task: TaskState.Completed)]));

    [Fact]
    public void ABackgroundTaskDiffers() =>
        Assert.False(SessionListComparer.Same([Make()], [Make() with { BackgroundTasks = [] }]));

    [Fact]
    public void AnInitOnlyFieldDiffers() =>
        Assert.False(SessionListComparer.Same([Make()], [Make() with { IsDormant = true }]));

    [Fact]
    public void ASessionAddedOrReorderedDiffers()
    {
        var other = Make() with { Pid = "200", SessionId = "s2" };
        Assert.False(SessionListComparer.Same([Make()], [Make(), other]));
        Assert.False(SessionListComparer.Same([Make(), other], [other, Make()]));
    }
}
