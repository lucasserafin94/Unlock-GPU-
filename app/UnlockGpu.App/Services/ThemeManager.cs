using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace UnlockGpu.App.Services;

/// <summary>
/// Live accent colour switch: replaces the accent resources in the application dictionary. Every style uses
/// DynamicResource for them, so the change is visible immediately.
/// </summary>
public sealed class ThemeManager
{
    /// <summary>Preset accent colours offered in Settings.</summary>
    public static IReadOnlyList<string> Presets { get; } =
        ["#BAF568", "#76B900", "#4FC3F7", "#7C8CFF", "#C792EA", "#FF7AB6", "#FFB454", "#FF6B6B"];

    /// <summary>Default accent when the stored value is invalid.</summary>
    public const string DefaultAccent = "#BAF568";

    readonly ResourceDictionary _resources;

    /// <summary>Creates the manager over the application resources.</summary>
    public ThemeManager(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _resources = resources;
    }

    /// <summary>Current accent as #RRGGBB.</summary>
    public string Current { get; private set; } = DefaultAccent;

    /// <summary>Raised after the accent changed.</summary>
    public event EventHandler? AccentChanged;

    /// <summary>Applies <paramref name="hex"/>; returns false (and keeps the current accent) when it is not a valid colour.</summary>
    public bool Apply(string? hex)
    {
        if (!TryParse(hex, out var c)) return false;
        Current = ToHex(c);
        _resources["AccentColor"] = c;
        _resources["AccentBrush"] = Frozen(c);
        _resources["AccentSoftBrush"] = Frozen(Color.FromArgb(0x30, c.R, c.G, c.B));
        _resources["AccentHoverBrush"] = Frozen(Blend(c, Colors.White, 0.18));
        _resources["AccentForegroundBrush"] = Frozen(Luminance(c) > 0.55 ? Color.FromRgb(0x10, 0x14, 0x18) : Colors.White);
        AccentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Parses #RGB or #RRGGBB (the leading # is optional).</summary>
    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    /// <summary>Formats a colour as #RRGGBB.</summary>
    public static string ToHex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

    static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;
}
