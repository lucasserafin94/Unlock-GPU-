using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>One lockable VRAM clock level (radio button).</summary>
public sealed class VramLevelOption : ObservableObject
{
    readonly Action<VramLevelOption> _selected;
    bool _isSelected;

    /// <summary>Creates the option.</summary>
    public VramLevelOption(uint mhz, string label, Action<VramLevelOption> selected)
    {
        MHz = mhz;
        Label = label;
        _selected = selected;
    }

    /// <summary>Clock level in MHz.</summary>
    public uint MHz { get; }
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
/// Tuning tab: clock offsets (driver ranges, step 15 MHz, read-back), VRAM lock, power-limit support and the
/// whole-board budget from the charger rating. All driver calls run off the UI thread.
/// </summary>
public sealed class TuningViewModel : TabViewModel
{
    /// <summary>Offset slider step (MHz).</summary>
    public const int OffsetStep = 15;
    /// <summary>Below this many watts left for CPU and the rest of the board, the budget text turns into a warning.</summary>
    public const double LowRestW = 60;

    readonly IOffsetService _offsets;
    readonly IVramLockService _vram;
    readonly IPowerService _power;
    readonly IGpu _gpu;
    readonly ISettingsStore _settings;
    readonly Action<Exception> _onError;

    bool _coreAvailable, _memAvailable;
    double _coreMin, _coreMax, _memMin, _memMax, _coreOffset, _memOffset;
    int? _coreCurrent, _memCurrent;
    bool _lockEnabled, _lockInitialized, _lockBusy;
    uint _selectedLevel;
    VramLockState? _lockState;
    // Info da GPU lida fora da thread de UI (InitializeAsync em Task.Run ou amostras do monitor).
    GpuInfo? _info;
    string _lockStatus = "";
    PowerSupport _powerSupport = PowerSupport.Unknown;
    double _powerMin, _powerMax, _powerValue, _enforcedW = double.NaN;
    string _adapterText;
    string _budgetText = "";
    bool _budgetLow;

    /// <summary>Creates the tab.</summary>
    public TuningViewModel(Localizer localizer, IGpu gpu, IOffsetService offsets, IVramLockService vram, IPowerService power,
        ISettingsStore settings, BannerViewModel mainBanner) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(vram);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mainBanner);
        _gpu = gpu;
        _offsets = offsets;
        _vram = vram;
        _power = power;
        _settings = settings;
        _onError = ex => mainBanner.Show(ResultText.Exception(L, ex), BannerKind.Error);
        int adapter = settings.Current.AdapterWatts;
        _adapterText = adapter > 0 ? adapter.ToString(CultureInfo.InvariantCulture) : "";

