using System.Diagnostics;

namespace SteamLinkVRSettings.Frame;

internal sealed record BenchmarkProgressState(int Step, string Phase, string Activity, string Candidate,
    string PhaseCount, int CompletedTests, int MaximumTests, double Percent, int MeasuredSeconds,
    int MeasurementSeconds, bool Measuring, double ElapsedSeconds, bool Terminal, bool Complete, bool RecoveryRequired)
{
    public int CompletedSteps { get; init; }
}

/// <summary>Estimated work progress; optional trials have reserved slots, not a fabricated ETA.</summary>
internal sealed class BenchmarkProgressTracker(Action<BenchmarkProgressState>? changed = null)
{
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private int _step,_completedSteps,_completed,_maximum,_screens,_screenDone,_seconds,_measured;
    private double _percent,_base,_span,_leaderBase,_leaderSpan,_leaderWeight,_weightDone,_screenSpan;
    private string _phase="Preparing",_activity="Checking the headset and current settings…",_candidate="",_count="";
    private bool _measuring,_terminal,_complete,_recovery,_screenTrial;
    public BenchmarkProgressState Current=>new(_step,_phase,_activity,_candidate,_count,_completed,_maximum,
        _percent,_measured,_seconds,_measuring,_clock.Elapsed.TotalSeconds,_terminal,_complete,_recovery){CompletedSteps=_completedSteps};
    private void Publish()=>changed?.Invoke(Current);
    private void Advance(double value)=>_percent=Math.Clamp(Math.Max(_percent,value),0,99);
    public void Activity(string text){_activity=text;Publish();}
    public void Plan(int screens,int leaders,int qualityTrials,BenchmarkOptions options)
    {
        _screens=screens;_maximum=screens+leaders*(qualityTrials+4);
        double screenWork=screens*options.TrialSeconds;
        _screenSpan=90*screenWork/(screenWork+leaders*((qualityTrials+2)*options.TrialSeconds+2*options.ValidationSeconds));
        _completedSteps=1;Advance(5);Publish();
    }
    public void Screen(BenchmarkCandidate candidate,int seconds)
    {
        _step=1;_phase="Comparing display and encoder modes";_screenTrial=true;
        _count=$"Mode test {_screenDone+1} / {_screens}";
        Begin(candidate,seconds,5+_screenSpan*_screenDone/_screens,_screenSpan/_screens);
    }
    public void Choice(int index,int total,BenchmarkOptions options,int qualityTrials)
    {
        _step=2;_phase="Tuning recommended choices";_screenTrial=false;
        _count=$"Choice {index+1} / {total}";_leaderBase=5+_screenSpan+(90-_screenSpan)*index/total;_leaderSpan=(90-_screenSpan)/total;
        _leaderWeight=(qualityTrials+2)*options.TrialSeconds+2*options.ValidationSeconds;
        _weightDone=0;Advance(_leaderBase);Publish();
    }
    public void Refinement(BenchmarkCandidate candidate,int seconds,string stage)
    {
        _phase=stage=="Confirmation"?"Confirming a recommended choice":stage;
        Begin(candidate,seconds,_leaderBase+_leaderSpan*_weightDone/_leaderWeight,_leaderSpan*seconds/_leaderWeight);
    }
    private void Begin(BenchmarkCandidate candidate,int seconds,double start,double span)
    {
        _candidate=candidate.Label;_seconds=seconds;_measured=0;_measuring=false;
        _base=start;_span=span;_activity="Applying settings and preparing the stream…";Advance(start);Publish();
    }
    public void Measuring(double elapsed,int seconds)
    {
        _seconds=seconds;_measured=Math.Clamp((int)elapsed,0,seconds);_measuring=true;
        _activity="Measuring delivered frames and wireless performance";
        Advance(_base+_span*Math.Clamp(elapsed/seconds,0,.99));Publish();
    }
    public void TrialFinished(double? measuredSeconds=null)
    {
        _completed++;if(_screenTrial)_screenDone++;else _weightDone+=_seconds;
        if(_screenTrial && _screenDone==_screens)_completedSteps=2;
        _measured=Math.Clamp((int)(measuredSeconds??_seconds),0,_seconds);_measuring=false;Advance(_base+_span);Publish();
    }
    public void ChoiceFinished(){Advance(_leaderBase+_leaderSpan);Publish();}
    public void SearchFinished(){_completedSteps=3;Advance(95);Publish();}
    public void Restoring(bool interrupted)
    {
        _step=3;_phase="Restoring your settings";_measuring=false;_seconds=_measured=0;_candidate="";
        _count=$"{_completed} tests completed";_activity="Restoring the original stream settings and refresh rate…";
        if(!interrupted)Advance(95);Publish();
    }
    public void Finish(bool complete,bool recovery,string message)
    {
        _terminal=true;_complete=complete;_recovery=recovery;_measuring=false;_seconds=_measured=0;
        _phase=recovery?"Settings recovery needed":complete?"Comparison complete":"Comparison stopped early";
        _step=complete?4:3;_candidate="";_activity=message;_count=$"{_completed} tests completed";
        if(complete){_percent=100;_completedSteps=4;}else _percent=Math.Min(_percent,99);Publish();
    }
}
