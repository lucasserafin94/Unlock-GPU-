using System.Diagnostics;
using System.Globalization;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Oem;

/// <summary>
/// Read-only <see cref="IOemPowerService"/> for Uniwill laptops. Every EC access is a read (op 0x0100), calls are
/// serialised and spaced by at least <see cref="MinCallSpacing"/>, and EC errors (0xFEFEFEFE) are retried once
/// before the register is reported as failed. Writing is disabled in this build.
/// </summary>
public sealed class UniwillOemPowerService : IOemPowerService, IDisposable
{
    /// <summary>Minimum delay between two GetSetULong calls (EC is slow; firmware tools use similar pacing).</summary>
    public static readonly TimeSpan MinCallSpacing = TimeSpan.FromMilliseconds(6);

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
    /// <exception cref="NotSupportedException">Always: writes are disabled in this build.</exception>
    public OemApplyResult Apply(OemPowerRequest request) => throw new NotSupportedException("oem.writeDisabled");

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsChannel) _channel.Dispose();
    }
}
