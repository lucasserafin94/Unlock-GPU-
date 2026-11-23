using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Oem;

namespace UnlockGpu.App.ViewModels;

/// <summary>
/// OEM tab: read-only view of the Uniwill embedded-controller power settings (mode, cTGP/TPP/Dynamic Boost offsets,
/// CPU PL1/PL2/PL4). Nothing is written; the panel says writes come in a later build.
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

    /// <summary>Detects the platform and reads the EC (background; read operations only).</summary>
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
        Build();
    }

    /// <inheritdoc/>
    public override void OnLanguageChanged() => Build();

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

    string OnOff(bool? v) => v switch { true => L.T("ui.common.on"), false => L.T("ui.common.off"), _ => Format.Unknown };

    string Watts(int? w) => w is int v ? v.ToString(L.Culture) + " W" : Format.Unknown;

    static string Raw(byte? b) => b is byte v ? " (0x" + v.ToString("X2", CultureInfo.InvariantCulture) + ")" : "";
}
