using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnlockGpu.Core.Infrastructure;

/// <summary>Pastas do app. Tudo por usuario; nada em Program Files e gravado em tempo de execucao.</summary>
public sealed class AppPaths
{
    public string DataDir { get; }
    public string SessionsDir { get; }
    public string ProfilesDir { get; }
    public string LogFile => Path.Combine(DataDir, "log.txt");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string StateFile => Path.Combine(DataDir, "state.json");

    public AppPaths(string? root = null)
    {
        DataDir = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockGPU");
        SessionsDir = root != null ? Path.Combine(root, "sessions")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UnlockGPU", "sessions");
        ProfilesDir = Path.Combine(DataDir, "profiles");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ProfilesDir);
    }
}

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Log em arquivo, com rotacao simples (2 MB) e protecao contra concorrencia.</summary>
public sealed class FileLog(AppPaths paths) : ILog
{
    readonly Lock _gate = new();
    const long MaxBytes = 2 * 1024 * 1024;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERRO", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    void Write(string level, string message)
    {
        lock (_gate)
        {
            try
            {
                var f = new FileInfo(paths.LogFile);
                if (f.Exists && f.Length > MaxBytes) File.Move(f.FullName, f.FullName + ".1", overwrite: true);
                File.AppendAllText(paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level}  {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>JSON com gravacao atomica (arquivo temporario + troca), para nunca deixar arquivo pela metade.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Textos traduzidos. Arquivos i18n/&lt;codigo&gt;.json embutidos no Core; ingles e o fallback.</summary>
public sealed class Localizer
{
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages =
    [
        ("pt", "Português"), ("en", "English"), ("es", "Español"), ("it", "Italiano"),
        ("fr", "Français"), ("de", "Deutsch"), ("ru", "Русский"), ("ja", "日本語"),
    ];

    readonly Dictionary<string, string> _fallback;
    Dictionary<string, string> _current;
    public string Code { get; private set; } = "en";
    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");
    public event EventHandler? Changed;

    public Localizer(string? code = null)
    {
        _fallback = LoadTable("en");
        _current = _fallback;
        SetLanguage(code ?? DefaultCode());
    }

    public static string DefaultCode()
    {
        string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == sys) ? sys : "en";
    }

    public void SetLanguage(string code)
    {
        if (!Languages.Any(l => l.Code == code)) code = "en";
        Code = code;
        Culture = CultureInfo.GetCultureInfo(code switch { "pt" => "pt-BR", "en" => "en-US", _ => code });
        _current = code == "en" ? _fallback : LoadTable(code);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string this[string key] => T(key);

    public string T(string key) =>
        _current.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Texto com parametros nomeados: T("x", ("watts", 180)) troca {watts}.</summary>
    public string T(string key, params (string Name, object? Value)[] args)
    {
        string s = T(key);
        foreach (var (name, value) in args)
            s = s.Replace("{" + name + "}", value is IFormattable fmt ? fmt.ToString(null, Culture) : value?.ToString() ?? "", StringComparison.Ordinal);
        return s;
    }

    public IEnumerable<string> Keys => _fallback.Keys;

    static Dictionary<string, string> LoadTable(string code)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream($"i18n.{code}.json");
        if (s == null) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
