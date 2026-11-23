using System.Collections.ObjectModel;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>A recorded session file.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Name">File name.</param>
/// <param name="Details">Date and size, formatted.</param>
public sealed record SessionFileItem(string Path, string Name, string Details);

/// <summary>A label/value line of the live summary.</summary>
/// <param name="Label">Translated label.</param>
/// <param name="Value">Formatted value.</param>
public sealed record SummaryLine(string Label, string Value);

/// <summary>Sessions tab: record readings to CSV, live summary, list, open folder, Excel (pt-BR) export.</summary>
public sealed class SessionsViewModel : TabViewModel
{
    readonly ISessionRecorder _recorder;
    readonly IDialogService _dialogs;
    readonly AppPaths _paths;
    readonly BannerViewModel _banner;
    SessionSummary? _summary;
    SessionFileItem? _selected;
    string _label = "";
    bool _isRecording;
    DateTime _lastLiveRefresh;

    /// <summary>Creates the tab.</summary>
    public SessionsViewModel(Localizer localizer, ISessionRecorder recorder, IDialogService dialogs, AppPaths paths,
        BannerViewModel banner) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(banner);
        _recorder = recorder;
        _dialogs = dialogs;
        _paths = paths;
        _banner = banner;
        void OnError(Exception ex) => _banner.Show(ResultText.Exception(L, ex), BannerKind.Error);
        StartCommand = new RelayCommand(Start, () => !_isRecording);
        StopCommand = new RelayCommand(Stop, () => _isRecording);
        RefreshCommand = new RelayCommand(Refresh);
        OpenFolderCommand = new RelayCommand(() => _dialogs.OpenFolder(_paths.SessionsDir));
        RevealCommand = new RelayCommand(() => { if (_selected != null) _dialogs.RevealFile(_selected.Path); }, () => _selected != null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _selected != null, OnError);
        _isRecording = recorder.IsRecording;
        Refresh();
    }

    /// <summary>Optional label added to the file name.</summary>
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    /// <summary>Recording in progress.</summary>
    public bool IsRecording { get => _isRecording; private set { if (SetProperty(ref _isRecording, value)) { StartCommand.RaiseCanExecuteChanged(); StopCommand.RaiseCanExecuteChanged(); } } }
    /// <summary>Path of the file being written.</summary>
    public string CurrentPath => _recorder.CurrentPath ?? "";
    /// <summary>Live (or last) summary lines.</summary>
    public ObservableCollection<SummaryLine> Summary { get; } = [];
    /// <summary>True when there is a summary to show.</summary>
    public bool HasSummary => Summary.Count > 0;
    /// <summary>Recorded sessions, newest first.</summary>
    public ObservableCollection<SessionFileItem> Sessions { get; } = [];

    /// <summary>Selected session.</summary>
    public SessionFileItem? SelectedSession
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { RevealCommand.RaiseCanExecuteChanged(); ExportCommand.RaiseCanExecuteChanged(); } }
    }

    /// <summary>Starts recording.</summary>
    public RelayCommand StartCommand { get; }
    /// <summary>Stops recording and keeps the final summary.</summary>
    public RelayCommand StopCommand { get; }
    /// <summary>Re-lists the folder.</summary>
    public RelayCommand RefreshCommand { get; }
    /// <summary>Opens the sessions folder.</summary>
    public RelayCommand OpenFolderCommand { get; }
    /// <summary>Shows the selected file in Explorer.</summary>
    public RelayCommand RevealCommand { get; }
    /// <summary>Exports the selected session for Excel (';' and ',' decimals).</summary>
    public AsyncRelayCommand ExportCommand { get; }

    /// <inheritdoc/>
    public override void OnSample(SampleContext context)
    {
        if (!_isRecording) return;
        // The summary is cheap, but refreshing it more than once a second is pointless.
        var now = DateTime.UtcNow;
        if (now - _lastLiveRefresh < TimeSpan.FromSeconds(1)) return;
        _lastLiveRefresh = now;
        _summary = _recorder.Live();
        BuildSummary();
    }

    /// <inheritdoc/>
    public override void OnLanguageChanged()
    {
        BuildSummary();
        Refresh();
    }

    void Start()
    {
        _recorder.Start(string.IsNullOrWhiteSpace(_label) ? null : _label.Trim());
        IsRecording = _recorder.IsRecording;
        OnPropertyChanged(nameof(CurrentPath));
        _summary = _recorder.Live();
        BuildSummary();
        Refresh();
    }

    void Stop()
    {
        _summary = _recorder.Stop();
        IsRecording = false;
        OnPropertyChanged(nameof(CurrentPath));
        BuildSummary();
        Refresh();
        if (_summary != null) _banner.Show(L.T("ui.sessions.saved", ("file", Path.GetFileName(_summary.CsvPath))), BannerKind.Success);
    }

    void Refresh()
    {
        string? keep = _selected?.Path;
        Sessions.Clear();
        foreach (string path in _recorder.ListSessions())
        {
            var fi = new FileInfo(path);
            string details = fi.Exists
                ? fi.LastWriteTime.ToString("g", L.Culture) + " · " + Format.OneDecimal(fi.Length / 1024.0, L.Culture) + " KB"
                : "";
            Sessions.Add(new SessionFileItem(path, fi.Name, details));
        }
        SelectedSession = Sessions.FirstOrDefault(s => s.Path == keep) ?? Sessions.FirstOrDefault();
    }

    async Task ExportAsync()
    {
        if (_selected == null) return;
        string source = _selected.Path;
        string defaultName = Path.GetFileNameWithoutExtension(source) + "-excel.csv";
        string? target = _dialogs.SaveFile(L.T("ui.sessions.exportTitle"), L.T("ui.dialog.csvFilter"), defaultName);
        if (target == null) return;
        int rows = await Task.Run(() => SessionCsvExporter.Export(source, target)).ConfigureAwait(true);
        _banner.Show(L.T("ui.sessions.exported", ("rows", rows), ("file", Path.GetFileName(target))), BannerKind.Success);
    }

    void BuildSummary()
    {
        Summary.Clear();
        var s = _summary;
        if (s != null)
        {
            var c = L.Culture;
            Add("ui.sessions.sum.duration", Format.Duration(s.Duration));
            Add("ui.sessions.sum.samples", L.T("ui.sessions.sum.samplesValue", ("total", s.Samples), ("load", s.LoadSamples)));
            Add("ui.sessions.sum.avgPower", Format.Watts(s.AvgPowerW, c) + " W");
            Add("ui.sessions.sum.peakPower", Format.Watts(s.PeakPowerW, c) + " W");
            Add("ui.sessions.sum.avgCore", Format.Number(s.AvgCoreMHz, c) + " MHz");
            Add("ui.sessions.sum.maxTemp", s.MaxTempC.ToString(c) + " °C");
            if (s.MemClockShare.Count > 0)
                Add("ui.sessions.sum.memShare", string.Join("  ", s.MemClockShare.OrderByDescending(kv => kv.Key)
                    .Select(kv => Format.Number(kv.Key, c) + ": " + Format.Share(kv.Value, c))));
            if (s.ReasonShare.Count > 0)
                Add("ui.sessions.sum.reasonShare", string.Join("  ", s.ReasonShare.OrderByDescending(kv => kv.Value)
                    .Select(kv => ReasonName(kv.Key) + ": " + Format.Share(kv.Value, c))));
        }
        OnPropertyChanged(nameof(HasSummary));
    }

    void Add(string key, string value) => Summary.Add(new SummaryLine(L.T(key), value));

    // The recorder keys reason shares by localizer key (reason.*).
    string ReasonName(string reasonKey) => L.T(reasonKey);
}