        ApplyOffsetsCommand = new AsyncRelayCommand(ApplyOffsetsAsync, () => _coreAvailable || _memAvailable, _onError);
        ResetOffsetsCommand = new AsyncRelayCommand(ResetOffsetsAsync, () => _coreAvailable || _memAvailable, _onError);
        ApplyPowerCommand = new AsyncRelayCommand(ApplyPowerAsync, () => _powerSupport == PowerSupport.Adjustable, _onError);
        UpdateBudget();
        UpdateLockStatus();
    }

    /// <summary>Read-back strip of this tab.</summary>
    public BannerViewModel ReadBack { get; } = new();

    // ---------------- offsets ----------------

    /// <summary>Core offset can be read and set.</summary>
    public bool CoreAvailable { get => _coreAvailable; private set => SetProperty(ref _coreAvailable, value); }
    /// <summary>Memory offset can be read and set.</summary>
    public bool MemAvailable { get => _memAvailable; private set => SetProperty(ref _memAvailable, value); }
    /// <summary>Core slider minimum (driver).</summary>
    public double CoreMin { get => _coreMin; private set => SetProperty(ref _coreMin, value); }
    /// <summary>Core slider maximum (driver).</summary>
    public double CoreMax { get => _coreMax; private set => SetProperty(ref _coreMax, value); }
    /// <summary>Memory slider minimum (driver).</summary>
    public double MemMin { get => _memMin; private set => SetProperty(ref _memMin, value); }
    /// <summary>Memory slider maximum (driver).</summary>
    public double MemMax { get => _memMax; private set => SetProperty(ref _memMax, value); }

    /// <summary>Core offset chosen on the slider (snapped to 15 MHz).</summary>
    public double CoreOffset
    {
        get => _coreOffset;
        set { if (SetProperty(ref _coreOffset, Snap(value, _coreMin, _coreMax))) OnPropertiesChanged(nameof(CoreOffsetText), nameof(IsDirty)); }
    }

    /// <summary>Memory offset chosen on the slider (snapped to 15 MHz).</summary>
    public double MemOffset
    {
        get => _memOffset;
        set { if (SetProperty(ref _memOffset, Snap(value, _memMin, _memMax))) OnPropertiesChanged(nameof(MemOffsetText), nameof(IsDirty)); }
    }

    /// <summary>"+150 MHz".</summary>
    public string CoreOffsetText => Format.Offset((int)_coreOffset, L.Culture) + " MHz";
    /// <summary>"+1000 MHz".</summary>
    public string MemOffsetText => Format.Offset((int)_memOffset, L.Culture) + " MHz";

    /// <summary>"Range -1000..+1000 MHz · now +0".</summary>
    public string CoreRangeText => RangeText(_coreAvailable, _coreMin, _coreMax, _coreCurrent);
    /// <summary>"Range -2000..+6000 MHz · now +0".</summary>
    public string MemRangeText => RangeText(_memAvailable, _memMin, _memMax, _memCurrent);

    /// <summary>True when the sliders differ from what the driver reports.</summary>
    public bool IsDirty => (_coreAvailable && (int)_coreOffset != (_coreCurrent ?? 0)) || (_memAvailable && (int)_memOffset != (_memCurrent ?? 0));

    /// <summary>Applies both offsets and shows the read-back.</summary>
    public AsyncRelayCommand ApplyOffsetsCommand { get; }
    /// <summary>Sets both offsets to 0.</summary>
    public AsyncRelayCommand ResetOffsetsCommand { get; }

    // ---------------- VRAM lock ----------------

    /// <summary>Lockable levels.</summary>
    public ObservableCollection<VramLevelOption> Levels { get; } = [];

    /// <summary>True when there are levels to lock.</summary>
    public bool HasLevels => Levels.Count > 0;

    /// <summary>The lock switch. Turning it on locks the selected level; off returns to automatic clocks.</summary>
    public bool LockEnabled
    {
        get => _lockEnabled;
        set
        {
            if (_lockBusy || !SetProperty(ref _lockEnabled, value)) return;
            _ = RunLockAsync(value);
        }
    }

    /// <summary>True while a lock/unlock call runs.</summary>
    public bool LockBusy { get => _lockBusy; private set { if (SetProperty(ref _lockBusy, value)) OnPropertyChanged(nameof(CanToggleLock)); } }

    /// <summary>The switch can be used (levels known and no call in progress).</summary>
    public bool CanToggleLock => HasLevels && !_lockBusy;

    /// <summary>Current lock state, translated.</summary>
    public string LockStatus { get => _lockStatus; private set => SetProperty(ref _lockStatus, value); }

    // ---------------- power limit ----------------

    /// <summary>Whether the power limit can be changed (Unknown until probed).</summary>
    public PowerSupport PowerSupport { get => _powerSupport; private set { if (SetProperty(ref _powerSupport, value)) OnPropertiesChanged(nameof(IsPowerAdjustable), nameof(PowerSupportText)); } }
    /// <summary>True when the limit is adjustable.</summary>
    public bool IsPowerAdjustable => _powerSupport == PowerSupport.Adjustable;
    /// <summary>Explanation of the support state.</summary>
    public string PowerSupportText => _powerSupport switch
    {
        PowerSupport.Adjustable => L.T("ui.power.adjustable"),
        PowerSupport.NotSupported => L.T("ui.power.notSupported"),
        _ => L.T("ui.power.unknown"),
    };
    /// <summary>Driver limits and the enforced value.</summary>
    public string PowerLimitsText
    {
        get
        {
            var info = _info;
            var c = L.Culture;
            return L.T("ui.power.limits",
                ("enforced", Format.WattsWhole(_enforcedW, c)),
                ("default", Format.WattsWhole(info?.DefaultLimitW ?? double.NaN, c)),
                ("min", Format.WattsWhole(info?.MinLimitW ?? double.NaN, c)),
                ("max", Format.WattsWhole(info?.MaxLimitW ?? double.NaN, c)));
        }
    }
    /// <summary>Slider minimum.</summary>
    public double PowerMin { get => _powerMin; private set => SetProperty(ref _powerMin, value); }
    /// <summary>Slider maximum.</summary>
    public double PowerMax { get => _powerMax; private set => SetProperty(ref _powerMax, value); }
    /// <summary>Requested limit (W).</summary>
    public double PowerValue { get => _powerValue; set { if (SetProperty(ref _powerValue, Math.Round(value))) OnPropertyChanged(nameof(PowerValueText)); } }
    /// <summary>"175 W".</summary>
    public string PowerValueText => Format.WattsWhole(_powerValue, L.Culture) + " W";
    /// <summary>Applies the power limit.</summary>
    public AsyncRelayCommand ApplyPowerCommand { get; }

    // ---------------- budget ----------------

    /// <summary>Charger rating typed by the user (W).</summary>
    public string AdapterWattsText
    {
        get => _adapterText;
        set
        {
            if (!SetProperty(ref _adapterText, value)) return;
            int w = ParseAdapterWatts(value);
            var s = _settings.Current;
            if (s.AdapterWatts != w) _settings.Save(s with { AdapterWatts = w });
            UpdateBudget();
        }
    }

    /// <summary>Budget split text.</summary>
    public string BudgetText { get => _budgetText; private set => SetProperty(ref _budgetText, value); }
    /// <summary>True when little is left for the CPU.</summary>
    public bool BudgetLow { get => _budgetLow; private set => SetProperty(ref _budgetLow, value); }

    // ---------------- lifecycle ----------------

    /// <summary>Reads offsets, levels and the power support (background), then fills the tab.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await ReloadOffsetsAsync().ConfigureAwait(true);
            var levels = await Task.Run(() => _vram.Levels).ConfigureAwait(true);
            BuildLevels(levels);
            var support = await Task.Run(_power.Probe).ConfigureAwait(true);
            var info = await Task.Run(() => _gpu.Info).ConfigureAwait(true);
            if (info != null) _info = info;
            ApplyPowerRange();
            _enforcedW = await Task.Run(_gpu.EnforcedLimitW).ConfigureAwait(true);
            PowerValue = double.IsNaN(_enforcedW) ? PowerMin : _enforcedW;
            PowerSupport = support;
            OnPropertyChanged(nameof(PowerLimitsText));
            UpdateBudget();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    /// <summary>Re-reads the offsets from the driver (after a profile, a reset or the tuner).</summary>
    public async Task ReloadOffsetsAsync()
    {
        var (core, mem) = await Task.Run(() => (_offsets.Get(ClockDomain.Core), _offsets.Get(ClockDomain.Memory))).ConfigureAwait(true);
        CoreAvailable = core != null;
        MemAvailable = mem != null;
        if (core != null) { CoreMin = core.MinMHz; CoreMax = core.MaxMHz; _coreCurrent = core.CurrentMHz; CoreOffset = core.CurrentMHz; }
        if (mem != null) { MemMin = mem.MinMHz; MemMax = mem.MaxMHz; _memCurrent = mem.CurrentMHz; MemOffset = mem.CurrentMHz; }
        OnPropertiesChanged(nameof(CoreRangeText), nameof(MemRangeText), nameof(IsDirty));
        ApplyOffsetsCommand.RaiseCanExecuteChanged();
        ResetOffsetsCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc/>
    public override void OnSample(SampleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Info != null && !Equals(context.Info, _info))
        {
            bool first = _info == null;
            _info = context.Info;
            if (first) ApplyPowerRange();
            OnPropertyChanged(nameof(PowerLimitsText));
        }
        if (context.Sample.Ok && !double.IsNaN(context.Sample.LimitW)
            && (double.IsNaN(_enforcedW) || Math.Abs(context.Sample.LimitW - _enforcedW) > 0.05))
        {
            _enforcedW = context.Sample.LimitW;
            OnPropertyChanged(nameof(PowerLimitsText));
            UpdateBudget();
        }
        _lockState = context.Lock;
        if (_lockState != null && !_lockBusy)
        {
            bool sync = !_lockInitialized || _lockState.Lost || (_lockState.Locked && !_lockEnabled);
            if (sync)
            {
                _lockInitialized = true;
                bool on = _lockState.Locked && !_lockState.Lost;
                if (on && _lockState.LevelMHz is uint lv) SelectLevelSilently(lv);
                if (_lockEnabled != on) { _lockEnabled = on; OnPropertyChanged(nameof(LockEnabled)); }
            }
        }
        UpdateLockStatus();
    }

    /// <inheritdoc/>
    public override void OnLanguageChanged()
    {
        OnPropertiesChanged(nameof(CoreOffsetText), nameof(MemOffsetText), nameof(CoreRangeText), nameof(MemRangeText),
            nameof(PowerSupportText), nameof(PowerLimitsText), nameof(PowerValueText));
        UpdateBudget();
        UpdateLockStatus();
    }

    /// <inheritdoc/>
    public override void OnSettingsChanged(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Same rule as the setter: an out-of-range entry is stored as 0, so it must not be wiped while being typed.
        int current = ParseAdapterWatts(_adapterText);
        if (settings.AdapterWatts != current)
        {
            _adapterText = settings.AdapterWatts > 0 ? settings.AdapterWatts.ToString(CultureInfo.InvariantCulture) : "";
            OnPropertyChanged(nameof(AdapterWattsText));
            UpdateBudget();
        }
    }

    // ---------------- actions ----------------

    async Task ApplyOffsetsAsync()
    {
        int core = _coreAvailable ? (int)_coreOffset : _coreCurrent ?? 0;
        int mem = _memAvailable ? (int)_memOffset : _memCurrent ?? 0;
        var result = await Task.Run(() => _offsets.Apply(core, mem)).ConfigureAwait(true);
        ReadBack.Show(ResultText.Describe(L, result), result.Ok ? BannerKind.Success : BannerKind.Error);
        await ReloadOffsetsAsync().ConfigureAwait(true);
    }

    async Task ResetOffsetsAsync()
    {
        var result = await Task.Run(() => _offsets.Apply(0, 0)).ConfigureAwait(true);
        ReadBack.Show(ResultText.Describe(L, result), result.Ok ? BannerKind.Success : BannerKind.Error);
        await ReloadOffsetsAsync().ConfigureAwait(true);
    }

    async Task ApplyPowerAsync()
    {
        double w = _powerValue;
        var result = await Task.Run(() => _power.Apply(w)).ConfigureAwait(true);
        PowerSupport = _power.Support;
        ReadBack.Show(ResultText.Describe(L, result), result.Ok ? BannerKind.Success : BannerKind.Error);
        _enforcedW = await Task.Run(_gpu.EnforcedLimitW).ConfigureAwait(true);
        OnPropertyChanged(nameof(PowerLimitsText));
        UpdateBudget();
    }

    async Task RunLockAsync(bool enable)
    {
        if (enable && _selectedLevel == 0)
        {
            ReadBack.Show(L.T("ui.vram.selectLevel"), BannerKind.Warning);
            _lockEnabled = false;
            OnPropertyChanged(nameof(LockEnabled));
            return;
        }
        LockBusy = true;
        try
        {
            uint level = _selectedLevel;
            var result = await Task.Run(() => enable ? _vram.Lock(level) : _vram.Unlock()).ConfigureAwait(true);
            ReadBack.Show(ResultText.Describe(L, result), result.Ok ? BannerKind.Success : BannerKind.Error);
            if (!result.Ok)
            {
                _lockEnabled = !enable;
                OnPropertyChanged(nameof(LockEnabled));
            }
            _lockInitialized = true;
        }
        catch (Exception ex)
        {
            _lockEnabled = !enable;
            OnPropertyChanged(nameof(LockEnabled));
            _onError(ex);
        }
        finally
        {
            LockBusy = false;
            UpdateLockStatus();
        }
    }

    void OnLevelSelected(VramLevelOption option)
    {
        foreach (var o in Levels) if (!ReferenceEquals(o, option)) o.SetSilently(false);
        bool changed = _selectedLevel != option.MHz;
        _selectedLevel = option.MHz;
        if (changed && _lockEnabled && !_lockBusy) _ = RunLockAsync(true);
    }

    void BuildLevels(IReadOnlyList<uint> levels)
    {
        Levels.Clear();
        foreach (uint mhz in levels)
            Levels.Add(new VramLevelOption(mhz, Format.Number(mhz, L.Culture) + " MHz", OnLevelSelected));
        if (Levels.Count > 0)
        {
            // Uma amostra pode ter chegado antes da lista existir (o SelectLevelSilently dela nao achou nada):
            // se a VRAM ja esta travada num nivel da lista, seleciona esse; senao o primeiro.
            uint pick = _lockState is { Locked: true, Lost: false, LevelMHz: uint lv } && Levels.Any(o => o.MHz == lv)
                ? lv
                : Levels[0].MHz;
            SelectLevelSilently(pick);
        }
        OnPropertiesChanged(nameof(HasLevels), nameof(CanToggleLock));
    }

    void ApplyPowerRange()
    {
        var info = _info;
        PowerMin = info is { MinLimitW: > 0 } ? info.MinLimitW : 0;
        PowerMax = info is { MaxLimitW: > 0 } ? info.MaxLimitW : 0;
    }

    void SelectLevelSilently(uint mhz)
    {
        foreach (var o in Levels) o.SetSilently(o.MHz == mhz);
        if (Levels.Any(o => o.MHz == mhz)) _selectedLevel = mhz;
    }

    void UpdateLockStatus()
    {
        var c = L.Culture;
        LockStatus = _lockState switch
        {
            null => L.T("ui.vram.status.unknown"),
            { Lost: true } => L.T("ui.vram.status.lost"),
            { Locked: true, LevelMHz: uint lv, OffsetMHz: int off } =>
                L.T("ui.vram.status.lockedOffset", ("level", Format.Number(lv, c)), ("offset", Format.Offset(off, c))),
            { Locked: true, LevelMHz: uint lv } => L.T("ui.vram.status.locked", ("level", Format.Number(lv, c))),
            _ => _lockEnabled ? L.T("ui.vram.status.confirming") : L.T("ui.vram.status.auto"),
        };
    }

    void UpdateBudget()
    {
        var c = L.Culture;
        int adapter = int.TryParse(_adapterText, NumberStyles.Integer, c, out int v) && v > 0 ? v : 0;
        if (adapter <= 0)
        {
            BudgetText = L.T("ui.budget.enterAdapter");
            BudgetLow = false;
            return;
        }
        if (double.IsNaN(_enforcedW))
        {
            BudgetText = L.T("ui.budget.noLimit");
            BudgetLow = false;
            return;
        }
        double rest = adapter - _enforcedW;
        string text = L.T("ui.budget.split", ("gpu", Format.WattsWhole(_enforcedW, c)), ("rest", Format.WattsWhole(rest, c)), ("adapter", adapter));
        BudgetLow = rest < LowRestW;
        if (BudgetLow) text += Environment.NewLine + L.T("ui.budget.low");
        BudgetText = text;
    }

    int ParseAdapterWatts(string? text) =>
        int.TryParse(text, NumberStyles.Integer, L.Culture, out int v) && v is > 0 and <= 1000 ? v : 0;

    string RangeText(bool available, double min, double max, int? current)
    {
        if (!available) return L.T("ui.offset.unavailable");
        var c = L.Culture;
        return L.T("ui.offset.range", ("min", Format.Offset((int)min, c)), ("max", Format.Offset((int)max, c)),
            ("current", current is int cur ? Format.Offset(cur, c) : Format.Unknown));
    }

    /// <summary>Rounds to the 15 MHz grid that contains 0 and keeps the value inside the driver range.</summary>
    public static double Snap(double value, double min, double max)
    {
        if (max < min) return 0;
        double lo = Math.Ceiling(min / OffsetStep) * OffsetStep;
        double hi = Math.Floor(max / OffsetStep) * OffsetStep;
        double v = Math.Round(value / OffsetStep, MidpointRounding.AwayFromZero) * OffsetStep;
        return lo > hi ? Math.Clamp(Math.Round(value), min, max) : Math.Clamp(v, lo, hi);
    }
}
