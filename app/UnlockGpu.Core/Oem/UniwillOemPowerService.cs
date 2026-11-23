using System.Diagnostics;
using System.Globalization;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Oem;

/// <summary>
/// <see cref="IOemPowerService"/> for Uniwill laptops. Every EC access is serialised and spaced by at least
/// <see cref="MinCallSpacing"/>, and EC errors (0xFEFEFEFE) are retried once before the register is reported as
/// failed. Reads use op 0x0100; <see cref="Apply"/> writes with op 0x0000 only the registers that actually change.
/// </summary>
public sealed class UniwillOemPowerService : IOemPowerService, IDisposable
{
    /// <summary>Minimum delay between two GetSetULong calls (EC is slow; firmware tools use similar pacing).</summary>
    public static readonly TimeSpan MinCallSpacing = TimeSpan.FromMilliseconds(6);

    /// <summary>Highest cTGP offset the UI/service accepts, in W above base TGP.</summary>
    public const int CtgpOffsetMaxW = 160;
    /// <summary>Highest Dynamic Boost offset the firmware accepts (register MXDB), in W.</summary>
    public const int DbOffsetMaxW = 25;

    const int Attempts = 2;

    readonly ISystemIdentitySource _identity;
    readonly IUniwillEcChannel _channel;
    readonly ILog? _log;
    readonly bool _ownsChannel;
    readonly Lock _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    TimeSpan _lastCall = TimeSpan.MinValue;
    bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="identity">SMBIOS identity reader.</param>
    /// <param name="channel">EC channel.</param>
    /// <param name="log">Optional log.</param>
    /// <param name="ownsChannel">When true, <see cref="Dispose"/> disposes <paramref name="channel"/>.</param>
    public UniwillOemPowerService(ISystemIdentitySource identity, IUniwillEcChannel channel, ILog? log = null, bool ownsChannel = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(channel);
        _identity = identity;
        _channel = channel;
        _log = log;
        _ownsChannel = ownsChannel;
    }

    /// <summary>Service wired to the real WMI identity source and EC channel (owned).</summary>
    public static UniwillOemPowerService CreateDefault(ILog? log = null) =>
        new(new WmiSystemIdentitySource(), new WmiUniwillEcChannel(), log, ownsChannel: true);

    /// <inheritdoc />
    public OemDetection Detect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = _identity.Read();
        bool verified = UniwillBoards.IsVerified(id);
        if (!UniwillBoards.LooksLikeUniwill(id) && !verified)
            return new OemDetection(OemStatus.NotUniwill, id, false, "oem.status.notUniwill");

