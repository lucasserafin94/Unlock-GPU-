using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>One value tile on the Monitor tab.</summary>
public sealed class TileViewModel(string titleKey) : ObservableObject
{
    string _title = "";
    string _value = Format.Unknown;
    string _unit = "";
    string _sub = "";
    bool _alert;

    /// <summary>Localizer key of the title.</summary>
    public string TitleKey { get; } = titleKey;
    /// <summary>Translated title.</summary>
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    /// <summary>Big value.</summary>
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    /// <summary>Unit next to the value.</summary>
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
    /// <summary>Small line under the value.</summary>
    public string Sub { get => _sub; set => SetProperty(ref _sub, value); }
    /// <summary>Highlights the tile (e.g. high temperature).</summary>
    public bool Alert { get => _alert; set => SetProperty(ref _alert, value); }
}

/// <summary>A clock-reason chip, coloured by severity.</summary>
/// <param name="Text">Translated reason.</param>
/// <param name="Severity">Severity (colour).</param>
/// <param name="Tooltip">Raw bits for experts.</param>
public sealed record ReasonChipViewModel(string Text, ReasonSeverity Severity, string Tooltip);

/// <summary>Monitor tab: 8 tiles, reason chips and the graph.</summary>
public sealed class MonitorViewModel : TabViewModel
{
    /// <summary>Graph window choices (minutes).</summary>
    public static IReadOnlyList<int> GraphChoices { get; } = [1, 3, 10];

    readonly ISettingsStore _settings;
    readonly IGpu _gpu;
    readonly TileViewModel _power = new("ui.tile.power");
    readonly TileViewModel _core = new("ui.tile.core");
    readonly TileViewModel _sm = new("ui.tile.sm");
    readonly TileViewModel _mem = new("ui.tile.mem");
    readonly TileViewModel _vram = new("ui.tile.vram");
    readonly TileViewModel _pstate = new("ui.tile.pstate");
    readonly TileViewModel _temp = new("ui.tile.temp");
    readonly TileViewModel _util = new("ui.tile.util");
    SampleContext? _last;
    ClockReasons? _chipReasons;
    IReadOnlyList<GpuSample> _graphSamples = [];
    int _graphMinutes;
    string _statusText = "";
    bool _hasError;

