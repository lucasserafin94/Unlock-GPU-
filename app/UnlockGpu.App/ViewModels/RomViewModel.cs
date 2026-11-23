using System.Collections.ObjectModel;
using System.Globalization;
using UnlockGpu.App.Mvvm;
using UnlockGpu.App.Services;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;
using UnlockGpu.Patch;

namespace UnlockGpu.App.ViewModels;

/// <summary>One romOverride slot row (name, size, hex blob).</summary>
public sealed class RomSlotRow : ObservableObject
{
    /// <summary>Creates the row from a service slot.</summary>
    public RomSlotRow(RomSlotInfo info)
    {
        Name = "romOverride" + info.Index.ToString("X2", CultureInfo.InvariantCulture);
        SizeText = info.SizeBytes.ToString(CultureInfo.InvariantCulture) + " B";
        Hex = info.Hex;
    }

    /// <summary>Slot name (romOverride00…).</summary>
    public string Name { get; }
    /// <summary>Blob size for display.</summary>
    public string SizeText { get; }
    /// <summary>Blob in hex, space-separated.</summary>
    public string Hex { get; }
}

/// <summary>
/// vBIOS offset kit tab: lists the romOverride slots currently stored in the GPU driver's class key and applies a
/// capture (stage + hot restart) or reverts to the factory ceiling. Once the driver reports the new max limit, the
/// power slider grows on its own; nothing else changes. Reads work unelevated; writes need elevation.
/// </summary>
public sealed class RomViewModel : TabViewModel
{
    readonly IRomOverrideService _rom;
    readonly IDialogService _dialogs;

    string _pnpId = "";
    string _statusText = "";
    bool _isWarning;
    string _captureFile = "";
    string _expectedMaxText = "";
    bool _noSlots;

