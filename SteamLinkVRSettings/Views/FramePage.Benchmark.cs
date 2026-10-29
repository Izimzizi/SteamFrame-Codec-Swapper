using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SteamLinkVRSettings.Frame;

namespace SteamLinkVRSettings.Views;

public sealed partial class FramePage
{
    private CancellationTokenSource? _benchmarkStop;
    private bool BenchmarkRecoveryPending => new BenchmarkSettingsLease(_paths,_probe).Pending || new BenchmarkDisplayLease(_paths,_preferences).Pending;

    private async void BenchmarkStart_Click(object sender,RoutedEventArgs e)=>await ActionAsync(async()=>
    {
        await EnsureReconnectOwnerAsync();
        if(_bandwidthEdited || _codecEdited || _pendingPreset!=null || _policyEdited || _encoderAdvancedEdited)
            throw new InvalidOperationException("Apply your pending edits before auto tune.");
        if(_sweepRunning)throw new InvalidOperationException("The Overview capacity sweep is running. Stop it before auto tune.");
        int Whole(double value)=>double.IsFinite(value) && value==Math.Truncate(value)?checked((int)value):throw new ArgumentException("Use whole numbers for test limits.");
        var options=new BenchmarkOptions(Whole(BenchmarkMaximum.Value),Whole(BenchmarkTrialSeconds.Value),Whole(BenchmarkValidationSeconds.Value),
            Whole(BenchmarkHeadroom.Value),BenchmarkFullMultipass.IsChecked==true,BenchmarkUnattended.IsChecked==true,
            BenchmarkReduceQuality.IsChecked==true,BenchmarkResolutions.IsChecked==true,BenchmarkRefreshRates.IsChecked==true,
            BenchmarkIssuePercent.Value,Whole(BenchmarkIssueMilliseconds.Value));
        options.Validate();
        ActionStatus.IsOpen=false;
        _benchmarkStop=CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token??default);
        foreach(var control in BenchmarkOptionsPanel.Children.OfType<Control>())control.IsEnabled=false;
        BenchmarkStartButton.IsEnabled=BenchmarkRecoverButton.IsEnabled=false;BenchmarkCancelButton.IsEnabled=true;
        BenchmarkResultCard.Visibility=Visibility.Collapsed;BenchmarkChoiceList.Children.Clear();
        BenchmarkProgressCard.Visibility=Visibility.Visible;BenchmarkProgress.IsIndeterminate=false;
        BenchmarkResultsLink.Visibility=Visibility.Collapsed;
        BenchmarkView.ChangeView(null,0,null,true);
        BenchmarkTrials.Text="Measuring…";BenchmarkTrialsExpander.IsExpanded=false;
        var runner=new BenchmarkRunner(_paths,_preferences,_probe,_powerLease,progressChanged:ShowBenchmarkProgress);
        ShowBenchmarkProgress(runner.Progress);
        try
        {
            var result=await runner.RunAsync(options,text=>{if(!runner.Progress.Measuring)BenchmarkStatus.Text=text;},_benchmarkStop.Token);
            BenchmarkStatus.Text=result.Message;
            BenchmarkTrials.Text=string.Join("\n\n",result.Trials.Select(t=>t.Stage+" · "+t.Candidate.Label+"\n"+
                $"{t.ActualMbps:0.0} actual Mbps · {t.EncodedFps:0.00} encoded fps · {t.ReceiverFps:0.00} headset submits/s · {t.Seconds:0}s\n"+
                (t.Assessment?.Summary??t.Reason)+(t.Quantizer is {} q?"\n"+q.Summary:"")+(t.Logs is {} logs?"\n"+logs.Summary:"")+(t.LinkNote is {} linkNote?"\n"+linkNote:"")));
            ShowBenchmarkChoices(runner.Choices);
            await RefreshPresetsAsync();
            return runner.Progress.Complete?"Comparison complete. Recommended presets are ready below.":
                "Comparison stopped early. See the progress panel for the cause and available recommendations.";
        }
        catch(Exception error)
        {
            ShowBenchmarkProgress(runner.Progress with {Terminal=true,Complete=false,RecoveryRequired=runner.RecoveryRequired,
                Phase="Comparison stopped early",Activity=error.Message,Measuring=false,Percent=Math.Min(99,runner.Progress.Percent),
                CompletedSteps=Math.Min(3,runner.Progress.CompletedSteps)});
            BenchmarkStatus.Text="Testing stopped: "+error.Message;
            int configuredWidth=double.IsFinite(StreamWidthInput.Value)?Math.Clamp((int)StreamWidthInput.Value,512,4096):1664;
            var result=BenchmarkExplorer.Recommend([],options,false,error.Message,configuredWidth,LegacyRefreshHz);
            var choice=result.Recommendations[0];
            var profile=WirelessBenchmark.Profile(new TuningProfile{BitratePatch=_patch?.IsPatched==true,RateControl=EncoderRateControl.Cbr},
                choice.Candidate,choice.Candidate.Width,choice.Candidate.Fps);
            var name="Auto unmeasured starting option "+DateTime.Now.ToString("MM-dd HH-mm-ss");
            // Keep a concrete option available even if the disconnected hardware
            // or preset storage could not be read. Never call it measured.
            try {await new PresetStore().SaveAsync(name,profile);await RefreshPresetsAsync();}
            catch(Exception saveError){BenchmarkStatus.Text+=" Preset could not be saved: "+saveError.Message;}
            ShowBenchmarkChoices([new(name,profile,choice)]);
            return "An unmeasured starting option is available. See the interruption details before applying it.";
        }
        finally
        {
            bool pending=BenchmarkRecoveryPending || runner.RecoveryRequired;
            if(pending)BenchmarkStatus.Text+=" Recovery is pending. Reconnect and restore starting settings first.";
            foreach(var button in BenchmarkChoiceList.Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()))button.IsEnabled=!pending;
            UpdatePowerLease();BenchmarkProgress.IsIndeterminate=false;BenchmarkTrialProgress.IsIndeterminate=false;
            BenchmarkResultsLink.Visibility=BenchmarkChoiceList.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
            foreach(var control in BenchmarkOptionsPanel.Children.OfType<Control>())control.IsEnabled=true;
            BenchmarkStartButton.IsEnabled=BenchmarkRecoverButton.IsEnabled=true;BenchmarkCancelButton.IsEnabled=false;
            _benchmarkStop.Dispose();_benchmarkStop=null;_rate.Reset();_quantizers.Reset();_nextReceiverRead=_nextConnectionRead=0;
        }
    });
    private void ShowBenchmarkProgress(BenchmarkProgressState state)
    {
        BenchmarkPhaseTitle.Text=state.Phase;BenchmarkPhaseCount.Text=state.PhaseCount;
        BenchmarkCancelButton.Visibility=state.Terminal?Visibility.Collapsed:Visibility.Visible;
        BenchmarkPercent.Text=$"{Math.Floor(state.Percent):0}%";BenchmarkProgress.Value=state.Percent;
        BenchmarkPercentCaption.Text=state.Complete?"complete":state.Terminal?"before stopping":"estimated";
        var accent=(Brush)Application.Current.Resources[state.Terminal&&!state.Complete?"SteamModifiedBrush":state.Complete?"SteamAccentGreenBrush":"SteamBlueBrush"];
        BenchmarkPercent.Foreground=BenchmarkProgress.Foreground=accent;
        var steps=new[]{BenchmarkPrepareStep,BenchmarkCompareStep,BenchmarkTuneStep,BenchmarkRestoreStep};
        for(int i=0;i<steps.Length;i++)steps[i].Foreground=(Brush)Application.Current.Resources[i<state.CompletedSteps?"SteamAccentGreenBrush":i==state.Step?"SteamBlueBrush":"TextFillColorSecondaryBrush"];
        BenchmarkCurrentCandidate.Text=state.Candidate;
        BenchmarkCurrentCandidate.Visibility=string.IsNullOrEmpty(state.Candidate)?Visibility.Collapsed:Visibility.Visible;
        BenchmarkMeasurementLabel.Text=state.Measuring?"Measuring current test":state.Terminal?"Measurement finished":"Preparing current test";
        BenchmarkMeasurementTime.Text=state.Measuring?$"{state.MeasuredSeconds} / {state.MeasurementSeconds} s":state.Terminal?"":"Waiting…";
        BenchmarkTrialProgress.Visibility=state.Terminal?Visibility.Collapsed:Visibility.Visible;
        BenchmarkTrialProgress.IsIndeterminate=!state.Measuring&&!state.Terminal;
        BenchmarkTrialProgress.Value=state.MeasurementSeconds>0?state.MeasuredSeconds*100d/state.MeasurementSeconds:0;
        BenchmarkStatus.Text=state.Activity;
        var elapsed=TimeSpan.FromSeconds(state.ElapsedSeconds);
        BenchmarkCompletedCount.Text=$"{state.CompletedTests} tests completed"+(state.MaximumTests>0?$" · up to {state.MaximumTests} planned":"")+
            $" · elapsed {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }
    private void BenchmarkResults_Click(object sender,RoutedEventArgs e)=>BenchmarkResultCard.StartBringIntoView();
    private void ShowBenchmarkChoices(IReadOnlyList<BenchmarkSavedOption> choices)
    {
        BenchmarkChoiceList.Children.Clear();BenchmarkResultTitle.Text="Choose your preferred setup";
        BenchmarkResultDetail.Text="Each option includes stream width, Hz, encoder pass and gaze/periphery offsets. Ranking balances requested detail with measured smoothness; more Mbps alone is not better. Load an option to review and apply it. Starting settings are restored unless recovery is pending.";
        foreach(var option in choices)
        {
            var recommendation=option.Recommendation;var trial=recommendation.Trial;
            var panel=new StackPanel{Spacing=8,Margin=new Thickness(0,8,0,12)};
            panel.Children.Add(new TextBlock{Text=recommendation.Name,FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            panel.Children.Add(new TextBlock{Text=recommendation.Candidate.Label,TextWrapping=TextWrapping.Wrap});
            panel.Children.Add(new TextBlock{Text=(trial is null?"Unmeasured — no stability claim.\n":
                $"{trial.ActualMbps:0.0} actual Mbps · {trial.EncodedFps:0.0} encoded fps · {trial.Seconds:0}s measured\n")+
                recommendation.Note,TextWrapping=TextWrapping.Wrap});
            var button=new Button{Content="Load "+recommendation.Name,Tag=option.Profile};
            button.Click+=BenchmarkChoice_Click;panel.Children.Add(button);BenchmarkChoiceList.Children.Add(panel);
        }
        BenchmarkResultCard.Visibility=Visibility.Visible;
    }
    private async void BenchmarkChoice_Click(object sender,RoutedEventArgs e)=>await ActionAsync(async()=>
    {
        if(BenchmarkRecoveryPending)throw new IOException("Restore starting settings before applying an option.");
        if(sender is not Button{Tag:TuningProfile profile})throw new InvalidOperationException("Choose a saved option.");
        await LoadProfileIntoEditorAsync(profile);SectionRequested?.Invoke("stream");
        return "Recommended option loaded. Review it, then apply stream settings.";
    });
    private void BenchmarkCancel_Click(object sender,RoutedEventArgs e)
    {_benchmarkStop?.Cancel();BenchmarkCancelButton.IsEnabled=false;BenchmarkStatus.Text="Stopping and restoring starting settings…";}
    private async void BenchmarkRecover_Click(object sender,RoutedEventArgs e)=>await ActionAsync(async()=>
    {
        await EnsureReconnectOwnerAsync();var lease=new BenchmarkSettingsLease(_paths,_probe);var display=new BenchmarkDisplayLease(_paths,_preferences);
        if(display.Pending && !lease.Pending)await lease.BeginAsync(await _probe.GetSnapshotAsync());
        async Task Restart()=>_=await BenchmarkRuntimeRestart.RunAsync(_paths,_preferences,_probe,lease,text=>BenchmarkStatus.Text=text,CancellationToken.None);
        await display.RestoreAsync(Restart,CancellationToken.None);await lease.RestoreAsync(Restart);
        if(_powerLease.RecoveryPending)await _powerLease.RestoreAsync();UpdatePowerLease();
        foreach(var button in BenchmarkChoiceList.Children.OfType<StackPanel>().SelectMany(p=>p.Children.OfType<Button>()))button.IsEnabled=true;
        BenchmarkStatus.Text="Starting settings and display Hz recovered. Newer external edits were preserved.";return BenchmarkStatus.Text;
    });
    private void BenchmarkReports_Click(object sender,RoutedEventArgs e)
    {
        try {Directory.CreateDirectory(BenchmarkRunner.ReportsDirectory);Process.Start(new ProcessStartInfo(BenchmarkRunner.ReportsDirectory){UseShellExecute=true});}
        catch(Exception error){ShowStatus("Could not open benchmark reports: "+error.Message,true);}
    }
}
