using System.Text.Json;
using CodecSwapper;

namespace CodecSwapperTests;

public sealed class DashboardHealTests
{
    private static string Reply(int streams, int remote)
    {
        var frames = new List<object> { new { id = 1, remote = 0 } };
        for (var i = 0; i < remote; i++) frames.Add(new { id = 10 + i, remote = 1 });
        var inner = JsonSerializer.Serialize(new { frames, active = 1, streams = Enumerable.Range(0, streams).Select(_ => new[] { "250820", "0" }), overlays = 0 });
        return JsonSerializer.Serialize(inner);   // the Frame returns the JSON text as a string value
    }

    private sealed class Rig
    {
        public readonly Queue<string> Replies = new();
        public readonly List<string> Calls = [];
        public readonly List<string> Log = [];
        public DashboardHeal Heal => new(_ => { Calls.Add("ask"); return Task.FromResult(Replies.Dequeue()); }, () => { Calls.Add("nudge"); return Task.FromResult(0); }, () => Calls.Add("failed"), Log.Add, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public void The_Frames_reply_is_read_as_streams_and_PC_frames()
    {
        Assert.Equal((1, 3), DashboardHeal.Parse(Reply(1, 3)));
        Assert.Equal((1, 0), DashboardHeal.Parse(Reply(1, 0)));
        Assert.Equal((0, 0), DashboardHeal.Parse(Reply(0, 0)));
        Assert.Null(DashboardHeal.Parse("\"error TypeError\""));
        Assert.Null(DashboardHeal.Parse("not json"));
    }

    [Fact]
    public async Task A_dashboard_with_its_PC_frames_is_left_alone()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 3));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask"], rig.Calls);
    }

    [Fact]
    public async Task No_stream_means_nothing_to_report()
    {
        var rig = new Rig(); for (var i = 0; i < 20; i++) rig.Replies.Enqueue(Reply(0, 0));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.All(rig.Calls, call => Assert.Equal("ask", call));
        Assert.DoesNotContain("nudge", rig.Calls);
        Assert.InRange(rig.Calls.Count, 2, 20);   // it keeps looking for a stream for a while, then gives up
    }

    [Fact]
    public async Task A_stream_without_PC_frames_is_nudged_once_and_checked_again()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 3));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "ask", "nudge", "ask"], rig.Calls);
        Assert.Contains(rig.Log, l => l.StartsWith("HEAL worked"));
    }

    [Fact]
    public async Task A_nudge_that_does_not_help_is_reported_and_not_repeated()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 0));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "ask", "nudge", "ask", "failed"], rig.Calls);
        Assert.Contains(rig.Log, l => l.StartsWith("HEAL did not help"));
    }

    [Fact]
    public async Task PC_frames_that_arrive_while_it_confirms_are_not_nudged()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 3));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "ask"], rig.Calls);
    }

    [Fact]
    public async Task A_stream_that_starts_late_is_found_by_asking_again()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(0, 0)); rig.Replies.Enqueue(Reply(0, 0)); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 3));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "ask", "ask", "ask", "nudge", "ask"], rig.Calls);
    }

    [Fact]
    public async Task An_unreachable_Frame_reports_nothing()
    {
        var calls = new List<string>(); var log = new List<string>();
        var heal = new DashboardHeal(_ => throw new IOException("no Frame"), () => { calls.Add("nudge"); return Task.FromResult(0); }, () => calls.Add("failed"), log.Add, (_, _) => Task.CompletedTask);
        await heal.RunAsync(CancellationToken.None);
        Assert.Empty(calls);
        Assert.Contains(log, l => l.Contains("could not be asked"));
    }

    [Fact]
    public async Task A_newer_switch_cancels_the_check_before_it_asks()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0));
        using var source = new CancellationTokenSource();
        var heal = new DashboardHeal(_ => { rig.Calls.Add("ask"); return Task.FromResult(rig.Replies.Dequeue()); }, () => Task.FromResult(0), () => rig.Calls.Add("failed"), rig.Log.Add,
            (_, token) => { source.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => heal.RunAsync(source.Token));
        Assert.Empty(rig.Calls);
    }
}
