using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

public sealed class SettingsStoreTests : TempDirTest
{
    [Test]
    public void Defaults_SaveAndReload_RaisesChanged()
    {
        var store = new SettingsStore(Paths);
        Check.Equal(2, store.Current.RefreshSeconds);
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;
        var updated = store.Current with { Language = "pt", AdapterWatts = 330, StartupProfile = "Jogo" };
        store.Save(updated);
        Check.Equal(updated, seen);
        Check.Equal(updated, new SettingsStore(Paths).Current);
    }

    [Test]
    public void CorruptFile_FallsBackToDefaults()
    {
        File.WriteAllText(Paths.SettingsFile, "{ not json");
        Check.Equal(new AppSettings(), new SettingsStore(Paths).Current);
    }
}

public sealed class ProfileStoreTests : TempDirTest
{
    [Test]
    public void RoundTrip_WithUnsafeNames()
    {
        var store = new ProfileStore(Paths);
        string[] names = ["Jogo leve", "a/b:c*?", "CON", "..", "Perfil \"x\" <1>", "COM1.backup"];
        foreach (var n in names)
            store.Save(new Profile { Name = n, CoreOffsetMHz = 100, MemOffsetMHz = 1000, VramLockMHz = 14001, PowerLimitW = 150.5, Notes = n });

        Check.Equal(names.Length, store.List().Count);
        Check.Equal(names.Length, Directory.GetFiles(Paths.ProfilesDir, "*.json").Length);
        foreach (var n in names)
        {
            var p = Check.NotNull(store.Get(n));
            Check.Equal(n, p.Name);
            Check.Equal(150.5, p.PowerLimitW);
            Check.Equal(14001u, p.VramLockMHz);
        }
        foreach (var f in Directory.GetFiles(Paths.ProfilesDir))
            Check.True(Path.GetFullPath(f).StartsWith(Path.GetFullPath(Paths.ProfilesDir), StringComparison.OrdinalIgnoreCase));
        Check.False(ProfileStore.FileNameFor("CON").StartsWith("CON.", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void Names_AreCaseInsensitive_AndSaveReplaces()
    {
        var store = new ProfileStore(Paths);
        store.Save(new Profile { Name = "Game", CoreOffsetMHz = 100 });
        store.Save(new Profile { Name = "game", CoreOffsetMHz = 200 });
        Check.Equal(1, store.List().Count);
        Check.Equal(200, store.Get("GAME")!.CoreOffsetMHz);
        Check.True(store.Delete("gAmE"));
        Check.False(store.Delete("gAmE"));
        Check.Equal(0, store.List().Count);
    }

    [Test]
    public void InvalidName_Throws()
    {
        var store = new ProfileStore(Paths);
        Check.Throws<ArgumentException>(() => store.Save(new Profile { Name = "  " }));
        Check.Equal(null, store.Get(""));
    }
}

public sealed class StartupServiceTests
{
    const string Exe = @"C:\Program Files\Unlock GPU\UnlockGPU.exe";
    readonly StartupService _svc = new(new MemoryLog(), Exe, @"PC\user");
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Test]
    public void Xml_HasLogonTriggerDelayElevationAndCommand()
    {
        string xml = _svc.BuildTaskXml("Jogo & <leve>");
        Check.True(xml.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", StringComparison.Ordinal));
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;
        Check.Equal(Ns + "Task", root.Name);
        var trigger = root.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;
        Check.Equal("PT30S", trigger.Element(Ns + "Delay")!.Value);
        Check.Equal(@"PC\user", trigger.Element(Ns + "UserId")!.Value);
        var principal = root.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Check.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        Check.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        var exec = root.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        Check.Equal(Exe, exec.Element(Ns + "Command")!.Value);
        Check.Equal("--silent --apply-profile \"Jogo & <leve>\"", exec.Element(Ns + "Arguments")!.Value);
        Check.Equal(@"C:\Program Files\Unlock GPU", exec.Element(Ns + "WorkingDirectory")!.Value);
        var settings = root.Element(Ns + "Settings")!;
        Check.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
    }

    [Test]
    public void Xml_RejectsNamesThatBreakTheCommandLine()
    {
        Check.Throws<ArgumentException>(() => _svc.BuildTaskXml("a\"b"));
        Check.Throws<ArgumentException>(() => _svc.BuildTaskXml(" "));
        Check.Throws<ArgumentException>(() => _svc.BuildTaskXml("a\nb"));
        Check.Throws<ArgumentException>(() => _svc.BuildTaskXml("perfil\\"));
        Check.Throws<ArgumentException>(() => StartupService.BuildArguments(new StartupOptions(true, "x\\")));
    }

    [Test]
    public void Arguments_PerMode()
    {
        Check.Equal("--minimized --apply-profile \"x\"", StartupService.BuildArguments(new StartupOptions(true, "x")));
        Check.Equal("--minimized", StartupService.BuildArguments(new StartupOptions(true, null)));
        Check.Equal("--silent --apply-profile \"x\"", StartupService.BuildArguments(new StartupOptions(false, "x")));
        Check.Equal("", StartupService.BuildArguments(new StartupOptions(false, null)));
    }

    [Test]
    public void Xml_ExecutionTimeLimit_PerMode()
    {
        Check.Equal("PT0S", TimeLimit(_svc.BuildTaskXml(new StartupOptions(true, "x"))));
        Check.Equal("PT0S", TimeLimit(_svc.BuildTaskXml(new StartupOptions(true, null))));
        Check.Equal("PT5M", TimeLimit(_svc.BuildTaskXml(new StartupOptions(false, "x"))));
    }

    static string TimeLimit(string xml) =>
        XDocument.Parse(xml).Root!.Element(Ns + "Settings")!.Element(Ns + "ExecutionTimeLimit")!.Value;
}

public sealed class ProfileApplierTests : TempDirTest
{
    readonly FakeGpu _gpu = new();

    ProfileApplier Create(out PowerService power)
    {
        power = new PowerService(_gpu, Paths, Log);
        return new ProfileApplier(_gpu, new OffsetService(_gpu, Log), new VramLockService(_gpu, Paths, Log), power, Log);
    }

    [Test]
    public void Apply_AppliesOnlyGivenItems_InOrder()
    {
        var applier = Create(out _);
        var results = applier.Apply(new Profile { Name = "p", MemOffsetMHz = 1000, VramLockMHz = 14001 });
        Check.Equal(2, results.Count);
        Check.True(results.All(r => r.Ok));
        Check.Equal(1000, _gpu.GetOffset(ClockDomain.Memory)!.CurrentMHz);
        Check.Equal(0, _gpu.GetOffset(ClockDomain.Core)!.CurrentMHz);
        Check.Equal(14001u, _gpu.LockedMHz);
        Check.Equal(0, _gpu.SetPowerCalls);

        var unlock = applier.Apply(new Profile { Name = "u", VramLockMHz = 0 });
        Check.Equal(1, unlock.Count);
        Check.Equal(null, _gpu.LockedMHz);
    }

    [Test]
    public void Apply_PowerOnNotSupportedLaptop_FailsCleanly()
    {
        _gpu.SetPowerRc = 3;
        var results = Create(out _).Apply(new Profile { Name = "p", PowerLimitW = 150 });
        Check.False(results[0].Ok);
        Check.Equal(ServiceTextKeys.PowerNotSupported, ApplyError.Split(results[0].ErrorText).Key);
    }

    [Test]
    public void Capture_And_Reset()
    {
        var applier = Create(out var power);
        _gpu.ForceOffset(ClockDomain.Core, 120);
        _gpu.ForceOffset(ClockDomain.Memory, 2000);
        power.Probe();
        var cap = applier.Capture("now");
        Check.Equal(120, cap.CoreOffsetMHz);
        Check.Equal(2000, cap.MemOffsetMHz);
        Check.Equal((uint?)null, cap.VramLockMHz);   // no lock active -> profile leaves the VRAM clock alone
        Check.Equal(175.0, cap.PowerLimitW);

        var reset = applier.ResetToDefaults();
        Check.True(reset.All(r => r.Ok));
        Check.Equal(0, _gpu.GetOffset(ClockDomain.Core)!.CurrentMHz);
        Check.Equal(1, _gpu.ResetCalls);
    }
}

public sealed class BackupServiceTests : TempDirTest
{
    [Test]
    public void ExportImport_RoundTrip()
    {
        var gpu = new FakeGpu();
        gpu.ForceOffset(ClockDomain.Memory, 1500);
        var settings = new SettingsStore(Paths);
        var profiles = new ProfileStore(Paths);
        settings.Save(settings.Current with { Language = "pt", GraphMinutes = 5 });
        profiles.Save(new Profile { Name = "A/1", CoreOffsetMHz = 50 });
        profiles.Save(new Profile { Name = "Bateria", VramLockMHz = 0, Notes = "ç ã é" });
        var applier = new ProfileApplier(gpu, new OffsetService(gpu, Log), new VramLockService(gpu, Paths, Log), new PowerService(gpu, Paths, Log), Log);

        string zip = new BackupService(Paths, settings, profiles, applier).Export(Path.Combine(Root, "out", "backup.zip"));
        Check.True(File.Exists(zip));
        using (var z = ZipFile.OpenRead(zip))
        {
            var names = z.Entries.Select(e => e.FullName).ToHashSet();
            Check.True(names.Contains("manifest.json"));
            Check.True(names.Contains("settings.json"));
            Check.True(names.Contains("captured-current.json"));
            Check.Equal(2, names.Count(n => n.StartsWith("profiles/", StringComparison.Ordinal)));
        }

        var other = new AppPaths(Path.Combine(Root, "restore"));
        var settings2 = new SettingsStore(other);
        var profiles2 = new ProfileStore(other);
        new BackupService(other, settings2, profiles2, applier).Import(zip);
        Check.Equal(settings.Current, settings2.Current);
        Check.Equal(2, profiles2.List().Count);
        Check.Equal(50, profiles2.Get("a/1")!.CoreOffsetMHz);
        Check.Equal("ç ã é", profiles2.Get("Bateria")!.Notes);
        Check.Equal(0, gpu.LockCalls);   // import never touches the hardware
    }

    [Test]
    public void Import_RejectsZipWithoutManifest()
    {
        string zip = Path.Combine(Root, "bad.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("settings.json").Open()))
            w.Write("{}");
        var gpu = new FakeGpu();
        var settings = new SettingsStore(Paths);
        var profiles = new ProfileStore(Paths);
        var applier = new ProfileApplier(gpu, new OffsetService(gpu, Log), new VramLockService(gpu, Paths, Log), new PowerService(gpu, Paths, Log), Log);
        var e = Check.Throws<InvalidDataException>(() => new BackupService(Paths, settings, profiles, applier).Import(zip));
        Check.Equal(ServiceTextKeys.BackupInvalid, ApplyError.Split(e.Message).Key);
    }

    ProfileApplier NewApplier()
    {
        var gpu = new FakeGpu();
        return new ProfileApplier(gpu, new OffsetService(gpu, Log), new VramLockService(gpu, Paths, Log), new PowerService(gpu, Paths, Log), Log);
    }

    [Test]
    public void Import_BadProfileName_WritesNothing()
    {
        var applier = NewApplier();
        var settings = new SettingsStore(Paths);
        var profiles = new ProfileStore(Paths);
        settings.Save(settings.Current with { Language = "pt" });
        profiles.Save(new Profile { Name = "Bom", CoreOffsetMHz = 50 });
        string zip = new BackupService(Paths, settings, profiles, applier).Export(Path.Combine(Root, "out", "backup.zip"));
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
        using (var s = z.CreateEntry("profiles/zz-bad.json").Open())
            JsonSerializer.Serialize(s, new Profile { Name = "a\nb", CoreOffsetMHz = 10 }, JsonStore.Options);

        var other = new AppPaths(Path.Combine(Root, "restore"));
        var settings2 = new SettingsStore(other);
        var profiles2 = new ProfileStore(other);
        var e = Check.Throws<InvalidDataException>(() => new BackupService(other, settings2, profiles2, applier).Import(zip));
        Check.Equal(ServiceTextKeys.BackupInvalid, ApplyError.Split(e.Message).Key);
        Check.Equal(0, profiles2.List().Count);
        Check.Equal(new AppSettings(), new SettingsStore(other).Current);
    }

    [Test]
    public void Import_KeepsLocalStartupFields()
    {
        var applier = NewApplier();
        var settings = new SettingsStore(Paths);
        settings.Save(settings.Current with
        {
            Language = "pt", StartWithWindows = true, StartupProfile = "Origem", ApplyProfileAtStartup = false, StartInTray = false,
        });
        string zip = new BackupService(Paths, settings, new ProfileStore(Paths), applier).Export(Path.Combine(Root, "out", "backup.zip"));

        var other = new AppPaths(Path.Combine(Root, "restore"));
        var settings2 = new SettingsStore(other);
        settings2.Save(settings2.Current with
        {
            Language = "en", StartWithWindows = false, StartupProfile = "Local", ApplyProfileAtStartup = true, StartInTray = true,
        });
        new BackupService(other, settings2, new ProfileStore(other), applier).Import(zip);
        var result = new SettingsStore(other).Current;
        Check.Equal("pt", result.Language);
        Check.False(result.StartWithWindows);
        Check.Equal("Local", result.StartupProfile);
        Check.True(result.ApplyProfileAtStartup);
        Check.True(result.StartInTray);
    }
}
