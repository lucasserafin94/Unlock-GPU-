using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Applies whole profiles in a fixed order: offsets, VRAM lock, power limit. Items that are null in the profile are
/// left untouched; one <see cref="ApplyResult"/> per item that was applied.
/// </summary>
public sealed class ProfileApplier : IProfileApplier
{
    readonly IGpu _gpu;
    readonly IOffsetService _offsets;
    readonly IVramLockService _vram;
    readonly IPowerService _power;
    readonly ILog _log;

    /// <summary>Creates the applier over the individual services.</summary>
    public ProfileApplier(IGpu gpu, IOffsetService offsets, IVramLockService vram, IPowerService power, ILog log)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(vram);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(log);
        _gpu = gpu;
        _offsets = offsets;
        _vram = vram;
        _power = power;
        _log = log;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ApplyResult> Apply(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var results = new List<ApplyResult>();
        if (profile.CoreOffsetMHz is not null || profile.MemOffsetMHz is not null)
        {
            int core = profile.CoreOffsetMHz ?? _offsets.Get(ClockDomain.Core)?.CurrentMHz ?? 0;
            int mem = profile.MemOffsetMHz ?? _offsets.Get(ClockDomain.Memory)?.CurrentMHz ?? 0;
            results.Add(_offsets.Apply(core, mem));
        }
        if (profile.VramLockMHz is uint level)
            results.Add(level == 0 ? _vram.Unlock() : _vram.Lock(level));
        if (profile.PowerLimitW is double watts)
            results.Add(_power.Apply(watts));

        int failed = results.Count(r => !r.Ok);
        _log.Info($"PROFILE '{profile.Name}' applied: {results.Count - failed} ok, {failed} failed");
        return results;
    }

    /// <inheritdoc/>
    public Profile Capture(string name)
    {
        var core = _offsets.Get(ClockDomain.Core);
        var mem = _offsets.Get(ClockDomain.Memory);
        var sample = _gpu.Sample();
        // Sem trava ativa o perfil nao guarda nada para a VRAM (null = nao mexer): aplicar o perfil depois nao
        // deve resetar o clock de memoria nem desfazer uma trava feita por outra ferramenta.
        uint? lockMHz = null;
        if (sample.Ok)
        {
            var st = _vram.Evaluate(sample);
            if (st.Locked) lockMHz = st.LevelMHz;
        }
        double? limit = null;
        if (_power.Support == PowerSupport.Adjustable)
        {
            // O limite configurado, nao o aplicado: o aplicado pode estar temporariamente mais baixo (termico,
            // bateria) e salvar esse valor baixaria o limite ao reaplicar o perfil.
            double w = _gpu.ConfiguredLimitW();
            if (!double.IsNaN(w)) limit = Math.Round(w, 1);
        }
        return new Profile
        {
            Name = name,
            CoreOffsetMHz = core?.CurrentMHz,
            MemOffsetMHz = mem?.CurrentMHz,
            VramLockMHz = lockMHz,
            PowerLimitW = limit,
            Saved = DateTime.Now,
        };
    }

    /// <inheritdoc/>
    public IReadOnlyList<ApplyResult> ResetToDefaults()
    {
        var results = new List<ApplyResult> { _offsets.Apply(0, 0), _vram.Unlock() };
        _log.Info($"RESET to defaults: {results.Count(r => r.Ok)}/{results.Count} ok");
        return results;
    }
}
