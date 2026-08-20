using Perch.Data;
using Xunit;

namespace Perch.Tests;

public class TodoStoreTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"perch-todo-{Guid.NewGuid():N}.json");

    [Fact]
    public void Add_Complete_Remove_RoundTripThroughDisk()
    {
        var path = TempPath();
        try
        {
            var store = TodoStore.LoadFrom(path);
            var a = store.Add("Ship release", "cut the tag", DateTime.UtcNow.AddHours(2));
            var b = store.Add("Water plants", "", null);
            store.Save();

            var reloaded = TodoStore.LoadFrom(path);
            Assert.Equal(2, reloaded.All().Count);

            Assert.True(reloaded.Complete(a.Id));
            Assert.False(reloaded.Complete("nope"));
            Assert.True(reloaded.Remove(b.Id));
            reloaded.Save();

            var again = TodoStore.LoadFrom(path);
            Assert.Single(again.All());
            var only = again.All()[0];
            Assert.Equal("Ship release", only.Title);
            Assert.True(only.Completed);
            Assert.NotNull(only.CompletedUtc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Update_ReplacesById_LeavesOthersUntouched()
    {
        var path = TempPath();
        try
        {
            var store = TodoStore.LoadFrom(path);
            var a = store.Add("Draft", "", null);
            store.Add("Other", "", null);

            var edit = a.Clone();
            edit.Title = "Final";
            edit.DueUtc = DateTime.UtcNow.AddDays(1);
            Assert.True(store.Update(edit));

            var reloaded = store.All().First(t => t.Id == a.Id);
            Assert.Equal("Final", reloaded.Title);
            Assert.NotNull(reloaded.DueUtc);
            Assert.Equal(2, store.All().Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reopen_ClearsCompletion_AndReturnsItToOutstanding()
    {
        var store = TodoStore.LoadFrom(TempPath());
        var a = store.Add("Ship it", "", null);
        store.Complete(a.Id);
        Assert.True(a.Completed);

        Assert.True(store.Reopen(a.Id));
        Assert.False(a.Completed);
        Assert.Null(a.CompletedUtc);
        Assert.Contains(store.TopOutstanding(5), t => t.Id == a.Id);
        Assert.False(store.Reopen("nope"));
    }

    [Fact]
    public void TopOutstanding_OrdersOverdueFirst_UndatedLast_ExcludesCompleted()
    {
        var store = TodoStore.LoadFrom(TempPath());
        var now = DateTime.UtcNow;
        var undated = store.Add("Undated", "", null);
        var soon = store.Add("Soon", "", now.AddMinutes(10));
        var overdue = store.Add("Overdue", "", now.AddMinutes(-30));
        var later = store.Add("Later", "", now.AddDays(3));
        var done = store.Add("Done", "", now.AddMinutes(-5));
        store.Complete(done.Id);

        var top = store.TopOutstanding(10);

        Assert.Equal(new[] { overdue.Id, soon.Id, later.Id, undated.Id }, top.Select(t => t.Id).ToArray());
        Assert.DoesNotContain(top, t => t.Id == done.Id);
    }

    [Fact]
    public void DueForReminder_SelectsOnlyPastDue_Unfired_Incomplete()
    {
        var store = TodoStore.LoadFrom(TempPath());
        var now = DateTime.UtcNow;
        var due = store.Add("Due", "", now.AddMinutes(-1));
        store.Add("Future", "", now.AddHours(1));
        store.Add("NoDate", "", null);
        var alreadyFired = store.Add("Fired", "", now.AddMinutes(-2));
        alreadyFired.ReminderFiredUtc = now.AddMinutes(-2);
        var completed = store.Add("Done", "", now.AddMinutes(-3));
        store.Complete(completed.Id);

        var picked = TodoStore.DueForReminder(store.All(), now).ToList();

        Assert.Single(picked);
        Assert.Equal(due.Id, picked[0].Id);

        // Once stamped, a second pass selects nothing (the de-dupe the monitor host relies on).
        due.ReminderFiredUtc = now;
        Assert.Empty(TodoStore.DueForReminder(store.All(), now));
    }
}
