using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.ViewModels;

/// <summary>One row of the live step table.</summary>
/// <param name="Offset">"+150 MHz".</param>
/// <param name="Score">Score text.</param>
/// <param name="Gain">Gain against the best so far.</param>
/// <param name="VramErrors">VRAM errors found.</param>
/// <param name="Note">Translated stop reason, when this step stopped the run.</param>
/// <param name="IsBad">Errors or stop on this step.</param>
public sealed record TuneStepRow(string Offset, string Score, string Gain, string VramErrors, string Note, bool IsBad);

/// <summary>
/// Auto-tune tab. Runs <see cref="IAutoTuner"/> in the background with live progress. The tuner applies the best
/// offset when it ends normally; the user then keeps it (Apply) or restores the offsets from before the run (Discard).
/// </summary>
public sealed class AutoTuneViewModel : TabViewModel
{
    readonly IAutoTuner _tuner;
    readonly IOffsetService _offsets;
    readonly IStabilityMonitor _stability;
    readonly BannerViewModel _banner;
    readonly Action<Exception> _onError;
    CancellationTokenSource? _cts;
    Task? _tunerTask;
    TuneResult? _result;
    TuneTarget _resultTarget;
    (int Core, int Mem)? _before;
    bool _targetCore;
    string _stepText = "150";
    string _maxText = "";
    string _secondsText = "10";
    string _maxTempText = StabilityMonitor.WarnTempC.ToString(CultureInfo.InvariantCulture);
    bool _runVramTest = true;
    bool _acknowledged;
    bool _isRunning;
    bool _applying; // Keep/Discard gravando offsets; bloqueia Run/Keep/Discard ate terminar
    string _statusText = "";
    string _resultSummary = "";

