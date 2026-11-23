using System.Globalization;

namespace UnlockGpu.App.Services;

/// <summary>Culture-aware number formatting for display (files always use the invariant culture).</summary>
public static class Format
{
    /// <summary>Placeholder for an unknown value.</summary>
    public const string Unknown = "—";

    /// <summary>Watts with one decimal, or a dash when unknown.</summary>
    public static string Watts(double w, CultureInfo culture) =>
        double.IsNaN(w) ? Unknown : w.ToString("0.0", culture);

    /// <summary>Whole watts, or a dash when unknown.</summary>
    public static string WattsWhole(double w, CultureInfo culture) =>
        double.IsNaN(w) ? Unknown : w.ToString("0", culture);

    /// <summary>Integer with group separators, or a dash when unknown.</summary>
    public static string Number(double v, CultureInfo culture) =>
        double.IsNaN(v) ? Unknown : v.ToString("#,0", culture);

    /// <summary>Number with one decimal, or a dash when unknown.</summary>
    public static string OneDecimal(double v, CultureInfo culture) =>
        double.IsNaN(v) ? Unknown : v.ToString("#,0.0", culture);

    /// <summary>Amperes without trailing zeros (15 A, 15.001 A), or a dash when unknown.</summary>
    public static string Amperes(double a, CultureInfo culture) =>
        double.IsNaN(a) ? Unknown : a.ToString("0.###", culture) + " A";

    /// <summary>Signed MHz offset (+150, 0, -45).</summary>
    public static string Offset(int v, CultureInfo culture) =>
        v > 0 ? "+" + v.ToString(culture) : v.ToString(culture);

    /// <summary>Percentage of a 0..1 share.</summary>
    public static string Share(double share, CultureInfo culture) =>
        double.IsNaN(share) ? Unknown : (share * 100).ToString("0", culture) + " %";

    /// <summary>Duration as h:mm:ss.</summary>
    public static string Duration(TimeSpan d) =>
        ((int)d.TotalHours).ToString(CultureInfo.InvariantCulture) + d.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
}
