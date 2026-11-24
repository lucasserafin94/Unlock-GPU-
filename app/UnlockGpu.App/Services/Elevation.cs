using System.Diagnostics;
using System.Security.Principal;

namespace UnlockGpu.App.Services;

/// <summary>
/// Self-elevation. The manifest asks for "highest available" so any launcher (installer, Task Scheduler, Explorer)
/// can start the process without failing with ERROR_ELEVATION_REQUIRED; when admin is actually needed and we are not
/// elevated, re-launch ourselves with a single UAC prompt.
/// </summary>
public static class Elevation
{
    /// <summary>The current process runs with an elevated token.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Re-launches the current executable elevated, forwarding all arguments. Returns true when a new process was
    /// started (the caller should exit), false on failure or UAC cancel (keep running unelevated).
    /// </summary>
    public static bool RelaunchElevated(IReadOnlyList<string> args)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true, // required for the "runas" verb
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(exe),
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC cancelled or no admin available: keep running unelevated.
            return false;
        }
    }
}
