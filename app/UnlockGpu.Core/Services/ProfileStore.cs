using System.Security.Cryptography;
using System.Text;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Profiles stored one JSON file per profile in <see cref="AppPaths.ProfilesDir"/>. The file name is derived from the
/// profile name (<see cref="FileNameFor"/>) and is always safe; the real name is kept inside the JSON.
/// Names are compared case-insensitively (ordinal).
/// </summary>
public sealed class ProfileStore : IProfileStore
{
    const int MaxStemLength = 60;
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    readonly AppPaths _paths;
    readonly Lock _gate = new();

    /// <summary>Creates the store over <see cref="AppPaths.ProfilesDir"/>.</summary>
    public ProfileStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc/>
    public IReadOnlyList<Profile> List()
    {
        lock (_gate) return LoadAll().Select(e => e.Profile).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <inheritdoc/>
    public Profile? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = name.Trim();
        lock (_gate)
        {
            var direct = JsonStore.Load<Profile>(Path.Combine(_paths.ProfilesDir, FileNameFor(key)));
            if (direct != null && Same(direct.Name, key)) return direct;
            return LoadAll().Select(e => e.Profile).FirstOrDefault(p => Same(p.Name, key));
        }
    }

    /// <inheritdoc/>
    public void Save(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string name = ValidName(profile.Name);
        var normalized = profile with { Name = name };
        string file = Path.Combine(_paths.ProfilesDir, FileNameFor(name));
        lock (_gate)
        {
            JsonStore.Save(file, normalized);
            foreach (var (path, p) in LoadAll())
                if (Same(p.Name, name) && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
        }
    }

    /// <inheritdoc/>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string key = name.Trim();
        bool any = false;
        lock (_gate)
        {
            foreach (var (path, p) in LoadAll())
                if (Same(p.Name, key)) { File.Delete(path); any = true; }
        }
        return any;
    }

    /// <summary>
    /// Safe file name for a profile: invalid characters become '_', reserved device names and any altered name get a
    /// short hash of the (case-insensitive) name appended so distinct names never collide.
    /// </summary>
    public static string FileNameFor(string name)
    {
        string trimmed = ValidName(name);
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(trimmed.Length);
        foreach (char c in trimmed)
            sb.Append(c < 32 || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string stem = sb.ToString().Trim(' ', '.');
        if (stem.Length > MaxStemLength) stem = stem[..MaxStemLength].TrimEnd(' ', '.');
        string device = stem.Split('.')[0];
        bool altered = !string.Equals(stem, trimmed, StringComparison.Ordinal);
        if (stem.Length == 0 || altered || Reserved.Contains(device))
            stem = (stem.Length == 0 ? "profile" : stem) + "-" + ShortHash(trimmed);
        return stem + ".json";
    }

    static string ValidName(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > 120 || trimmed.Any(char.IsControl))
            throw new ArgumentException(ServiceTextKeys.ProfileInvalidName, nameof(name));
        return trimmed;
    }

    static string ShortHash(string name)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    static bool Same(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    List<(string Path, Profile Profile)> LoadAll()
    {
        var list = new List<(string, Profile)>();
        if (!Directory.Exists(_paths.ProfilesDir)) return list;
        foreach (string f in Directory.EnumerateFiles(_paths.ProfilesDir, "*.json"))
        {
            var p = JsonStore.Load<Profile>(f);
            if (p != null && !string.IsNullOrWhiteSpace(p.Name)) list.Add((f, p));
        }
        return list;
    }
}