    /// <summary>Creates the tab.</summary>
    public RomViewModel(Localizer localizer, IRomOverrideService rom, IDialogService dialogs) : base(localizer)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(dialogs);
        _rom = rom;
        _dialogs = dialogs;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, null, ex =>
        {
            StatusText = ResultText.Exception(L, ex);
            IsWarning = true;
        });
        BrowseCommand = new RelayCommand(Browse);
        StageCommand = new AsyncRelayCommand(StageAsync, CanStageNow, OnActionError);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => PnpId.Length > 0 && Slots.Count > 0, OnActionError);

        // The capture ships next to the app (config/romoverride-capture.txt) when present; otherwise the user picks one.
        string bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "config", "romoverride-capture.txt");
        if (System.IO.File.Exists(bundled)) _captureFile = bundled;
        StatusText = L.T("rom.status.working");
    }

    /// <summary>GPU device ID shown under the title (empty when not found).</summary>
    public string PnpId { get => _pnpId; private set => SetProperty(ref _pnpId, value); }
    /// <summary>Status / result text.</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    /// <summary>Status is a warning (no GPU, key not readable, action failed).</summary>
    public bool IsWarning { get => _isWarning; private set => SetProperty(ref _isWarning, value); }
    /// <summary>Capture file path (romOverride lines).</summary>
    public string CaptureFile { get => _captureFile; set => SetProperty(ref _captureFile, value); }
    /// <summary>Expected max power after restart in W (optional; empty = just verify the ceiling moved).</summary>
    public string ExpectedMaxText { get => _expectedMaxText; set => SetProperty(ref _expectedMaxText, value); }
    /// <summary>romOverride slots currently stored in the driver key.</summary>
    public ObservableCollection<RomSlotRow> Slots { get; } = [];
    /// <summary>No slots written (factory ceiling) — shows the note instead of the list.</summary>
    public bool NoSlots { get => _noSlots; private set => SetProperty(ref _noSlots, value); }
    /// <summary>Finds the GPU and lists its driver-key slots again.</summary>
    public AsyncRelayCommand RefreshCommand { get; }
    /// <summary>Picks a capture file in an open dialog.</summary>
    public RelayCommand BrowseCommand { get; }
    /// <summary>Writes the capture's slots into the driver key and hot-restarts the GPU.</summary>
    public AsyncRelayCommand StageCommand { get; }
    /// <summary>Deletes all slots and hot-restarts the GPU back to the factory ceiling.</summary>
    public AsyncRelayCommand ClearCommand { get; }

    /// <summary>Finds the GPU and lists the slots in its driver key (background).</summary>
    public async Task RefreshAsync()
    {
        StatusText = L.T("rom.status.working");
        IsWarning = false;
        string? pnpId = await Task.Run(() => _rom.FindGpuPnpId()).ConfigureAwait(true);
        PnpId = pnpId ?? "";
        if (pnpId == null)
        {
            Slots.Clear();
            NoSlots = false;
            StatusText = L.T(ServiceTextKeys.RomNoGpu);
            IsWarning = true;
            return;
        }
        string error = await LoadSlotsAsync(pnpId).ConfigureAwait(true);
        if (error.Length > 0)
        {
            StatusText = ResultText.Error(L, ApplyError.Compose(ServiceTextKeys.RomKeyOpen, error));
            IsWarning = true;
        }
        else
        {
            StatusText = L.T("rom.status.ready");
            IsWarning = false;
        }
    }

    /// <summary>Writes the capture's slots into the driver key and hot-restarts the GPU.</summary>
    async Task StageAsync()
    {
        string pnpId = PnpId;
        if (pnpId.Length == 0) return;
        string file = CaptureFile.Trim();
        if (!System.IO.File.Exists(file))
        {
            StatusText = ResultText.Error(L, ApplyError.Compose(ServiceTextKeys.RomCaptureInvalid, file));
            IsWarning = true;
            return;
        }
        double? expected = ParseExpectedMax(out string? problem);
        if (problem != null)
        {
            StatusText = problem;
            IsWarning = true;
            return;
        }

        if (!_dialogs.Confirm(L.T("rom.stageTitle"), L.T("rom.restartHint"))) return;
        StatusText = L.T("rom.status.working");
        IsWarning = false;

        ApplyResult staged = await Task.Run(() => _rom.StageCaptured(pnpId, file)).ConfigureAwait(true);
        if (!staged.Ok)
        {
            StatusText = ResultText.Describe(L, staged);
            IsWarning = true;
            return;
        }
        ApplyResult restart = await _rom.HotRestartAsync(pnpId, expected).ConfigureAwait(true);
        StatusText = ResultText.Describe(L, restart);
        IsWarning = !restart.Ok;
        await LoadSlotsAsync(pnpId).ConfigureAwait(true);
    }

    /// <summary>Deletes all slots and hot-restarts the GPU back to the factory ceiling.</summary>
    async Task ClearAsync()
    {
        string pnpId = PnpId;
        if (pnpId.Length == 0) return;
        if (!_dialogs.Confirm(L.T("rom.clearTitle"), L.T("rom.restartHint"))) return;
        StatusText = L.T("rom.status.working");
        IsWarning = false;

        ApplyResult cleared = await Task.Run(() => _rom.ClearAll(pnpId)).ConfigureAwait(true);
        if (!cleared.Ok)
        {
            StatusText = ResultText.Describe(L, cleared);
            IsWarning = true;
            return;
        }
        ApplyResult restart = await _rom.HotRestartAsync(pnpId, null).ConfigureAwait(true);
        StatusText = ResultText.Describe(L, restart);
        IsWarning = !restart.Ok;
        await LoadSlotsAsync(pnpId).ConfigureAwait(true);
    }

    void Browse()
    {
        string? file = _dialogs.OpenFile(L.T("rom.captureFile"), "Text files (*.txt)|*.txt|All files (*.*)|*.*");
        if (file != null) CaptureFile = file;
    }

    /// <summary>Re-reads the slot list without touching the status text.</summary>
    async Task<string> LoadSlotsAsync(string pnpId)
    {
        var (slots, error) = await Task.Run(() =>
        {
            IReadOnlyList<RomSlotInfo> list = _rom.ReadSlots(pnpId, out string err);
            return ((IReadOnlyList<RomSlotInfo>)list, err);
        }).ConfigureAwait(true);
        Slots.Clear();
        foreach (RomSlotInfo s in slots) Slots.Add(new RomSlotRow(s));
        NoSlots = slots.Count == 0;
        return error;
    }

    /// <summary>Parses the optional expected max W; null when the field is empty.</summary>
    double? ParseExpectedMax(out string? problem)
    {
        problem = null;
        string text = ExpectedMaxText.Trim();
        if (text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, L.Culture, out double w) || w <= 0)
            problem = L.T("rom.expectedMaxInvalid");
        return problem == null ? w : null;
    }

    bool CanStageNow() => PnpId.Length > 0 && !string.IsNullOrWhiteSpace(CaptureFile);

    void OnActionError(Exception ex)
    {
        StatusText = ResultText.Exception(L, ex);
        IsWarning = true;
    }
}
