namespace UnlockGpu.App.Hosting;

/// <summary>
/// Parsed command line. Supported: <c>--silent</c>, <c>--apply-profile "&lt;name&gt;"</c> (also <c>--apply-profile=&lt;name&gt;</c>)
/// and <c>--minimized</c>. Unknown arguments are ignored (and reported in <see cref="Unknown"/>).
/// </summary>
public sealed record CommandLineOptions
{
    /// <summary>Run without UI and exit.</summary>
    public bool Silent { get; init; }
    /// <summary>Profile to apply.</summary>
    public string? ApplyProfile { get; init; }
    /// <summary>Start hidden in the tray.</summary>
    public bool Minimized { get; init; }
    /// <summary>Arguments that were not understood.</summary>
    public IReadOnlyList<string> Unknown { get; init; } = [];

    /// <summary>Parses the arguments (case-insensitive switches).</summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool silent = false, minimized = false;
        string? profile = null;
        var unknown = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (Is(a, "--silent")) silent = true;
            else if (Is(a, "--minimized")) minimized = true;
            else if (Is(a, "--apply-profile"))
            {
                if (i + 1 < args.Count) profile = args[++i].Trim().Trim('"');
                else unknown.Add(a);
            }
            else if (a.StartsWith("--apply-profile=", StringComparison.OrdinalIgnoreCase))
                profile = a["--apply-profile=".Length..].Trim().Trim('"');
            else unknown.Add(a);
        }
        return new CommandLineOptions
        {
            Silent = silent,
            ApplyProfile = string.IsNullOrWhiteSpace(profile) ? null : profile,
            Minimized = minimized,
            Unknown = unknown,
        };
    }

    static bool Is(string a, string option) => string.Equals(a, option, StringComparison.OrdinalIgnoreCase);
}
