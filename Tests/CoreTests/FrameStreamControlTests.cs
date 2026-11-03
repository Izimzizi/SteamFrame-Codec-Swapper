using System.Text;
using SteamFrame.Core;

namespace CoreTests;

public sealed class FrameStreamControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "framestream-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _calls = [];
    private string _users = "[]";
    private string _stopReply = "{\"requested\": true, \"action\": \"stop-direct-vr\"}";

    public FrameStreamControlTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "stop_stream.js"), "STOP()");
        File.WriteAllText(Path.Combine(_dir, "start_stream.js"), "START()");
        File.WriteAllText(Path.Combine(_dir, "frame_cef_stdio.py"), "expression = base64('__EXPRESSION_BASE64__')");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FrameStreamControl Control() => new(Run, _dir, (_, _) => { _calls.Add("settle"); return Task.CompletedTask; });

    private Task<string> Run(string script, CancellationToken token)
    {
        if (script == FrameStreamControl.EncoderUsersScript) { _calls.Add("users"); return Task.FromResult(_users); }
        var expression = Encoding.UTF8.GetString(Convert.FromBase64String(script.Split('\'')[1]));
        _calls.Add(expression);
        return Task.FromResult(expression == "STOP()" ? _stopReply : "{\"requested\": true, \"action\": \"start-direct-vr\"}");
    }

    [Fact]
    public async Task Without_another_encoder_nothing_is_touched_and_the_caller_changes_live()
    {
        var ran = false;
        Assert.Null(await Control().ChangeWithRestartAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
        Assert.Equal(["users"], _calls);
    }

    [Fact]
    public async Task With_another_encoder_the_stream_stops_settles_changes_and_starts_again()
    {
        _users = "[\"framecorder\"]";
        var users = await Control().ChangeWithRestartAsync(() => { _calls.Add("change"); return Task.CompletedTask; });
        Assert.Equal(["framecorder"], users);
        Assert.Equal(["users", "STOP()", "settle", "change", "START()"], _calls);
    }

    [Fact]
    public async Task The_stream_comes_back_even_when_the_change_fails()
    {
        _users = "[\"framecorder\"]";
        await Assert.ThrowsAsync<IOException>(() => Control().ChangeWithRestartAsync(() => throw new IOException("settings")));
        Assert.Equal("START()", _calls[^1]);
    }

    [Fact]
    public async Task A_stream_that_was_not_running_is_not_started()
    {
        _users = "[\"framecorder\"]";
        _stopReply = "{\"alreadyStopped\": true}";
        await Control().ChangeWithRestartAsync(() => { _calls.Add("change"); return Task.CompletedTask; });
        Assert.Equal(["users", "STOP()", "change"], _calls);
    }

    [Fact]
    public async Task An_unreachable_Frame_means_a_live_change_as_before()
    {
        var control = new FrameStreamControl((_, _) => throw new IOException("no Frame"), _dir);
        Assert.Null(await control.ChangeWithRestartAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task A_refused_stop_is_reported_and_the_change_is_not_made()
    {
        _users = "[\"framecorder\"]";
        var control = new FrameStreamControl((script, _) => Task.FromResult(script == FrameStreamControl.EncoderUsersScript ? _users : "{\"error\": \"x\"}"), _dir);
        var ran = false;
        await Assert.ThrowsAsync<IOException>(() => control.ChangeWithRestartAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
    }
}

public sealed class FrameActivityTests
{
    [Fact]
    public void The_lease_script_extends_or_releases_and_nothing_else()
    {
        Assert.Contains("ExtendActivityTimeout(0, 60)", FrameActivity.Script(60));
        Assert.Contains("ExtendActivityTimeout(0, 0)", FrameActivity.Script(0));
        Assert.DoesNotContain("vrLinkStatus", FrameActivity.Script(60));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameActivity.Script(3600));
    }
}
