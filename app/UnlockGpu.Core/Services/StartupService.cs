using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using UnlockGpu.Core.Infrastructure;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Start with Windows through a Task Scheduler task (imported with schtasks.exe /XML): at logon of the current user,
/// 30 s delay, highest available privileges, running <c>"&lt;exe&gt;" --silent --apply-profile "&lt;name&gt;"</c>.
/// Errors are thrown as <see cref="InvalidOperationException"/> whose message follows <see cref="ApplyError"/>.
/// </summary>
public sealed class StartupService : IStartupService
{
    /// <summary>Task name in Task Scheduler (root folder).</summary>
    public const string TaskName = "UnlockGPU";
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(20);

    readonly ILog _log;
    readonly string? _exePath;
    readonly string _userId;

    /// <summary>Creates the service.</summary>
    /// <param name="log">Log.</param>
    /// <param name="exePath">Executable to start (defaults to the current process).</param>
    /// <param name="userId">Logon user (defaults to DOMAIN\user of the current process).</param>
    public StartupService(ILog log, string? exePath = null, string? userId = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _exePath = exePath ?? Environment.ProcessPath;
        _userId = userId ?? Environment.UserDomainName + "\\" + Environment.UserName;
    }

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            if (_exePath == null) return false;
            var (code, output) = RunSchtasks("/Query", "/TN", TaskName, "/XML");
            return code == 0 && output.Contains(_exePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc/>
    public void Enable(string profileName)
    {
        string xml = BuildTaskXml(profileName);
        string tmp = Path.Combine(Path.GetTempPath(), "UnlockGPU-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var (code, output) = RunSchtasks("/Create", "/TN", TaskName, "/XML", tmp, "/F");
            if (code != 0)
            {
                _log.Warn($"STARTUP create failed ({code}): {output.Trim()}");
                throw new InvalidOperationException(ApplyError.Compose(ServiceTextKeys.StartupCreateFailed, output.Trim()));
            }
            _log.Info($"STARTUP enabled with profile '{profileName}'");
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    /// <inheritdoc/>
    public void Disable()
    {
        var (exists, _) = RunSchtasks("/Query", "/TN", TaskName);
        if (exists != 0) return;
        var (code, output) = RunSchtasks("/Delete", "/TN", TaskName, "/F");
        if (code != 0)
        {
            _log.Warn($"STARTUP delete failed ({code}): {output.Trim()}");
            throw new InvalidOperationException(ApplyError.Compose(ServiceTextKeys.StartupDeleteFailed, output.Trim()));
        }
        _log.Info("STARTUP disabled");
    }

    /// <summary>Command-line arguments for the task.</summary>
    public static string BuildArguments(string profileName) => $"--silent --apply-profile \"{ValidName(profileName)}\"";

    /// <summary>Task Scheduler XML (schema 1.2) for the startup task. Pure: does not touch the system.</summary>
    public string BuildTaskXml(string profileName)
    {
        string args = BuildArguments(profileName);
        if (string.IsNullOrEmpty(_exePath))
            throw new InvalidOperationException(ServiceTextKeys.StartupNoExe);

        var doc = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.2"),
                new XElement(Ns + "RegistrationInfo",
                    new XElement(Ns + "Description", "Unlock GPU: applies the selected profile at logon."),
                    new XElement(Ns + "URI", "\\" + TaskName)),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", _userId),
                        new XElement(Ns + "Delay", "PT30S"))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", _userId),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "StartWhenAvailable", "true"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT5M"),
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "Priority", "7")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", _exePath),
                        new XElement(Ns + "Arguments", args),
                        new XElement(Ns + "WorkingDirectory", Path.GetDirectoryName(_exePath) ?? "")))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + doc.ToString();
    }

    static string ValidName(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Contains('"', StringComparison.Ordinal) || trimmed.Any(char.IsControl))
            throw new ArgumentException(ServiceTextKeys.StartupInvalidName, nameof(name));
        return trimmed;
    }

    (int Code, string Output) RunSchtasks(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(SchtasksTimeout))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return (-1, "schtasks timeout");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            _log.Error("STARTUP schtasks", e);
            return (-1, e.Message);
        }
    }
}