    /// <summary>Creates the tab.</summary>
    public AutoTuneViewModel(Localizer localizer, IAutoTuner tuner, IOffsetService offsets, IStabilityMonitor stability,
        BannerViewModel banner) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(tuner);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(stability);
        ArgumentNullException.ThrowIfNull(banner);
        _tuner = tuner;
        _offsets = offsets;
        _stability = stability;
        _banner = banner;
        _onError = ex => _banner.Show(ResultText.Exception(L, ex), BannerKind.Error);
        RunCommand = new AsyncRelayCommand(RunAsync, () => _acknowledged && !_isRunning && !_applying, _onError);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _isRunning);
        KeepCommand = new AsyncRelayCommand(KeepAsync, () => _result != null && !_isRunning && !_applying, _onError);
        DiscardCommand = new AsyncRelayCommand(DiscardAsync, () => _result != null && !_isRunning && !_applying && _before != null, _onError);
    }

    /// <summary>Raised after the offsets changed (so the Tuning tab re-reads them).</summary>
    public event EventHandler? OffsetsChanged;

    /// <summary>Tune the core clock (otherwise memory).</summary>
    public bool TargetCore { get => _targetCore; set { if (SetProperty(ref _targetCore, value)) OnPropertyChanged(nameof(TargetMemory)); } }
    /// <summary>Tune the memory clock.</summary>
    public bool TargetMemory { get => !_targetCore; set => TargetCore = !value; }
    /// <summary>Step in MHz (multiple of 15).</summary>
    public string StepText { get => _stepText; set => SetProperty(ref _stepText, value); }
    /// <summary>Maximum offset in MHz; empty = driver maximum.</summary>
    public string MaxOffsetText { get => _maxText; set => SetProperty(ref _maxText, value); }
    /// <summary>Measurement seconds per step.</summary>
    public string SampleSecondsText { get => _secondsText; set => SetProperty(ref _secondsText, value); }
    /// <summary>Temperature that stops the run (°C).</summary>
    public string MaxTempText { get => _maxTempText; set => SetProperty(ref _maxTempText, value); }
    /// <summary>Run the VRAM pattern test on memory steps.</summary>
    public bool RunVramTest { get => _runVramTest; set => SetProperty(ref _runVramTest, value); }
    /// <summary>The user ticked "I understand the risks".</summary>
    public bool Acknowledged { get => _acknowledged; set { if (SetProperty(ref _acknowledged, value)) RunCommand.RaiseCanExecuteChanged(); } }
    /// <summary>A run is in progress.</summary>
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) RaiseAll(); } }
    /// <summary>Progress line.</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    /// <summary>Result summary.</summary>
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    /// <summary>A result waits for Apply/Discard.</summary>
    public bool HasResult => _result != null;
    /// <summary>Live step table.</summary>
    public ObservableCollection<TuneStepRow> Steps { get; } = [];
    /// <summary>True when the table has rows.</summary>
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Starts the run.</summary>
    public AsyncRelayCommand RunCommand { get; }
    /// <summary>Cancels the run (the tuner restores the original offset).</summary>
    public RelayCommand CancelCommand { get; }
    /// <summary>Keeps the best offset.</summary>
    public AsyncRelayCommand KeepCommand { get; }
    /// <summary>Restores the offsets from before the run.</summary>
    public AsyncRelayCommand DiscardCommand { get; }

    /// <summary>Cancels a run in progress (app exit).</summary>
    public void CancelRun() => _cts?.Cancel();

    /// <summary>
    /// Cancels a run in progress and blocks until the tuner has restored the original offset (or
    /// <paramref name="timeout"/> elapses). Used at app exit: without it the process could end while a test offset
    /// is still applied. Safe on the UI thread because the tuner never needs it to finish.
    /// </summary>
    /// <returns>True when no run is left in progress.</returns>
    public bool CancelAndWait(TimeSpan timeout)
    {
        var task = _tunerTask;
        _cts?.Cancel();
        if (task == null || task.IsCompleted) return true;
        try { return task.Wait(timeout); }
        catch (AggregateException) { return true; }
    }

    async Task RunAsync()
    {
        if (!TryBuildOptions(out var options, out string error))
        {
            _banner.Show(error, BannerKind.Warning);
            return;
        }
        Steps.Clear();
        OnPropertyChanged(nameof(HasSteps));
        _result = null;
        ResultSummary = "";
        OnPropertyChanged(nameof(HasResult));
        IsRunning = true;
        StatusText = L.T("ui.tune.status.baseline");
        using var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            _before = await Task.Run(() =>
            {
                var c = _offsets.Get(ClockDomain.Core);
                var m = _offsets.Get(ClockDomain.Memory);
                return c != null && m != null ? (c.CurrentMHz, m.CurrentMHz) : ((int, int)?)null;
            }).ConfigureAwait(true);

            var progress = new Progress<TuneStep>(OnStep);
            var tunerTask = Task.Run(() => _tuner.RunAsync(options, progress, cts.Token), CancellationToken.None);
            _tunerTask = tunerTask;
            var result = await tunerTask.ConfigureAwait(true);
            _result = result;
            _resultTarget = options.Target;
            ShowResult(result);
            if (result.StopReasonKey == TuneStopReasons.ScoreDrop && result.BestScore > 0 && result.Steps.Count > 0)
            {
                double last = result.Steps[^1].Score;
                if (last > 0) _stability.ReportBenchmarkDrop((result.BestScore - last) / result.BestScore * 100.0);
            }
        }
        finally
        {
            _cts = null;
            _tunerTask = null;
            IsRunning = false;
            StatusText = "";
            OnPropertyChanged(nameof(HasResult));
            OffsetsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void OnStep(TuneStep step)
    {
        var c = L.Culture;
        bool baseline = step.GainPercent is null && Steps.Count == 0;
        Steps.Add(new TuneStepRow(
            Format.Offset(step.OffsetMHz, c) + " MHz",
            Format.OneDecimal(step.Score, c),
            baseline ? L.T("ui.tune.baseline") : step.GainPercent is double g ? (g >= 0 ? "+" : "") + g.ToString("0.00", c) + " %" : Format.Unknown,
            step.VramErrors.ToString(c),
            step.StopReasonKey is string k ? L.T(k) : "",
            step.VramErrors > 0 || step.StopReasonKey != null));
        OnPropertyChanged(nameof(HasSteps));
        StatusText = L.T("ui.tune.status.step", ("offset", Format.Offset(step.OffsetMHz, c)), ("count", Steps.Count));
    }

    void ShowResult(TuneResult r)
    {
        var c = L.Culture;
        double gain = r.BaselineScore > 0 ? (r.BestScore - r.BaselineScore) / r.BaselineScore * 100.0 : double.NaN;
        ResultSummary = L.T("ui.tune.result",
            ("offset", Format.Offset(r.BestOffsetMHz, c)),
            ("baseline", Format.OneDecimal(r.BaselineScore, c)),
            ("best", Format.OneDecimal(r.BestScore, c)),
            ("gain", double.IsNaN(gain) ? Format.Unknown : gain.ToString("0.0", c)),
            ("unit", L.T("bench.scoreUnit")))
            + Environment.NewLine + L.T(r.StopReasonKey);
    }

    async Task KeepAsync()
    {
        if (_result == null) return;
        if (_applying) return;
        var r = _result;
        var target = _resultTarget;
        SetApplying(true);
        try
        {
            var applied = await Task.Run(() =>
            {
                int core = _offsets.Get(ClockDomain.Core)?.CurrentMHz ?? 0;
                int mem = _offsets.Get(ClockDomain.Memory)?.CurrentMHz ?? 0;
                return target == TuneTarget.Core ? _offsets.Apply(r.BestOffsetMHz, mem) : _offsets.Apply(core, r.BestOffsetMHz);
            }).ConfigureAwait(true);
            _banner.Show(ResultText.Describe(L, applied), applied.Ok ? BannerKind.Success : BannerKind.Error);
            ClearResult();
        }
        finally
        {
            SetApplying(false);
        }
    }

    async Task DiscardAsync()
    {
        if (_applying) return;
        if (_before is not (int core, int mem)) return;
        SetApplying(true);
        try
        {
            var applied = await Task.Run(() => _offsets.Apply(core, mem)).ConfigureAwait(true);
            _banner.Show(ResultText.Describe(L, applied), applied.Ok ? BannerKind.Success : BannerKind.Error);
            ClearResult();
        }
        finally
        {
            SetApplying(false);
        }
    }

    void SetApplying(bool value)
    {
        _applying = value;
        RaiseAll();
    }

    void ClearResult()
    {
        _result = null;
        OnPropertyChanged(nameof(HasResult));
        RaiseAll();
        OffsetsChanged?.Invoke(this, EventArgs.Empty);
    }

    bool TryBuildOptions(out TuneOptions options, out string error)
    {
        options = new TuneOptions();
        var c = L.Culture;
        if (!int.TryParse(_stepText, NumberStyles.Integer, c, out int step) || step < 15 || step > 1000 || step % 15 != 0)
        {
            error = L.T("ui.tune.error.step");
            return false;
        }
        int? max = null;
        if (!string.IsNullOrWhiteSpace(_maxText))
        {
            if (!int.TryParse(_maxText, NumberStyles.Integer, c, out int m) || m <= 0)
            {
                error = L.T("ui.tune.error.max");
                return false;
            }
            max = m;
        }
        if (!int.TryParse(_secondsText, NumberStyles.Integer, c, out int secs) || secs < 3 || secs > 120)
        {
            error = L.T("ui.tune.error.seconds");
            return false;
        }
        if (!uint.TryParse(_maxTempText, NumberStyles.Integer, c, out uint temp) || temp < 60 || temp > StabilityMonitor.WarnTempC)
        {
            error = L.T("ui.tune.error.temp", ("max", StabilityMonitor.WarnTempC));
            return false;
        }
        options = new TuneOptions
        {
            Target = _targetCore ? TuneTarget.Core : TuneTarget.Memory,
            StepMHz = step,
            MaxOffsetMHz = max,
            SampleDuration = TimeSpan.FromSeconds(secs),
            MaxTempC = temp,
            RunVramTest = _runVramTest,
        };
        error = "";
        return true;
    }

    void RaiseAll()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        KeepCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc/>
    public override void OnLanguageChanged()
    {
        if (_result != null) ShowResult(_result);
    }
}
