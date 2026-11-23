using System.IO.Compression;
using System.Text.Json;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Backup ZIP: <c>manifest.json</c>, <c>settings.json</c>, <c>profiles/*.json</c> and <c>captured-current.json</c>
/// (the current hardware state as a profile). Import restores settings and profiles only; nothing is applied to the
/// hardware. Entries are read in memory (never extracted to disk) and size-limited.
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>Backup format written in the manifest.</summary>
    public const int FormatVersion = 1;
    const string ManifestEntry = "manifest.json";
    const string SettingsEntry = "settings.json";
    const string ProfilesPrefix = "profiles/";
    const string CapturedEntry = "captured-current.json";
    const long MaxEntryBytes = 1024 * 1024;
    const int MaxProfiles = 500;

    readonly AppPaths _paths;
    readonly ISettingsStore _settings;
    readonly IProfileStore _profiles;
    readonly IProfileApplier _applier;

    /// <summary>Creates the service.</summary>
    public BackupService(AppPaths paths, ISettingsStore settings, IProfileStore profiles, IProfileApplier applier)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(applier);
        _paths = paths;
        _settings = settings;
        _profiles = profiles;
        _applier = applier;
    }

    /// <inheritdoc/>
    /// <remarks>A relative path is placed under <c>&lt;DataDir&gt;\backups</c>. Returns the full path written.</remarks>
    public string Export(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        string full = Path.IsPathRooted(zipPath) ? Path.GetFullPath(zipPath) : Path.GetFullPath(Path.Combine(_paths.DataDir, "backups", zipPath));
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = full + ".tmp";

        var profiles = _profiles.List();
        var captured = _applier.Capture("captured-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        var manifest = new BackupManifest(FormatVersion, "UnlockGPU",
            typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "?", DateTime.UtcNow, profiles.Count);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            Write(zip, ManifestEntry, manifest);
            Write(zip, SettingsEntry, _settings.Current);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in profiles)
            {
                string file = ProfileStore.FileNameFor(p.Name);
                if (used.Add(file)) Write(zip, ProfilesPrefix + file, p);
            }
            Write(zip, CapturedEntry, captured);
        }
        File.Move(tmp, full, overwrite: true);
        return full;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidDataException">Not a valid backup (message is <see cref="ServiceTextKeys.BackupInvalid"/>).</exception>
    public void Import(string zipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        AppSettings? settings;
        var profiles = new List<Profile>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = Read<BackupManifest>(zip.GetEntry(ManifestEntry));
            if (manifest == null || manifest.Format < 1 || manifest.Format > FormatVersion)
                throw Invalid("manifest");
            settings = Read<AppSettings>(zip.GetEntry(SettingsEntry));
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(ProfilesPrefix, StringComparison.Ordinal) ||
                    !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (profiles.Count >= MaxProfiles) throw Invalid("too many profiles");
                var p = Read<Profile>(entry);
                if (p == null) continue;
                // Valida o nome antes de gravar qualquer coisa: um nome ruim nao pode deixar restauracao parcial.
                try { _ = ProfileStore.FileNameFor(p.Name); }
                catch (ArgumentException) { throw Invalid(entry.FullName + " invalid profile name"); }
                profiles.Add(p);
            }
        }
        catch (JsonException e) { throw new InvalidDataException(ApplyError.Compose(ServiceTextKeys.BackupInvalid, e.Message), e); }

        // Validated fully before writing anything.
        if (settings != null) _settings.Save(KeepLocalStartup(settings, _settings.Current));
        foreach (var p in profiles) _profiles.Save(p);
    }

    /// <summary>
    /// Os campos de inicializacao sao locais da maquina: a tarefa agendada nao e alterada pelo import, entao eles
    /// continuam vindo das configuracoes atuais.
    /// </summary>
    static AppSettings KeepLocalStartup(AppSettings imported, AppSettings current) => imported with
    {
        StartWithWindows = current.StartWithWindows,
        StartupProfile = current.StartupProfile,
        ApplyProfileAtStartup = current.ApplyProfileAtStartup,
        StartInTray = current.StartInTray,
    };

    static InvalidDataException Invalid(string detail) => new(ApplyError.Compose(ServiceTextKeys.BackupInvalid, detail));

    static void Write<T>(ZipArchive zip, string name, T value)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        JsonSerializer.Serialize(s, value, JsonStore.Options);
    }

    static T? Read<T>(ZipArchiveEntry? entry) where T : class
    {
        if (entry == null) return null;
        if (entry.Length > MaxEntryBytes) throw Invalid(entry.FullName + " too large");
        using var s = entry.Open();
        return JsonSerializer.Deserialize<T>(s, JsonStore.Options);
    }

    /// <summary>Backup metadata.</summary>
    internal sealed record BackupManifest(int Format, string App, string AppVersion, DateTime CreatedUtc, int Profiles);
}
