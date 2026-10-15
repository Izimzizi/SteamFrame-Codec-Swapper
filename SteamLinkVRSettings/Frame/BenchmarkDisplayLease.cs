using System.Text.Json;
using SteamLinkVRSettings.Services;

namespace SteamLinkVRSettings.Frame;

internal sealed record BenchmarkDisplayJournal(string Config,int OriginalHz,double? OriginalHost,double? OriginalFrame,int ExpectedHz,int PreviousHz);
internal interface IBenchmarkDisplay
{
    Task<double?> Host();
    Task<double> EffectiveHost();
    Task<FrameDisplayRate.State> Frame(CancellationToken token);
    Task Host(double? rate);
    Task Frame(int rate,CancellationToken token);
    Task Preference(double? rate,CancellationToken token);
    Task<double> Pc();
}
internal sealed class BenchmarkDisplayLease(SteamPaths paths,FramePreferences preferences,string? path=null,IBenchmarkDisplay? backend=null)
{
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SteamLinkVRSettings","benchmark-display-recovery.json");
    private readonly string _path=path??DefaultPath;
    private readonly IBenchmarkDisplay _display=backend??new LiveBenchmarkDisplay(paths,preferences);
    private BenchmarkDisplayJournal? _data;
    private bool _failedBegin;
    public bool Pending=>File.Exists(_path);
    public async Task BeginAsync(int hz,CancellationToken token)
    {
        _failedBegin=true;
        if(Pending)throw new IOException("Recover the interrupted display-mode test first.");
        var frame=await _display.Frame(token);
        if(!FrameDisplayRate.Matches(frame.DisplayHz,hz))throw new IOException("The Frame and PC disagree about active Hz.");
        _data=new(paths.Config,hz,await _display.Host(),frame.PreferredHz,hz,hz);
        await SaveAsync(true);_failedBegin=false;
    }
    public async Task ApplyAsync(int hz,CancellationToken token)
    {
        if(hz is not(90 or 120))throw new ArgumentOutOfRangeException(nameof(hz));
        var d=_data??throw new IOException("No display journal owns this test.");
        var host=await _display.Host();var frame=await _display.Frame(token);
        bool initial=d.ExpectedHz==d.OriginalHz && d.PreviousHz==d.OriginalHz;
        // SteamVR removes explicit values equal to its defaults when saving.
        // Ownership follows the effective rate, while recovery retains absence.
        var effectiveHost=host??await _display.EffectiveHost();
        if(!(SameRate(effectiveHost,d.ExpectedHz) || initial && SameRate(host,d.OriginalHost)) || !FrameDisplayRate.Matches(frame.DisplayHz,d.ExpectedHz))
            throw new IOException($"Display settings changed outside auto tune (host preference {host:0.####}, Frame {frame.DisplayHz:0.####}, expected {d.ExpectedHz} Hz).");
        _data=d with {PreviousHz=d.ExpectedHz,ExpectedHz=hz};await SaveAsync();
        await _display.Host(hz);await _display.Frame(hz,token);
    }
    public async Task RestoreAsync(Func<Task> reconnect,CancellationToken token)
    {
        if(_failedBegin || !Pending)return;
        if(new FileInfo(_path).Length>8192)throw new IOException("Invalid display recovery journal.");
        var d=_data??JsonSerializer.Deserialize<BenchmarkDisplayJournal>(await File.ReadAllTextAsync(_path))??throw new IOException("Missing display recovery journal.");
        if(!Path.GetFullPath(d.Config).Equals(Path.GetFullPath(paths.Config),StringComparison.OrdinalIgnoreCase) ||
            d.OriginalHz is not(90 or 120) || d.ExpectedHz is not(90 or 120) || d.PreviousHz is not(90 or 120) ||
            new[]{d.OriginalHost,d.OriginalFrame}.Any(x=>x.HasValue && (!double.IsFinite(x.Value) || x<0 || x>240)))
            throw new IOException("Display journal does not match this installation.");
        var host=await _display.Host();var frame=await _display.Frame(token);
        var effectiveHost=host??await _display.EffectiveHost();
        bool owned=(SameRate(effectiveHost,d.ExpectedHz) || SameRate(effectiveHost,d.PreviousHz) || SameRate(host,d.OriginalHost)) &&
            (SameRate(frame.PreferredHz,d.ExpectedHz) || SameRate(frame.PreferredHz,d.PreviousHz) || SameRate(frame.PreferredHz,d.OriginalFrame));
        if(owned)
        {
            // Keep a recoverable journal throughout a partial restore/restart.
            _data=d with {ExpectedHz=d.OriginalHz,PreviousHz=d.ExpectedHz};await SaveAsync();
            await _display.Host(d.OriginalHz);await _display.Frame(d.OriginalHz,token);
            if(!FrameDisplayRate.Matches(await _display.Pc(),d.OriginalHz))await reconnect();
            if(!FrameDisplayRate.Matches(await _display.Pc(),d.OriginalHz))throw new IOException("Original display Hz is not active yet.");
            await _display.Preference(d.OriginalFrame,token);await _display.Host(d.OriginalHost);
        }
        File.Delete(_path);_data=null;
    }
    internal static bool SameRate(double? a,double? b)=>a.HasValue && b.HasValue
        ? double.IsFinite(a.Value) && double.IsFinite(b.Value) && Math.Abs(a.Value-b.Value)<.1 : a==b;
    private async Task SaveAsync(bool create=false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);string temp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
            {await JsonSerializer.SerializeAsync(file,_data);await file.FlushAsync();file.Flush(true);}
            File.Move(temp,_path,!create);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
internal sealed class LiveBenchmarkDisplay(SteamPaths paths,FramePreferences preferences):IBenchmarkDisplay
{
    public async Task<double?> Host()
    {
        var saved=await new SettingsService{Section="steamvr",UserConfigPath=paths.Config}.LoadUserOverridesAsync();
        return saved.TryGetValue("preferredRefreshRate",out var v)?Convert.ToDouble(v):null;
    }
    public Task<FrameDisplayRate.State> Frame(CancellationToken token)=>FrameDisplayRate.ReadAsync(preferences,token);
    public Task<double> EffectiveHost()=>Task.Run(()=>OpenVrSettings.ReadPreferredRefreshRate(paths));
    public Task Host(double? hz)=>Task.Run(()=>{if(hz.HasValue)OpenVrSettings.SetPreferredRefreshRate(paths,hz.Value);else OpenVrSettings.RemovePreferredRefreshRate(paths);});
    public async Task Frame(int hz,CancellationToken token)=>_=await FrameDisplayRate.SetAsync(preferences,hz,token);
    public Task Preference(double? hz,CancellationToken token)=>FrameDisplayRate.RestorePreferenceAsync(preferences,hz,token);
    public Task<double> Pc()=>Task.Run(()=>OpenVrSettings.ReadDisplayHz(paths));
}
