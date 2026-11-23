using UnlockGpu.Core.Model;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Convention for <see cref="ApplyResult.ErrorText"/> and for exception messages thrown by the services:
/// <c>"&lt;localizer key&gt;"</c> or <c>"&lt;localizer key&gt;|&lt;technical detail&gt;"</c>.
/// The key is translated by the UI; the detail (driver message, value) is shown verbatim.
/// <see cref="ApplyResult.RequestedText"/> and <see cref="ApplyResult.ReadBackText"/> are culture-invariant
/// technical values (numbers and units), never prose.
/// </summary>
public static class ApplyError
{
    /// <summary>Separator between key and detail.</summary>
    public const char Separator = '|';

    /// <summary>Builds an error text from a key and an optional detail.</summary>
    public static string Compose(string key, string? detail = null) =>
        string.IsNullOrEmpty(detail) ? key : key + Separator + detail;

    /// <summary>Splits an error text into key and detail.</summary>
    public static (string Key, string? Detail) Split(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return ("", null);
        int i = errorText.IndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? (errorText, null) : (errorText[..i], errorText[(i + 1)..]);
    }

    /// <summary>A failed result.</summary>
    public static ApplyResult Fail(string requested, string key, string? detail = null, string readBack = "") =>
        new(false, requested, readBack, Compose(key, detail));
}
