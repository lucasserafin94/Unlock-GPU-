using System.Globalization;
using System.Text;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Records samples to a CSV file in <see cref="AppPaths.SessionsDir"/> and keeps running sums for the summary.
/// Format (culture-invariant): first line is a '#' comment with metadata, then a header row, then one row per sample;
/// separator ';', decimal point '.', empty field = no reading. The file is opened with FileShare.ReadWrite so it can be
/// opened elsewhere while recording. Load statistics use samples with GPU utilisation &gt;= <see cref="LoadUtilPercent"/>
/// and valid power/clock readings (same rule as the reference app).
/// </summary>
public sealed class SessionRecorder : ISessionRecorder, IDisposable
{
    /// <summary>GPU utilisation (%) from which a sample counts as "under load".</summary>
    public const uint LoadUtilPercent = 90;
    /// <summary>Format version written in the comment line.</summary>
    public const int FormatVersion = 1;
    const string FilePrefix = "session-";

    static readonly string[] Columns =
        ["time", "pstate", "core_mhz", "sm_mhz", "mem_mhz", "vram_used_mb", "power_w", "limit_w", "temp_c", "util_gpu", "util_mem", "reasons"];

    readonly AppPaths _paths;
    readonly ILog _log;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    Recording? _rec;

    /// <summary>Creates a recorder (not recording).</summary>
    public SessionRecorder(AppPaths paths, ILog log, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _paths = paths;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public bool IsRecording { get { lock (_gate) return _rec != null; } }

    /// <inheritdoc/>
    public string? CurrentPath { get { lock (_gate) return _rec?.Path; } }

    /// <inheritdoc/>
    public void Start(string? label = null)
    {
        lock (_gate)
        {
            if (_rec != null) return;
            Directory.CreateDirectory(_paths.SessionsDir);
            var started = _time.GetLocalNow().DateTime;
            string stem = FilePrefix + started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeLabel = SafeLabel(label);
            if (safeLabel.Length > 0) stem += "-" + safeLabel;

            FileStream? fs = null;
            string path = "";
            for (int i = 1; fs == null; i++)
            {
                path = Path.Combine(_paths.SessionsDir, (i == 1 ? stem : stem + "-" + i.ToString(CultureInfo.InvariantCulture)) + ".csv");
                try { fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) when (File.Exists(path) && i < 100) { }
            }
            StreamWriter? writer = null;
            try
            {
                writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
                string labelMeta = (label ?? "").Replace(";", ",", StringComparison.Ordinal).ReplaceLineEndings(" ");
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"# UnlockGPU session v{FormatVersion}; started={started:yyyy-MM-ddTHH:mm:ss}; label={labelMeta}; separator=;; decimal=.; culture=invariant"));
                writer.WriteLine(string.Join(';', Columns));
            }
            catch
            {
                // Libera o arquivo (senao fica travado) e repassa o erro original
                CloseQuietly(writer, fs);
                throw;
            }
            _rec = new Recording(path, writer, _time.GetUtcNow());
            _log.Info("SESSION start " + path);
        }
    }

    /// <inheritdoc/>
    public void Add(GpuSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (_rec == null || !sample.Ok) return;
            try { _rec.Writer.WriteLine(FormatRow(sample)); }
            catch (IOException e) { _log.Error("SESSION write", e); }
            _rec.Stats.Add(sample);
        }
    }

    /// <inheritdoc/>
    public SessionSummary? Live()
    {
        lock (_gate) return _rec == null ? null : Summarize(_rec);
    }

    /// <inheritdoc/>
    public SessionSummary? Stop()
    {
        lock (_gate)
        {
            if (_rec == null) return null;
            var summary = Summarize(_rec);
            _rec.Writer.Dispose();
            _log.Info($"SESSION stop {_rec.Path} ({summary.Samples} samples)");
            _rec = null;
            return summary;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ListSessions()
    {
        if (!Directory.Exists(_paths.SessionsDir)) return [];
        return Directory.EnumerateFiles(_paths.SessionsDir, FilePrefix + "*.csv")
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Formats one CSV row (culture-invariant).</summary>
    public static string FormatRow(GpuSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var ci = CultureInfo.InvariantCulture;
        return string.Join(';',
            s.Time.ToString("yyyy-MM-dd HH:mm:ss", ci),
            s.PState,
            s.CoreMHz.ToString(ci),
            s.SmMHz.ToString(ci),
            s.MemMHz.ToString(ci),
            Math.Round(s.VramUsedMB).ToString("0", ci),
            double.IsNaN(s.PowerW) ? "" : s.PowerW.ToString("0.0", ci),
            double.IsNaN(s.LimitW) ? "" : s.LimitW.ToString("0.0", ci),
            s.TempC.ToString(ci),
            s.UtilGpu.ToString(ci),
            s.UtilMem.ToString(ci),
            ReasonsField(s.Reasons));
    }

    static string ReasonsField(ClockReasons r)
    {
        if (r == ClockReasons.None) return "";
        var names = new List<string>();
        ulong known = 0;
        foreach (var flag in Enum.GetValues<ClockReasons>())
        {
            if (flag == ClockReasons.None) continue;
            known |= (ulong)flag;
            if (r.HasFlag(flag)) names.Add(flag.ToString());
        }
        ulong unknown = (ulong)r & ~known;
        if (unknown != 0) names.Add("0x" + unknown.ToString("X", CultureInfo.InvariantCulture));
        return string.Join('|', names);
    }

    /// <summary>Disposes the writer (which owns the stream) or, if it was never created, the stream itself; dispose errors are ignored.</summary>
    static void CloseQuietly(StreamWriter? writer, FileStream fs)
    {
        try { writer?.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        // Dispose repetido do FileStream e no-op; garante o fechamento se o writer falhou
        try { fs.Dispose(); }
        catch (IOException) { }
    }

    static string SafeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        var sb = new StringBuilder();
        foreach (char c in label.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        string s = sb.ToString();
        return s.Length > 40 ? s[..40] : s;
    }

    SessionSummary Summarize(Recording rec) => rec.Stats.ToSummary(rec.Path, _time.GetUtcNow() - rec.StartedUtc);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _rec?.Writer.Dispose();
            _rec = null;
        }
    }

    sealed record Recording(string Path, StreamWriter Writer, DateTimeOffset StartedUtc)
    {
        public SessionStats Stats { get; } = new();
    }
}
