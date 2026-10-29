using System.Globalization;
using System.Text;

namespace SteamFrameTray;

/// <summary>
/// A CSV of one measuring run: setup description as '#' comment lines, then one row per sample. The file can be read
/// while the run is still being written.
/// </summary>
public sealed class RunLog : IDisposable
{
    public static readonly string[] Columns =
    [
        "time", "elapsed_s", "mbps", "avg60_mbps", "fps", "frames_encoded", "bytes_encoded", "codec", "codec_verified",
        "tray_state", "target_mbps", "enc_avg_mbps", "enc_max_mbps", "enc_rate_control", "enc_vbv_bits", "enc_preset", "enc_fps",
        "last_frame_qp", "avg_qp", "last_frame_bytes", "encode_errors", "held_total", "held_last60", "link_stalls",
        "comp_available", "comp_dropped", "comp_late", "comp_reprojected", "steamvr_running", "note"
    ];

    private readonly StreamWriter _writer;
    private DateTimeOffset? _first;
    public string Path { get; }

    public RunLog(string directory, IEnumerable<string> header, DateTimeOffset now)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "run-" + now.LocalDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
        _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        foreach (var line in header) _writer.WriteLine("# " + line.Replace('\r', ' ').Replace('\n', ' '));
        _writer.WriteLine(string.Join(',', Columns));
    }

    public void Write(StatsSample sample, TraySnapshot tray, double? average60)
    {
        _first ??= sample.At;
        var config = sample.Config; var health = sample.Health;
        string[] row =
        [
            sample.At.LocalDateTime.ToString("O", CultureInfo.InvariantCulture), (sample.At - _first.Value).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
            Number(sample.Mbps), Number(average60), Number(sample.Fps, "0.00"), sample.EncodedFrames.ToString(CultureInfo.InvariantCulture), sample.EncodedBytes.ToString(CultureInfo.InvariantCulture),
            sample.Codec, sample.CodecVerified ? "1" : "0", tray.State.ToString(), tray.TargetMbps.ToString(CultureInfo.InvariantCulture),
            Number(config?.AverageMbps), Number(config?.MaxMbps), config?.RateControl ?? "", config is null ? "" : config.VbvSize.ToString(CultureInfo.InvariantCulture), config?.Preset ?? "", Number(config?.Fps, "0.00"),
            sample.LastFrameQp?.ToString(CultureInfo.InvariantCulture) ?? "", Number(sample.AverageQp, "0.00"), sample.LastFrameBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
            sample.EncodeErrors.ToString(CultureInfo.InvariantCulture),
            health is null ? "" : health.HeldTotal.ToString(CultureInfo.InvariantCulture), health is null ? "" : health.HeldLastMinute.ToString(CultureInfo.InvariantCulture),
            health is null ? "" : health.LinkStalls.ToString(CultureInfo.InvariantCulture),
            health is null ? "" : health.CompositorAvailable ? "1" : "0", health is { CompositorAvailable: true } ? health.CompositorDropped.ToString(CultureInfo.InvariantCulture) : "",
            health is { CompositorAvailable: true } ? health.CompositorLate.ToString(CultureInfo.InvariantCulture) : "", health is { CompositorAvailable: true } ? health.CompositorReprojected.ToString(CultureInfo.InvariantCulture) : "",
            sample.SteamVrRunning ? "1" : "0", sample.Note ?? ""
        ];
        _writer.WriteLine(string.Join(',', row.Select(Escape)));
    }

    public void Dispose() => _writer.Dispose();

    private static string Number(double? value, string format = "0.0") => value is double v && double.IsFinite(v) ? v.ToString(format, CultureInfo.InvariantCulture) : "";

    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}