    /// <summary>Creates the tab.</summary>
    public MonitorViewModel(Localizer localizer, ISettingsStore settings, IGpu gpu) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(gpu);
        _settings = settings;
        _gpu = gpu;
        Tiles = [_power, _core, _sm, _mem, _vram, _pstate, _temp, _util];
        _graphMinutes = Normalize(settings.Current.GraphMinutes);
        SetGraphMinutesCommand = new RelayCommand(p => GraphMinutes = ParseMinutes(p));
        OnLanguageChanged();
    }

    /// <summary>The 8 tiles in display order.</summary>
    public IReadOnlyList<TileViewModel> Tiles { get; }

    /// <summary>Current clock-reason chips.</summary>
    public ObservableCollection<ReasonChipViewModel> Chips { get; } = [];

    /// <summary>Samples shown by the graph (the graph cuts them to <see cref="GraphMinutes"/>).</summary>
    public IReadOnlyList<GpuSample> GraphSamples { get => _graphSamples; private set => SetProperty(ref _graphSamples, value); }

    /// <summary>Graph window in minutes (1, 3 or 10); persisted in the settings.</summary>
    public int GraphMinutes
    {
        get => _graphMinutes;
        set
        {
            int v = Normalize(value);
            if (!SetProperty(ref _graphMinutes, v)) return;
            var s = _settings.Current;
            if (s.GraphMinutes != v) _settings.Save(s with { GraphMinutes = v });
        }
    }

    /// <summary>Selects the graph window; the parameter is the number of minutes.</summary>
    public RelayCommand SetGraphMinutesCommand { get; }

    /// <summary>Error line when the GPU cannot be read.</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>True when the last reading failed.</summary>
    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    /// <inheritdoc/>
    public override void OnLanguageChanged()
    {
        foreach (var t in Tiles) t.Title = L.T(t.TitleKey);
        _chipReasons = null;
        if (_last != null) OnSample(_last);
    }

    /// <inheritdoc/>
    public override void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int v = Normalize(settings.GraphMinutes);
        if (v != _graphMinutes) { _graphMinutes = v; OnPropertyChanged(nameof(GraphMinutes)); }
    }

    /// <inheritdoc/>
    public override void OnSample(SampleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _last = context;
        GraphSamples = context.History;
        var s = context.Sample;
        var c = L.Culture;
        HasError = !s.Ok;
        StatusText = s.Ok ? "" : L.T("ui.monitor.readError", ("error", s.Error ?? _gpu.LastError));
        if (!s.Ok) { foreach (var t in Tiles) { t.Value = Format.Unknown; t.Sub = ""; } return; }

        _power.Value = Format.Watts(s.PowerW, c);
        _power.Unit = "W";
        _power.Sub = L.T("ui.tile.power.sub", ("avg", Format.Watts(context.AveragePower30W, c)), ("limit", Format.WattsWhole(s.LimitW, c)));

        _core.Value = Format.Number(s.CoreMHz, c);
        _core.Unit = "MHz";
        uint maxCore = _gpu.Info?.MaxCoreMHz ?? 0;
        _core.Sub = maxCore > 0 ? L.T("ui.tile.core.sub", ("max", Format.Number(maxCore, c))) : "";

        _sm.Value = Format.Number(s.SmMHz, c);
        _sm.Unit = "MHz";
        _sm.Sub = L.T("ui.tile.sm.sub");

        _mem.Value = Format.Number(s.MemMHz, c);
        _mem.Unit = "MHz";
        _mem.Sub = context.Lock switch
        {
            { Locked: true, LevelMHz: uint lv } => L.T("ui.tile.mem.locked", ("level", Format.Number(lv, c))),
            { Lost: true } => L.T("ui.tile.mem.lost"),
            _ => L.T("ui.tile.mem.auto"),
        };

        _vram.Value = (s.VramUsedMB / 1024.0).ToString("0.0", c);
        _vram.Unit = "GB";
        _vram.Sub = s.VramTotalMB > 0 ? L.T("ui.tile.vram.sub", ("total", (s.VramTotalMB / 1024.0).ToString("0.0", c))) : "";

        _pstate.Value = s.PState;
        _pstate.Unit = "";
        _pstate.Sub = s.UtilGpu >= SessionRecorder.LoadUtilPercent ? L.T("ui.tile.pstate.load")
            : s.Reasons.HasFlag(ClockReasons.GpuIdle) || s.UtilGpu < VramLockService.IdleUtilPercent ? L.T("ui.tile.pstate.idle")
            : L.T("ui.tile.pstate.light");

        _temp.Value = s.TempC.ToString(c);
        _temp.Unit = "°C";
        _temp.Alert = s.TempC >= StabilityMonitor.WarnTempC;
        _temp.Sub = L.T("ui.tile.temp.sub", ("warn", StabilityMonitor.WarnTempC), ("critical", StabilityMonitor.CriticalTempC));

        _util.Value = s.UtilGpu.ToString(c);
        _util.Unit = "%";
        _util.Sub = L.T("ui.tile.util.sub", ("mem", s.UtilMem));

        _power.Alert = ClockReasonText.CostsPerformance(s.Reasons) && s.Reasons.HasFlag(ClockReasons.SwPowerCap);
        UpdateChips(s.Reasons);
    }

    void UpdateChips(ClockReasons reasons)
    {
        if (_chipReasons == reasons) return;
        _chipReasons = reasons;
        Chips.Clear();
        foreach (var tag in ClockReasonText.Tags(reasons))
            Chips.Add(new ReasonChipViewModel(L.T(tag.Key), tag.Severity,
                "0x" + tag.Bits.ToString("X", CultureInfo.InvariantCulture)));
    }

    static int Normalize(int minutes) => GraphChoices.Contains(minutes) ? minutes : 3;

    static int ParseMinutes(object? p) =>
        p is int i ? i : int.TryParse(p?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 3;
}
