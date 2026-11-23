using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Services;

// ============================================================================================
// Contratos entre o nucleo e a interface. Cada servico tem uma implementacao em Services/*.cs.
// Regras de seguranca que valem para todos:
//  * so APIs publicas (NVML / nvidia-smi); nada de registro do driver, vBIOS ou nvflash;
//  * todo valor aplicado e lido de volta do hardware (ApplyResult);
//  * faixas sempre vem do driver (nunca constantes fixas);
//  * textos para o usuario sao CHAVES de traducao (Localizer), nunca texto pronto.
// ============================================================================================

public sealed record AppSettings
{
    public string Language { get; init; } = "";            // "" = idioma do Windows
    public string Accent { get; init; } = "#BAF568";
    public int RefreshSeconds { get; init; } = 2;
    public int AdapterWatts { get; init; }                  // etiqueta do carregador; 0 = nao informado
    public bool MinimizeToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public string? StartupProfile { get; init; }            // perfil aplicado ao entrar no Windows
    public bool ReapplyAfterDriverReset { get; init; } = true;
    public int GraphMinutes { get; init; } = 3;
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event EventHandler<AppSettings>? Changed;
}

/// <summary>Um perfil completo de ajustes. Null = nao mexer naquele item.</summary>
public sealed record Profile
{
    public required string Name { get; init; }
    public int? CoreOffsetMHz { get; init; }
    public int? MemOffsetMHz { get; init; }
    public uint? VramLockMHz { get; init; }                  // 0 = destravar
    public double? PowerLimitW { get; init; }
    public DateTime Saved { get; init; } = DateTime.Now;
    public string? Notes { get; init; }
}

public interface IProfileStore
{
    IReadOnlyList<Profile> List();
    Profile? Get(string name);
    void Save(Profile profile);
    bool Delete(string name);
}

/// <summary>Aplica um perfil inteiro (offsets, trava, limite) e devolve um resultado por item.</summary>
public interface IProfileApplier
{
    IReadOnlyList<ApplyResult> Apply(Profile profile);
    /// <summary>Le do hardware o estado atual como perfil (para "salvar atual" e para backup).</summary>
    Profile Capture(string name);
    /// <summary>Volta tudo ao padrao do driver: offsets 0, VRAM destravada (limite nao e alterado).</summary>
    IReadOnlyList<ApplyResult> ResetToDefaults();
}

// ---------- monitor ----------

/// <summary>Leitura periodica em thread de fundo. Eventos chegam na thread de fundo: a UI faz o despacho.</summary>
public interface IGpuMonitor : IDisposable
{
    TimeSpan Interval { get; set; }
    IReadOnlyList<GpuSample> History { get; }   // ultimas ~30 min
    GpuSample? Latest { get; }
    event EventHandler<GpuSample>? SampleArrived;
    /// <summary>Disparado quando detecta reinicio do driver (handle reaberto, offsets/trava zerados).</summary>
    event EventHandler? DriverReset;
    void Start();
    void Stop();
}

// ---------- trava da VRAM ----------

public sealed record VramLockState(bool Locked, uint? LevelMHz, int? OffsetMHz, bool Lost);

public interface IVramLockService
{
    /// <summary>Niveis travaveis (os dois maiores clocks de memoria suportados).</summary>
    IReadOnlyList<uint> Levels { get; }
    ApplyResult Lock(uint levelMHz);
    ApplyResult Unlock();
    /// <summary>Estado atual combinando o que o app aplicou e o comportamento da GPU parada.</summary>
    VramLockState Evaluate(GpuSample sample);
}

// ---------- offsets e limite ----------

public interface IOffsetService
{
    OffsetInfo? Get(ClockDomain domain);
    /// <summary>Aplica e le de volta. Valores fora da faixa do driver sao recusados antes de chamar a NVML.</summary>
    ApplyResult Apply(int coreMHz, int memMHz);
}

public enum PowerSupport { Unknown, Adjustable, NotSupported }

public interface IPowerService
{
    PowerSupport Support { get; }
    /// <summary>Descobre se o limite e ajustavel (reaplica o valor em vigor uma vez por driver; resultado em cache).</summary>
    PowerSupport Probe();
    ApplyResult Apply(double watts);
}

