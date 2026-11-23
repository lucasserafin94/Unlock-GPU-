using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace UnlockGpu.Core.Services;

/// <summary>
/// Reads the Windows System event log (read-only) through the public Windows Event Log API (wevtapi.dll).
/// Used instead of System.Diagnostics.Eventing.Reader because that assembly is not part of the base .NET runtime
/// referenced by the core library (no extra packages allowed).
/// </summary>
public sealed partial class WindowsSystemEventSource : ISystemEventSource
{
    const string KernelPower = "Microsoft-Windows-Kernel-Power";
    const string Whea = "Microsoft-Windows-WHEA-Logger";
    const int MaxEvents = 50;
    const int EvtQueryChannelPath = 0x1;
    const int EvtQueryReverseDirection = 0x200;
    const int EvtRenderEventXml = 1;
    const int ErrorNoMoreItems = 259;
    const int ErrorInsufficientBuffer = 122;
    static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>XPath query used against the System log (exposed for diagnostics and tests).</summary>
    public static string BuildQuery(DateTime sinceUtc) =>
        "*[System[((Provider[@Name='" + KernelPower + "'] and EventID=41) or Provider[@Name='" + Whea + "']) and " +
        "TimeCreated[@SystemTime>='" + sinceUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "']]]";

    /// <inheritdoc/>
    /// <exception cref="Win32Exception">The log could not be queried.</exception>
    public IReadOnlyList<SystemEventRecord> Query(DateTime sinceUtc)
    {
        var list = new List<SystemEventRecord>();
        IntPtr query = EvtQuery(IntPtr.Zero, "System", BuildQuery(sinceUtc), EvtQueryChannelPath | EvtQueryReverseDirection);
        if (query == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var handles = new IntPtr[16];
            while (list.Count < MaxEvents)
            {
                if (!EvtNext(query, handles.Length, handles, 1000, 0, out int returned))
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == ErrorNoMoreItems) break;
                    throw new Win32Exception(err);
                }
                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        if (Render(handles[i]) is { } xml && Parse(xml) is { } rec && list.Count < MaxEvents) list.Add(rec);
                    }
                    finally { _ = EvtClose(handles[i]); }
                }
            }
        }
        finally { _ = EvtClose(query); }
        return list;
    }

    /// <summary>Parses one rendered event XML.</summary>
    public static SystemEventRecord? Parse(string xml)
    {
        try
        {
            var sys = XDocument.Parse(xml).Root?.Element(Ns + "System");
            if (sys == null) return null;
            string provider = sys.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "?";
            int id = int.TryParse(sys.Element(Ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
            string? t = sys.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            var time = DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d : DateTime.MinValue;
            return new SystemEventRecord(time, provider, id);
        }
        catch (System.Xml.XmlException) { return null; }
    }

    static string? Render(IntPtr evt)
    {
        if (!EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, 0, IntPtr.Zero, out int used, out _)
            && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) return null;
        IntPtr buffer = Marshal.AllocHGlobal(used);
        try
        {
            return EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, used, buffer, out _, out _)
                ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [LibraryImport("wevtapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtNext(IntPtr resultSet, int eventsSize, [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [LibraryImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, IntPtr buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport("wevtapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EvtClose(IntPtr handle);
}