        OemStatus status;
        lock (_gate) status = _channel.Probe();
        _log?.Info($"OEM detect: {id.SystemVendor}/{id.BoardVendor} {id.BoardProduct} -> {status} (verified={verified})");
        string key = status switch
        {
            OemStatus.Available => verified ? "oem.status.available" : "oem.status.unverifiedBoard",
            OemStatus.WmiClassMissing => "oem.status.classMissing",
            OemStatus.AccessDenied => "oem.status.accessDenied",
            OemStatus.NotUniwill => "oem.status.notUniwill",
            _ => "oem.status.error",
        };
        return new OemDetection(status, id, verified, key);
    }

    /// <inheritdoc />
    public OemPowerState ReadState(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failed = new List<ushort>();
        byte? R(ushort reg) => ReadRegister(reg, failed, ct);

        byte? mode = R(UniwillEc.Reg.PerformanceMode);
        byte? ctrl = R(UniwillEc.Reg.GpuControl);
        byte? ctgp = R(UniwillEc.Reg.CtgpOffset);
        byte? tpp = R(UniwillEc.Reg.TppOffset);
        byte? db = R(UniwillEc.Reg.DynamicBoostOffset);
        byte? flags = R(UniwillEc.Reg.ProfileFlags);
        byte? pl1 = R(UniwillEc.Reg.CpuPl1);
        byte? pl2 = R(UniwillEc.Reg.CpuPl2);
        byte? pl4 = R(UniwillEc.Reg.CpuPl4);

        bool? pl4Doubled = Bit(flags, 7);
        var state = new OemPowerState
        {
            TimeUtc = DateTime.UtcNow,
            PerformanceModeRaw = mode,
            PerformanceMode = mode is byte m ? UniwillEc.DecodePerformanceMode(m) : OemPerformanceMode.Unknown,
            ControlRaw = ctrl,
            GeneralEnabled = Bit(ctrl, 0),
            DynamicBoostEnabled = Bit(ctrl, 1),
            CtgpEnabled = Bit(ctrl, 2),
            CtgpOffsetW = ctgp,
            TppOffsetW = tpp,
            DynamicBoostOffsetW = db,
            ProfileFlagsRaw = flags,
            CustomProfileMode = Bit(flags, 6),
            Pl4Doubled = pl4Doubled,
            CpuPl1W = pl1,
            CpuPl2W = pl2,
            CpuPl4W = pl4 is byte p4 ? (pl4Doubled == true ? p4 * 2 : p4) : null,
            FailedRegisters = failed,
        };
        if (failed.Count > 0)
            _log?.Warn("OEM EC read failed for " + string.Join(", ", failed.Select(r => "0x" + r.ToString("X4", CultureInfo.InvariantCulture))));
        return state;
    }

    /// <inheritdoc />
    public OemApplyResult Apply(OemPowerRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = ReadState();
        if (state.FailedRegisters.Count > 0)
            return new OemApplyResult(false, state, "oem.applyReadFailed");

        var writes = new List<(ushort Register, byte Value)>();

        // Performance mode: canonical values with the bits outside the mode mask preserved.
        if (request.PerformanceMode is OemPerformanceMode mode && mode != OemPerformanceMode.Unknown)
        {
            byte current = state.PerformanceModeRaw ?? 0;
            byte target = UniwillEc.EncodePerformanceMode(current, mode);
            if (target != current) writes.Add((UniwillEc.Reg.PerformanceMode, target));
        }

        // cTGP and Dynamic Boost: enable bits in 0x0743 plus the offset registers.
        bool touchControl = request.CtgpEnabled.HasValue || request.CtgpOffsetW.HasValue ||
                            request.DynamicBoostEnabled.HasValue || request.DynamicBoostOffsetW.HasValue;
        if (touchControl)
        {
            if (!state.ControlRaw.HasValue)
                return new OemApplyResult(false, state, "oem.applyReadFailed");

            byte control = state.ControlRaw.Value;
            // An explicit offset implies the feature is wanted on; an explicit enable always wins.
            bool ctgpOn = request.CtgpEnabled ?? (request.CtgpOffsetW.HasValue ? true : Bit(control, 2) == true);
            bool dbOn = request.DynamicBoostEnabled ?? (request.DynamicBoostOffsetW.HasValue ? true : Bit(control, 1) == true);

            int? ctgpOffset = request.CtgpOffsetW;
            if (ctgpOffset is < 0 or > CtgpOffsetMaxW)
                return new OemApplyResult(false, state, "oem.applyRange");
            int? dbOffset = request.DynamicBoostOffsetW;
            if (dbOffset is < 0 or > DbOffsetMaxW)
                return new OemApplyResult(false, state, "oem.applyRange");

            control = SetBit(control, 2, ctgpOn);
            control = SetBit(control, 1, dbOn);
            if ((ctgpOn || dbOn) && Bit(control, 0) != true)
                control |= 0x01; // general gate: the offsets do nothing without it
            if (control != state.ControlRaw.Value)
                writes.Add((UniwillEc.Reg.GpuControl, control));

            if (ctgpOffset.HasValue && state.CtgpOffsetW != ctgpOffset.Value)
                writes.Add((UniwillEc.Reg.CtgpOffset, (byte)ctgpOffset.Value));
            if (dbOffset.HasValue && state.DynamicBoostOffsetW != dbOffset.Value)
                writes.Add((UniwillEc.Reg.DynamicBoostOffset, (byte)dbOffset.Value));
        }

        foreach (var (register, value) in writes)
            if (!WriteRegister(register, value))
                return new OemApplyResult(false, ReadState(), "oem.ecWriteFailed");

        // Verify with a fresh full read: every written register must come back identical.
        var readBack = ReadState();
        bool verified = writes.All(w => w.Register switch
        {
            UniwillEc.Reg.PerformanceMode => readBack.PerformanceModeRaw == w.Value,
            UniwillEc.Reg.GpuControl => readBack.ControlRaw == w.Value,
            UniwillEc.Reg.CtgpOffset => readBack.CtgpOffsetW == w.Value,
            UniwillEc.Reg.DynamicBoostOffset => readBack.DynamicBoostOffsetW == w.Value,
            _ => false,
        });
        if (!verified)
            _log?.Warn("OEM apply: verification mismatch after write.");
        else
            _log?.Info("OEM apply: wrote " + string.Join(", ", writes.Select(w => $"0x{w.Register:X4}={w.Value:X2}")) + ".");
        return new OemApplyResult(verified, readBack, verified ? null : "oem.applyVerifyFailed");
    }

    /// <summary>Writes one register with pacing and a single retry on EC error.</summary>
    bool WriteRegister(ushort register, byte value)
    {
        ulong data = UniwillEc.EncodeWrite(register, value);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            uint result;
            lock (_gate)
            {
                Pace(default);
                try { result = _channel.Invoke(data); }
                finally { _lastCall = _clock.Elapsed; }
            }
            if (result != UniwillEc.EcError) return true;
        }
        _log?.Warn("OEM EC write failed for 0x" + register.ToString("X4", CultureInfo.InvariantCulture));
        return false;
    }

    byte? ReadRegister(ushort register, List<ushort> failed, CancellationToken ct)
    {
        ulong data = UniwillEc.EncodeRead(register);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            uint result;
            lock (_gate)
            {
                Pace(ct);
                try { result = _channel.Invoke(data); }
                finally { _lastCall = _clock.Elapsed; }
            }
            if (UniwillEc.TryDecodeReadResult(result, out byte value)) return value;
        }
        failed.Add(register);
        return null;
    }

    /// <summary>Waits until <see cref="MinCallSpacing"/> has passed since the previous call. Caller holds the gate.</summary>
    void Pace(CancellationToken ct)
    {
        if (_lastCall == TimeSpan.MinValue) return;
        var wait = _lastCall + MinCallSpacing - _clock.Elapsed;
        if (wait > TimeSpan.Zero && ct.WaitHandle.WaitOne(wait)) ct.ThrowIfCancellationRequested();
    }

    static bool? Bit(byte? value, int bit) => value is byte v ? (v & (1 << bit)) != 0 : null;

    static byte SetBit(byte value, int bit, bool on) => on ? (byte)(value | (1 << bit)) : (byte)(value & ~(1 << bit));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsChannel) _channel.Dispose();
    }
}
