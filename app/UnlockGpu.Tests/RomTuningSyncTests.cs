using UnlockGpu.App.Services;
using UnlockGpu.App.ViewModels;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Patch;

namespace UnlockGpu.Tests;

/// <summary>
/// The Rom tab and the Tuning persistent card share the driver key: when one writes, the other must re-read.
/// These tests wire the two tabs exactly like MainViewModel does and assert both directions of the sync, plus
/// the restart verification fallback for boards without a MAX slot.
/// </summary>
public sealed class RomTuningSyncTests
{
    sealed class FakeDialogs : IDialogService
    {
        public bool ConfirmResult = true;
        public string? SaveFile(string title, string filter, string defaultFileName) => null;
        public string? OpenFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => ConfirmResult;
        public void OpenFolder(string folder) { }
        public void RevealFile(string file) { }
    }

    static TuningViewModel CreateTuning(FakeRomOverrideService rom, FakeGpu gpu) =>
        new(new Localizer("en"), gpu, new FakeOffsetService(), new FakeVramLockService(),
            new FakePowerService(), rom, new FakeSettingsStore(), new BannerViewModel());

    static void Init(TuningViewModel vm) => vm.InitializeAsync().GetAwaiter().GetResult();

    [Test]
    public void RomTab_ClearsSlots_TuningCardFollows()
    {
        var rom = new FakeRomOverrideService { Slots = FakeRomLayout.KnownBoard() };
        var tuning = CreateTuning(rom, new FakeGpu());
        Init(tuning);
        Check.True(tuning.HasRomRoles);

        var romTab = new RomViewModel(new Localizer("en"), rom, new FakeDialogs());
        romTab.RefreshCommand.ExecuteAsync().GetAwaiter().GetResult();
        Check.True(romTab.Slots.Count > 0);

        // Same wiring as MainViewModel: the key changed in Rom → Tuning re-reads.
        romTab.SlotsChanged += (_, _) => tuning.ReloadRomSlotsAsync().GetAwaiter().GetResult();

        romTab.ClearCommand.ExecuteAsync().GetAwaiter().GetResult();

        Check.True(romTab.NoSlots);          // the Rom tab now shows "no slots"
        Check.False(tuning.HasRomRoles);     // Tuning re-resolved: no slots left in the key
        Check.False(tuning.IsRomDirty);      // and nothing is "dirty" against an empty key
    }

    [Test]
    public void TuningTab_AppliesPersistent_RomTabRefreshes()
    {
        var rom = new FakeRomOverrideService { Slots = FakeRomLayout.KnownBoard() };
        var tuning = CreateTuning(rom, new FakeGpu());
        Init(tuning);

        var romTab = new RomViewModel(new Localizer("en"), rom, new FakeDialogs());
        romTab.RefreshCommand.ExecuteAsync().GetAwaiter().GetResult();
        int readsBefore = rom.ReadSlotCalls;

        // Same wiring as MainViewModel: the key changed in Tuning → Rom re-reads.
        tuning.PersistentApplied += (_, _) => romTab.RefreshCommand.ExecuteAsync().GetAwaiter().GetResult();

        tuning.MaxValue = 300; // dirty against the stored 250 W (Base follows via sync)
        Check.True(tuning.IsRomDirty);
        tuning.ApplyPersistentCommand.ExecuteAsync().GetAwaiter().GetResult();

        Check.False(tuning.IsRomDirty);              // reloaded from the key: values now match
        Check.True(rom.ReadSlotCalls > readsBefore); // the Rom tab re-read after Tuning wrote
    }

    [Test]
    public void ApplyPersistent_NoMaxSlot_VerifiesAgainstNvmlCeiling()
    {
        // A board with only a width-8 board-input slot: no MAX role resolves, so the write does not move the ceiling.
        var rom = new FakeRomOverrideService { Slots = [FakeRomLayout.Slot(0, 0x580FA, 8, 71_000)] };
        var gpu = new FakeGpu(); // Info.MaxLimitW = 175 W
        var tuning = CreateTuning(rom, gpu);
        Init(tuning);

        Check.True(tuning.HasRomRoles);
        tuning.BoardInputA = 80; // dirty against the stored 71 A
        Check.True(tuning.IsRomDirty);
        tuning.ApplyPersistentCommand.ExecuteAsync().GetAwaiter().GetResult();

        double? expected = rom.LastHotRestartExpected;
        Check.True(expected is not null);
        Check.Near(175.0, expected!.Value); // verified against NVML's ceiling, not a stale slider value
    }
}