// ---------- sessoes ----------

public sealed record SessionSummary(string CsvPath, TimeSpan Duration, int Samples, int LoadSamples,
    double AvgPowerW, double PeakPowerW, double AvgCoreMHz, uint MaxTempC,
    IReadOnlyDictionary<uint, double> MemClockShare, IReadOnlyDictionary<string, double> ReasonShare);

public interface ISessionRecorder
{
    bool IsRecording { get; }
    string? CurrentPath { get; }
    void Start(string? label = null);
    void Add(GpuSample sample);
    SessionSummary? Live();
    SessionSummary? Stop();
    IReadOnlyList<string> ListSessions();
}

// ---------- estabilidade ----------

public enum Health { Good, Warning, Critical }

public sealed record HealthReport(Health Level, IReadOnlyList<string> ReasonKeys);

public interface IStabilityMonitor
{
    HealthReport Current { get; }
    event EventHandler<HealthReport>? Changed;
    void Feed(GpuSample sample);
    /// <summary>Informa uma queda de pontuacao do benchmark (percentual) para o alerta.</summary>
    void ReportBenchmarkDrop(double percent);
    /// <summary>Eventos do Windows (Kernel-Power 41, WHEA) desde a ultima inicializacao do app.</summary>
    IReadOnlyList<string> RecentSystemEvents();
}

// ---------- inicializacao com o Windows ----------

public interface IStartupService
{
    bool IsEnabled { get; }
    /// <summary>Tarefa agendada ao entrar no Windows, elevada, que roda "UnlockGPU.exe --silent --apply-profile &lt;nome&gt;".</summary>
    void Enable(string profileName);
    void Disable();
}

// ---------- backup ----------

public interface IBackupService
{
    /// <summary>ZIP com settings, perfis e o estado atual lido do hardware (perfil "captura").</summary>
    string Export(string zipPath);
    /// <summary>Restaura settings e perfis. Nao aplica nada ao hardware sozinho.</summary>
    void Import(string zipPath);
}

// ---------- benchmark, teste de VRAM e ajuste automatico ----------

public sealed record BenchResult(double Score, TimeSpan Duration, bool Completed, string? ErrorKey);
public sealed record VramTestResult(long BytesTested, long Errors, bool Completed, string? ErrorKey);

public interface IGpuBenchmark
{
    /// <summary>Carga D3D11 (compute) por ~duration; Score = trabalho por segundo (maior e melhor).</summary>
    Task<BenchResult> RunAsync(TimeSpan duration, CancellationToken ct);
}

public interface IVramTester
{
    /// <summary>Preenche uma fracao da VRAM livre com padroes, le de volta e conta erros.</summary>
    Task<VramTestResult> RunAsync(double fractionOfFree, CancellationToken ct);
}

public enum TuneTarget { Core, Memory }

public sealed record TuneOptions
{
    public TuneTarget Target { get; init; } = TuneTarget.Memory;
    public int StepMHz { get; init; } = 150;
    public int? MaxOffsetMHz { get; init; }                  // null = maximo do driver
    public TimeSpan SampleDuration { get; init; } = TimeSpan.FromSeconds(10);
    public double MinGainPercent { get; init; } = 0.5;        // ganho minimo para continuar subindo
    public uint MaxTempC { get; init; } = 87;
    public bool RunVramTest { get; init; } = true;
}

public sealed record TuneStep(int OffsetMHz, double Score, double? GainPercent, long VramErrors, string? StopReasonKey);

public sealed record TuneResult(int BestOffsetMHz, double BaselineScore, double BestScore, IReadOnlyList<TuneStep> Steps, string StopReasonKey);

public interface IAutoTuner
{
    /// <summary>Sobe o offset em passos, mede, volta ao ultimo bom ao cair/errar/travar. Sempre restaura o offset inicial
    /// se for cancelado ou falhar; aplica o melhor so ao terminar bem.</summary>
    Task<TuneResult> RunAsync(TuneOptions options, IProgress<TuneStep>? progress, CancellationToken ct);
}
