using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Tests;

/// <summary>Controllable clock.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public ManualTime() : this(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)) { }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Log that keeps messages in memory.</summary>
public sealed class MemoryLog : ILog
{
    public List<string> Lines { get; } = [];
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warn(string message) => Lines.Add("WARN " + message);
    public void Error(string message, Exception? ex = null) => Lines.Add("ERRO " + message + (ex == null ? "" : ": " + ex.Message));
}

/// <summary>Scripted System log.</summary>
public sealed class FakeEventSource : ISystemEventSource
{
    public List<SystemEventRecord> Events { get; } = [];
    public int Queries { get; private set; }
    public IReadOnlyList<SystemEventRecord> Query(DateTime sinceUtc)
    {
        Queries++;
        return Events.Where(e => e.TimeUtc >= sinceUtc).ToArray();
    }
}

/// <summary>Base for tests that need a private temp data folder (deleted on dispose).</summary>
public abstract class TempDirTest : IDisposable
{
    protected TempDirTest()
    {
        Root = Path.Combine(Path.GetTempPath(), "UnlockGpuTests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
    }

    protected string Root { get; }
    protected AppPaths Paths { get; }
    protected MemoryLog Log { get; } = new();

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }
}
