using Microsoft.UI.Xaml;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private async Task EnsureReconnectOwnerAsync()
    {
        var service = await _codecController.ReadStatusAsync();
        if (CodecHooksPaused(service) || service.Live)
            throw new InvalidOperationException("Finish switching back to HEVC with Apply and reconnect before using Reconnect Frame.");
    }

    private async void ReconnectFrame_Click(object sender, RoutedEventArgs e) => await ActionAsync(ReconnectFrameAsync);
    private async Task<string> ReconnectFrameAsync()
    {
        await EnsureReconnectOwnerAsync();
        var token = _lifetime?.Token ?? default;
        var result = await FrameReconnectRecovery.RunAsync(_probe.GetSnapshotAsync,
            async processId =>
            {
                await EnsureReconnectOwnerAsync();
                token.ThrowIfCancellationRequested();
                _configuredPid = 0;
                _rate.Reset(); _quantizers.Reset(); _history.Clear();
                await Task.Run(() => SteamVrRuntime.StartOrRestartForReconnectAsync(_paths, processId));
            }, async (before, ready) =>
            {
                await EnsureReconnectOwnerAsync();
                token.ThrowIfCancellationRequested();
                var tuning = FrameReconnectTuning.Capture(before, _preferences);
                var current = await _probe.StartAsync();
                void Check()
                {
                    token.ThrowIfCancellationRequested();
                    if (current.ProcessId != ready.ProcessId || !current.Installed ||
                        current.PairedCodecTrialPresent || current.RequiresSteamVrRestart)
                        throw new InvalidOperationException("The encoder changed while restoring tuning. " + current.Message);
                }
                Check();
                if (tuning is not null)
                {
                    current = await _probe.SetQualityAsync(tuning.QualityEnabled, tuning.Gaze, tuning.Peripheral); Check();
                    current = await _probe.SetEncodingPolicyAsync(tuning.Policy); Check();
                    if (current.AdvancedEncoding is { Supported: true })
                    { current = await _probe.SetAdvancedEncodingAsync(tuning.Advanced); Check(); }
                    else if (!tuning.Advanced.IsDefault)
                        throw new InvalidOperationException("SteamVR restarted, but advanced encoder tuning could not be restored.");
                    if (!tuning.RequestsRestored(current))
                        throw new InvalidOperationException("SteamVR restarted, but the encoder did not retain all previous tuning requests. Check Stream settings before reopening your game.");
                    if (current.Active)
                    {
                        await EnsureReconnectOwnerAsync();
                        await Task.Run(() => OpenVrSettings.RefreshEncoder(_paths));
                        current = await _probe.GetSnapshotAsync(); Check();
                    }
                }
                _monitorInstalled = true;
                _configuredPid = current.ProcessId;
                return current;
            }, message => ShowStatus(message, false), token);
        RenderEncoderRecovery(result.Snapshot);
        _rate.Reset(); _quantizers.Reset();
        _nextConnectionRead = _nextReceiverRead = 0;
        if (!result.VideoResumed && FrameSshConfigured)
        {
            // The headset often needs to be told to stream again after a SteamVR restart: do what its
            // own Stop streaming / Stream again does, then look for fresh video once more.
            ShowStatus("SteamVR restarted without fresh video; asking the Frame to stream again…", false);
            try
            {
                if (await FrameStreamControl.StartAndWaitAsync(_preferences, _streamHealth, message => ShowStatus(message, false), token))
                {
                    var previous = await _probe.GetSnapshotAsync();
                    for (var attempt = 0; attempt < 30 && !result.VideoResumed; attempt++)
                    {
                        await Task.Delay(500, token);
                        var current = await _probe.GetSnapshotAsync();
                        if (FrameReconnectRecovery.FreshVideo(previous, current)) result = new(current, true);
                        previous = current;
                    }
                    RenderEncoderRecovery(result.Snapshot);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { ShowStatus("The Frame could not be asked to stream again: " + error.Message, false); }
        }
        if (!result.VideoResumed)
            return "SteamVR restarted, but no fresh HEVC video was detected. Keep the Frame awake. Unplug the dedicated dongle, plug it back in (the same port or hub is fine), and wait for it to reconnect. Select this PC in the Frame's streaming menu if needed. Recovery will not restart again automatically.";
        _links = await _connection.ReadAsync(token);
        return _links.ActiveLinks.Length > 0
            ? $"Frame link restored ({_links.VideoPath}); fresh HEVC video verified. Reopen your game when ready."
            : "Fresh HEVC video resumed. Headset link status could not be verified; check the image in the Frame before reopening your game.";
    }
}
