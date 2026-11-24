using UnlockGpu.App.ViewModels;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Services;
using UnlockGpu.Patch;

namespace UnlockGpu.Tests;

/// <summary>
/// Persistent power card (driver shadow slots): factory delta in role resolution, MAX→Base sync, the PCF
/// total/overhead invariant and the typed value boxes (exact commit without step snapping).
/// </summary>
public sealed class TuningPcfTests
{
    // ---------------- fixtures (shared fakes live in Fakes/) ----------------

    static TuningViewModel CreateVm(string lang = "en", IReadOnlyList<RomSlotInfo>? slots = null)
    {
        var rom = new FakeRomOverrideService { Slots = slots ?? FakeRomLayout.KnownBoard() };
        return new TuningViewModel(new Localizer(lang), new FakeGpu(), new FakeOffsetService(), new FakeVramLockService(),
            new FakePowerService(), rom, new FakeSettingsStore(), new BannerViewModel());
    }

    static void Init(TuningViewModel vm) => vm.InitializeAsync().GetAwaiter().GetResult();

    // ---------------- role resolution: factory delta ----------------

    [Test]
    public void ResolveRoles_KnownBoard_ReportsFactoryDelta()
    {
        var roles = RomOverrideService.ResolveRoles(FakeRomLayout.KnownBoard());
        Check.True(roles.Any);
        Check.Equal(RomRoleOffsets.FactoryDeltaW, roles.FactoryDeltaW); // 25 W on the captured board
    }

    [Test]
    public void ResolveRoles_DenseFallback_NoFactoryDelta()
    {
        var slots = FakeRomLayout.DenseFallback();
        var roles = RomOverrideService.ResolveRoles(slots);
        Check.True(roles.Any);
        Check.Equal<int?>(null, roles.FactoryDeltaW); // the delta is only known for the captured board
    }

    // ---------------- MAX → Base sync ----------------

    [Test]
    public void Init_SyncOn_BaseTracksMaxMinusFactoryDelta()
    {
        var vm = CreateVm();
        Init(vm);

        Check.True(vm.HasRomRoles);
        Check.Near(250, vm.MaxValue);
        Check.Near(225, vm.BaseValue); // 250 W − factory delta of 25 W
        Check.Equal(25.0, vm.MaxValue - vm.BaseValue);
    }

    [Test]
    public void MaxDrag_SyncOn_BaseFollows()
    {
        var vm = CreateVm();
        Init(vm);

        vm.MaxValue = 300; // slider snaps to the 5 W grid: 300 stays
        Check.Near(275, vm.BaseValue); // base trails MAX by the factory delta
    }

    // ---------------- PCF total / overhead invariant ----------------

    [Test]
    public void Pcf_OverheadDrivesTotal()
    {
        var vm = CreateVm();
        Init(vm);

        vm.OverheadW = 100;
        Check.Near(350, vm.PcfTotalW); // ceiling 250 W + overhead 100 W
    }

    [Test]
    public void Pcf_TotalDrivesOverhead()
    {
        var vm = CreateVm();
        Init(vm);

        vm.PcfTotalW = 290; // ceiling 250 W + 40 W of overhead
        Check.Near(40, vm.OverheadW);
        Check.Near(290, vm.PcfTotalW);
    }

    [Test]
    public void Pcf_TotalCapsAtSliderMax()
    {
        var vm = CreateVm();
        Init(vm);

        vm.MaxValue = 400; // top of the MAX slider (RomPowerMax)
        vm.OverheadW = 200; // 400 + 200 would be 600 W, above the 500 W total cap

        Check.Near(100, vm.OverheadW); // overhead shrinks to keep the total inside the range
        Check.Near(500, vm.PcfTotalW);
    }

    // ---------------- typed value boxes: exact commit, no snapping ----------------

    [Test]
    public void MaxInput_CommitsExactValueWithoutSnapping()
    {
        var vm = CreateVm();
        Init(vm);

        vm.MaxInput = "317"; // not on the 5 W grid: typed values commit exactly
        Check.Near(317, vm.MaxValue);
        Check.Equal("317", vm.MaxInput);
    }

    [Test]
    public void MaxInput_OutOfRangeOrInvalidRevertsBox()
    {
        var vm = CreateVm();
        Init(vm);
        vm.MaxValue = 300;

        vm.MaxInput = "999"; // above the 400 W slider max: not committed, box reverts to the model value
        Check.Near(300, vm.MaxValue);
        Check.Equal("300", vm.MaxInput);

        vm.MaxInput = "abc"; // invalid text also reverts
        Check.Equal("300", vm.MaxInput);
    }

    [Test]
    public void Input_PtCultureUsesDecimalComma()
    {
        var vm = CreateVm(lang: "pt");
        Init(vm);

        vm.NvvddInput = "14,001"; // pt-BR decimal comma
        Check.Near(14.001, vm.NvvddA);
        Check.Equal("14,001", vm.NvvddInput);
    }

    [Test]
    public void Init_ExactLoad_NotDirty()
    {
        var vm = CreateVm();
        Init(vm);

        Check.Near(15.001, vm.NvvddA); // raw 15001 mA loads without snapping to the 0.5 A grid
        Check.False(vm.IsRomDirty);    // every control matches its stored raw value
    }

    [Test]
    public void NvvddInput_WideRangeAccepted()
    {
        var vm = CreateVm();
        Init(vm);

        vm.NvvddInput = "215"; // rail range widened to 400 A: a value the old slider could not reach
        Check.Near(215, vm.NvvddA);
        Check.True(vm.IsRomDirty); // stored raw (15001) differs from the typed value
    }

    [Test]
    public void ValidateAll_MixedState_ReportsPerControl()
    {
        var vm = CreateVm();
        Init(vm);

        vm.MaxValue = 300; // MAX and BASE now differ from what is stored in the driver key
        vm.ValidateAllCommand.ExecuteAsync().GetAwaiter().GetResult();

        Check.Contains("✓", vm.LiveLine); // rails still match their stored values
        Check.Contains("✗", vm.LiveLine); // MAX/Base do not
    }
}
