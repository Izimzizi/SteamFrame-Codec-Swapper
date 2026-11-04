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
        public DashboardHeal Heal => new(_ => { Calls.Add("ask"); return Task.FromResult(Replies.Dequeue()); }, () => { Calls.Add("stop"); return Task.FromResult(true); },
            () => { Calls.Add("start"); return Task.CompletedTask; }, Log.Add, (_, _) => Task.CompletedTask);
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
    public async Task No_stream_means_nothing_to_heal()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(0, 0));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask"], rig.Calls);
    }

    [Fact]
    public async Task A_stream_without_PC_frames_is_restarted_once_and_checked_again()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 3));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "stop", "start", "ask"], rig.Calls);
        Assert.Contains(rig.Log, l => l.StartsWith("HEAL worked"));
    }

    [Fact]
    public async Task A_restart_that_does_not_help_is_not_repeated()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0)); rig.Replies.Enqueue(Reply(1, 0));
        await rig.Heal.RunAsync(CancellationToken.None);
        Assert.Equal(["ask", "stop", "start", "ask"], rig.Calls);
        Assert.Contains(rig.Log, l => l.StartsWith("HEAL did not help"));
    }

    [Fact]
    public async Task An_unreachable_Frame_changes_nothing()
    {
        var calls = new List<string>(); var log = new List<string>();
        var heal = new DashboardHeal(_ => throw new IOException("no Frame"), () => { calls.Add("stop"); return Task.FromResult(true); }, () => { calls.Add("start"); return Task.CompletedTask; }, log.Add, (_, _) => Task.CompletedTask);
        await heal.RunAsync(CancellationToken.None);
        Assert.Empty(calls);
        Assert.Contains(log, l => l.Contains("could not be asked"));
    }

    [Fact]
    public async Task A_newer_switch_cancels_the_check_before_it_asks()
    {
        var rig = new Rig(); rig.Replies.Enqueue(Reply(1, 0));
        using var source = new CancellationTokenSource();
        var heal = new DashboardHeal(_ => { rig.Calls.Add("ask"); return Task.FromResult(rig.Replies.Dequeue()); }, () => Task.FromResult(true), () => Task.CompletedTask, rig.Log.Add,
            (_, token) => { source.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAsync<OperationCanceledException>(() => heal.RunAsync(source.Token));
        Assert.Empty(rig.Calls);
    }
}
