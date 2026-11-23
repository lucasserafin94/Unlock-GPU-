using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Oem;

namespace UnlockGpu.App.ViewModels;

/// <summary>One EC performance mode (radio button).</summary>
public sealed class OemModeOption : ObservableObject
{
    readonly Action<OemModeOption> _selected;
    bool _isSelected;

    /// <summary>Creates the option.</summary>
    public OemModeOption(OemPerformanceMode mode, string label, Action<OemModeOption> selected)
    {
        Mode = mode;
        Label = label;
        _selected = selected;
    }

    /// <summary>The mode this radio represents.</summary>
    public OemPerformanceMode Mode { get; }
    /// <summary>Display text.</summary>
    public string Label { get; }

    /// <summary>Radio state.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value) _selected(this);
        }
    }

    /// <summary>Sets the radio state without notifying the owner.</summary>
    internal void SetSilently(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>
/// OEM tab: Uniwill embedded-controller power settings. The top card reads the current values; the apply card
/// writes only what changes (mode 0x0751, cTGP/Dynamic Boost enable bits and offsets) and verifies with a fresh read.
/// </summary>
public sealed class OemViewModel : TabViewModel
{
    readonly IOemPowerService _oem;
    readonly IGpu _gpu;
    OemDetection? _detection;
    OemPowerState? _state;
    string? _readErrorKey;
    string _statusText = "";
    string _identityText = "";
    bool _hasState;
    bool _isWarning;

    // apply panel state (mirrors the last read, edited by the user)
    OemModeOption? _selectedMode;
    bool _ctgpEnabled;
    int _ctgpOffsetW;
    bool _dbEnabled;
    int _dbOffsetW;
    string _applyStatusText = "";
    bool _applyIsWarning;

    /// <summary>Creates the tab.</summary>
    public OemViewModel(Localizer localizer, IOemPowerService oem, IGpu gpu) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(oem);
        ArgumentNullException.ThrowIfNull(gpu);
        _oem = oem;
        _gpu = gpu;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, null, ex =>
        {
            _readErrorKey = null;
            StatusText = ResultText.Exception(L, ex);
            IsWarning = true;
        });
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApplyNow, ex =>
        {
            ApplyStatusText = ResultText.Exception(L, ex);
            ApplyIsWarning = true;
        });
        BuildModeOptions();
        StatusText = L.T("ui.oem.reading");
    }

    /// <summary>Detection / read status.</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    /// <summary>System and board names.</summary>
    public string IdentityText { get => _identityText; private set => SetProperty(ref _identityText, value); }
    /// <summary>True when the EC values were read.</summary>
    public bool HasState { get => _hasState; private set => SetProperty(ref _hasState, value); }
    /// <summary>Status is a warning (not available, access denied, unverified board).</summary>
    public bool IsWarning { get => _isWarning; private set => SetProperty(ref _isWarning, value); }
    /// <summary>EC values.</summary>
    public ObservableCollection<SummaryLine> Fields { get; } = [];
    /// <summary>Detects and reads again.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    // ===== apply panel =====

    /// <summary>Performance mode radios (Power save / Enthusiast / Overboost).</summary>
    public ObservableCollection<OemModeOption> ModeOptions { get; } = [];
    /// <summary>The selected performance mode radio, or null when the register does not decode to a known mode.</summary>
    public OemModeOption? SelectedMode { get => _selectedMode; private set => SetProperty(ref _selectedMode, value); }
    /// <summary>cTGP enable bit (0x0743 bit2).</summary>
    public bool CtgpEnabled
    {
        get => _ctgpEnabled;
        set => SetProperty(ref _ctgpEnabled, value);
    }
    /// <summary>cTGP offset in W above base TGP (0-160).</summary>
    public int CtgpOffsetW
    {
        get => _ctgpOffsetW;
        set
        {
            if (SetProperty(ref _ctgpOffsetW, value)) OnPropertyChanged(nameof(CtgpOffsetText));
        }
    }
    /// <summary>cTGP offset for display.</summary>
    public string CtgpOffsetText => CtgpOffsetW.ToString(L.Culture) + " W";
    /// <summary>Highest cTGP offset the service accepts (firmware range).</summary>
    public int CtgpOffsetMax => UniwillOemPowerService.CtgpOffsetMaxW;
    /// <summary>Dynamic Boost enable bit (0x0743 bit1).</summary>
    public bool DbEnabled
    {
        get => _dbEnabled;
        set => SetProperty(ref _dbEnabled, value);
    }
    /// <summary>Dynamic Boost offset in W (0-25: firmware maximum).</summary>
    public int DbOffsetW
    {
        get => _dbOffsetW;
        set
        {
            if (SetProperty(ref _dbOffsetW, value)) OnPropertyChanged(nameof(DbOffsetText));
        }
    }
    /// <summary>Dynamic Boost offset for display.</summary>
    public string DbOffsetText => DbOffsetW.ToString(L.Culture) + " W";
    /// <summary>Highest Dynamic Boost offset the firmware accepts.</summary>
    public int DbOffsetMax => UniwillOemPowerService.DbOffsetMaxW;
    /// <summary>Apply result / progress text.</summary>
    public string ApplyStatusText { get => _applyStatusText; private set => SetProperty(ref _applyStatusText, value); }
    /// <summary>Apply status is a warning (write failed).</summary>
    public bool ApplyIsWarning { get => _applyIsWarning; private set => SetProperty(ref _applyIsWarning, value); }
    /// <summary>Writes the changed settings to the EC and verifies them.</summary>
    public AsyncRelayCommand ApplyCommand { get; }

    /// <summary>Detects the platform and reads the EC (background).</summary>
    public async Task RefreshAsync()
    {
        StatusText = L.T("ui.oem.reading");
        var (detection, state, errorKey) = await Task.Run(() =>
        {
            var d = _oem.Detect();
            if (d.Status != OemStatus.Available) return (d, (OemPowerState?)null, (string?)null);
            try { return (d, _oem.ReadState(), (string?)null); }
            catch (UnauthorizedAccessException e) { return (d, null, e.Message); }
            catch (InvalidOperationException e) { return (d, null, e.Message); }
        }).ConfigureAwait(true);
        _detection = detection;
        _state = state;
        _readErrorKey = errorKey;
        SyncControlsFromState();
        Build();
    }

    /// <summary>Writes the requested settings: reads first, writes only what differs, verifies with a fresh read.</summary>
    async Task ApplyAsync()
    {
        if (_state == null || _detection?.Status != OemStatus.Available) return;
        string requested = DescribeRequest();
        ApplyStatusText = L.T("oem.applying");
        ApplyIsWarning = false;

        var request = new OemPowerRequest
        {
            PerformanceMode = SelectedMode?.Mode,
            CtgpEnabled = CtgpEnabled,
            CtgpOffsetW = CtgpOffsetW,
            DynamicBoostEnabled = DbEnabled,
            DynamicBoostOffsetW = DbOffsetW,
        };
        OemApplyResult result = await Task.Run(() => _oem.Apply(request)).ConfigureAwait(true);

        if (result.ReadBack is { } readBack)
        {
            _state = readBack;
            SyncControlsFromState();
            Build();
        }
        if (result.Ok && result.ReadBack is { } verified)
        {
            ApplyStatusText = L.T("oem.applied", ("requested", requested), ("state", Describe(verified)));
            ApplyIsWarning = false;
        }
        else
        {
            ApplyStatusText = L.T(result.ErrorKey ?? "oem.ecWriteFailed");
            ApplyIsWarning = true;
        }
    }

    /// <inheritdoc/>
    public override void OnLanguageChanged()
    {
        BuildModeOptions();
        SyncControlsFromState();
        Build();
    }

    bool CanApplyNow() => HasState && _detection?.Status == OemStatus.Available;

    void BuildModeOptions()
    {
        ModeOptions.Clear();
        foreach (OemPerformanceMode mode in new[] { OemPerformanceMode.PowerSave, OemPerformanceMode.Enthusiast, OemPerformanceMode.Overboost })
            ModeOptions.Add(new OemModeOption(mode, L.T(UniwillEc.ModeKey(mode)), SelectMode));
    }

    void SelectMode(OemModeOption option) => SelectedMode = option;

    /// <summary>Points the apply controls at the last read state (radios included).</summary>
    void SyncControlsFromState()
    {
        var s = _state;
        if (s == null) return;
        OemModeOption? match = null;
        foreach (OemModeOption option in ModeOptions)
        {
            bool selected = option.Mode == s.PerformanceMode;
            option.SetSilently(selected);
            if (selected) match = option;
        }
        SelectedMode = match;
        CtgpEnabled = s.CtgpEnabled ?? false;
        CtgpOffsetW = s.CtgpOffsetW is int c ? Math.Clamp(c, 0, UniwillOemPowerService.CtgpOffsetMaxW) : 0;
        DbEnabled = s.DynamicBoostEnabled ?? false;
        DbOffsetW = s.DynamicBoostOffsetW is int d ? Math.Clamp(d, 0, UniwillOemPowerService.DbOffsetMaxW) : 0;
    }

    void Build()
    {
        Fields.Clear();
        var d = _detection;
        if (d == null) return;
        var id = d.Identity;
        IdentityText = string.Join(" · ", new[] { id.SystemVendor, id.SystemProduct, id.BoardVendor, id.BoardProduct }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        string status = L.T(d.MessageKey);
        if (_readErrorKey != null) status += Environment.NewLine + L.T(_readErrorKey);
        var s = _state;
        if (s is { FailedRegisters.Count: > 0 }) status += Environment.NewLine + L.T("oem.ecReadFailed");
        StatusText = status;
        IsWarning = d.Status != OemStatus.Available || !d.BoardVerified || _readErrorKey != null || s is { FailedRegisters.Count: > 0 };
        HasState = s != null;
        if (s == null) return;

        var c = L.Culture;
        Add("oem.field.mode", L.T(UniwillEc.ModeKey(s.PerformanceMode)) + Raw(s.PerformanceModeRaw));
        Add("oem.field.ctgpEnabled", OnOff(s.CtgpEnabled));
        Add("oem.field.ctgpOffset", Watts(s.CtgpOffsetW));
        Add("oem.field.tppOffset", Watts(s.TppOffsetW));
        Add("oem.field.dbEnabled", OnOff(s.DynamicBoostEnabled));
        Add("oem.field.dbOffset", Watts(s.DynamicBoostOffsetW));
        double baseTgp = _gpu.Info?.DefaultLimitW ?? double.NaN;
        if (!double.IsNaN(baseTgp))
        {
            string tgp = s.ConfiguredTgpW(baseTgp) is double t ? Format.WattsWhole(t, c) + " W" : L.T("ui.oem.notConfigured");
            if (s.ConfiguredTgpWithBoostW(baseTgp) is double tb && s.DynamicBoostEnabled == true)
                tgp += " " + L.T("ui.oem.withBoost", ("w", Format.WattsWhole(tb, c)));
            Add("oem.field.configuredTgp", tgp + " " + L.T("ui.oem.baseTgp", ("w", Format.WattsWhole(baseTgp, c))));
        }
        Add("oem.field.customProfile", OnOff(s.CustomProfileMode));
        Add("oem.field.pl1", Watts(s.CpuPl1W));
        Add("oem.field.pl2", Watts(s.CpuPl2W));
        Add("oem.field.pl4", Watts(s.CpuPl4W) + (s.Pl4Doubled == true ? " " + L.T("ui.oem.pl4Doubled") : ""));
    }

    void Add(string key, string value) => Fields.Add(new SummaryLine(L.T(key), value));

    /// <summary>One-line description of the requested settings (for the apply result).</summary>
    string DescribeRequest() => string.Join(" · ", new[]
    {
        SelectedMode is { } m ? L.T(UniwillEc.ModeKey(m.Mode)) : null,
        "cTGP " + OnOffB(CtgpEnabled) + " +" + CtgpOffsetW.ToString(L.Culture) + " W",
        "DB " + OnOffB(DbEnabled) + " +" + DbOffsetW.ToString(L.Culture) + " W",
    }.Where(x => x != null));

    /// <summary>One-line description of a firmware state (for the apply result).</summary>
    string Describe(OemPowerState s) => string.Join(" · ", new[]
    {
        s.PerformanceMode != OemPerformanceMode.Unknown ? L.T(UniwillEc.ModeKey(s.PerformanceMode)) : null,
        "cTGP " + OnOff(s.CtgpEnabled) + " +" + (s.CtgpOffsetW?.ToString(L.Culture) ?? "?") + " W",
        "DB " + OnOff(s.DynamicBoostEnabled) + " +" + (s.DynamicBoostOffsetW?.ToString(L.Culture) ?? "?") + " W",
    }.Where(x => x != null));

    string OnOffB(bool v) => v ? L.T("ui.common.on") : L.T("ui.common.off");

    string OnOff(bool? v) => v switch { true => L.T("ui.common.on"), false => L.T("ui.common.off"), _ => Format.Unknown };

    string Watts(int? w) => w is int v ? v.ToString(L.Culture) + " W" : Format.Unknown;

    static string Raw(byte? b) => b is byte v ? " (0x" + v.ToString("X2", CultureInfo.InvariantCulture) + ")" : "";
}
