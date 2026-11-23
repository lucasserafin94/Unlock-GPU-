using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.App.Services;

/// <summary>Turns service results and error texts (<see cref="ApplyError"/> convention) into user text.</summary>
public static class ResultText
{
    /// <summary>"Requested X, read back Y" or the translated error with the driver detail.</summary>
    public static string Describe(Localizer loc, ApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Ok)
            return loc.T("ui.result.ok", ("requested", result.RequestedText), ("readBack", result.ReadBackText));
        string error = Error(loc, result.ErrorText);
        return string.IsNullOrEmpty(result.ReadBackText)
            ? loc.T("ui.result.failed", ("requested", result.RequestedText), ("error", error))
            : loc.T("ui.result.failedReadBack", ("requested", result.RequestedText), ("error", error), ("readBack", result.ReadBackText));
    }

    /// <summary>All results, one per line.</summary>
    public static string Describe(Localizer loc, IReadOnlyList<ApplyResult> results)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(results);
        return results.Count == 0 ? loc.T("ui.result.nothing") : string.Join(Environment.NewLine, results.Select(r => Describe(loc, r)));
    }

    /// <summary>Translates an error text "key" or "key|detail".</summary>
    public static string Error(Localizer loc, string? errorText)
    {
        ArgumentNullException.ThrowIfNull(loc);
        var (key, detail) = ApplyError.Split(errorText);
        if (key.Length == 0) return loc.T("ui.error.unknown");
        string text = loc.T(key);
        return string.IsNullOrEmpty(detail) ? text : text + " (" + detail + ")";
    }

    /// <summary>Text for an exception thrown by a service (its message follows the key|detail convention when known).</summary>
    public static string Exception(Localizer loc, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(loc);
        ArgumentNullException.ThrowIfNull(ex);
        var (key, _) = ApplyError.Split(ex.Message);
        bool isKey = key.Length > 0 && key.Contains('.', StringComparison.Ordinal) && !key.Contains(' ', StringComparison.Ordinal);
        return isKey ? Error(loc, ex.Message) : loc.T("ui.error.unexpected", ("message", ex.Message));
    }
}
