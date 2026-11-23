using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using UnlockGpu.Core.Infrastructure;
using UnlockGpu.Core.Model;
using UnlockGpu.Core.Nvml;
using UnlockGpu.Core.Oem;
using UnlockGpu.Core.Services;

namespace UnlockGpu.Patch;

/// <summary>One romOverride slot as stored in the driver class key (hex, uppercase).</summary>
public sealed record RomSlotInfo(int Index, int SizeBytes, string Hex);

/// <summary>
/// Persistent power-ceiling override. Writes <c>romOverrideXX</c> REG_BINARY values into the GPU's driver class
/// key — the mechanism that passes the vBIOS factory ceiling without rewriting the ROM — and hot-restarts the
/// device so the driver re-reads them on start. After a successful stage + restart, NVML's MaxLimitW grows on
/// its own; no other service needs to change. Reads work unelevated; writes need elevation.
/// </summary>
public interface IRomOverrideService
{
    /// <summary>Finds the NVIDIA display adapter's PnP instance ID via WMI; null when not found.</summary>
    string? FindGpuPnpId();

    /// <summary>Lists every romOverride slot currently in the driver class key (read-only).</summary>
    IReadOnlyList<RomSlotInfo> ReadSlots(string pnpInstanceId, out string error);

    /// <summary>Writes all slots from a capture file into the driver class key. Blobs are stored verbatim.</summary>
    ApplyResult StageCaptured(string pnpInstanceId, string captureFile);

    /// <summary>Deletes romOverride00..15 (back to the factory vBIOS ceiling).</summary>
    ApplyResult ClearAll(string pnpInstanceId);

    /// <summary>Hot-restarts the device and verifies NVML's max limit reached expectedMaxW.</summary>
    Task<ApplyResult> HotRestartAsync(string pnpInstanceId, double? expectedMaxW, CancellationToken ct = default);
}

public sealed class RomOverrideService : IRomOverrideService
{
    readonly IGpu _gpu;
    readonly ILog _log;
    readonly WriteGate? _gate;

    // Capture-file line: "romOverride00  14B 98 03 5E 00 1E 36 7C 05 00 04 90 D0 03 00"
    static readonly Regex SlotLine = new(
        @"^romOverride([0-9A-Fa-f]{2})\s+(\d+)B\s+((?:[0-9A-Fa-f]{2}\s*)+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly Regex HexByte = new(@"[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    public RomOverrideService(IGpu gpu, ILog log, WriteGate? gate = null)
    {
        _gpu = gpu;
        _log = log;
        _gate = gate;
    }

    /// <inheritdoc/>
    public string? FindGpuPnpId()
    {
        object services;
        try
        {
            services = WbemScripting.Connect("root\\cimv2");
        }
        catch (Exception ex)
        {
            _log.Warn("ROM OVERRIDE: WMI unavailable: " + ex.Message);
            return null;
        }

        try
        {
            dynamic set = ((dynamic)services).ExecQuery(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Display' AND Name LIKE 'NVIDIA%'", "WQL");
            try
            {
                foreach (object item in WbemScripting.Items(set))
                {
                    string id = WbemScripting.PropertyString((dynamic)item, "DeviceID");
                    if (GpuHotRestart.ValidPnpId(id)) return id;
                }
                _log.Warn("ROM OVERRIDE: no NVIDIA display device found via WMI.");
                return null;
            }
            finally { WbemScripting.Release(set); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or MissingMemberException)
        {
            _log.Warn("ROM OVERRIDE: WMI query failed: " + ex.Message);
            return null;
        }
        finally { WbemScripting.Release(services); }
    }

    /// <inheritdoc/>
    public IReadOnlyList<RomSlotInfo> ReadSlots(string pnpInstanceId, out string error)
    {
        var list = new List<RomSlotInfo>();
        using RegistryKey? key = RomSlot.OpenDeviceKey(pnpInstanceId, writable: false, out error);
        if (key == null) return list;
        foreach (var kv in RomSlot.ReadAllSlots(key, RomSlot.DefaultMaxSlot).OrderBy(kv => kv.Key))
            list.Add(new RomSlotInfo(kv.Key, kv.Value?.Length ?? 0, ToHex(kv.Value)));
        return list;
    }

    /// <inheritdoc/>
    public ApplyResult StageCaptured(string pnpInstanceId, string captureFile)
    {
        const string requested = "stage capture";
        if (_gate?.IsBlocked == true) return ApplyError.Fail(requested, ServiceTextKeys.TunerBusy);
        if (!File.Exists(captureFile))
            return ApplyError.Fail(requested, ServiceTextKeys.RomCaptureInvalid, captureFile);

        var staged = new Dictionary<int, byte[]>();
        foreach (Match m in SlotLine.Matches(File.ReadAllText(captureFile)))
        {
            int index = Convert.ToInt32(m.Groups[1].Value, 16);
            int declared = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var bytes = new List<byte>(declared);
            foreach (Match h in HexByte.Matches(m.Groups[3].Value))
                bytes.Add(Convert.ToByte(h.Value, 16));
            if (bytes.Count != declared)
                return ApplyError.Fail(requested, ServiceTextKeys.RomCaptureInvalid, RomSlot.SlotName(index));

            // Reject blobs the driver would not understand (neither canonical nor sized).
            int shadow, raw, width; byte[] payload; string mode;
            if (!RomSlot.TryParseCanonical(bytes.ToArray(), out shadow, out raw) &&
                !RomSlot.TryParseSized(bytes.ToArray(), null, out shadow, out width, out payload, out mode))
                return ApplyError.Fail(requested, ServiceTextKeys.RomCaptureInvalid, RomSlot.SlotName(index));

            staged[index] = bytes.ToArray();
        }
        if (staged.Count == 0)
            return ApplyError.Fail(requested, ServiceTextKeys.RomCaptureInvalid, captureFile);

        using RegistryKey? key = RomSlot.OpenDeviceKey(pnpInstanceId, writable: true, out string error);
        if (key == null)
            return ApplyError.Fail(requested, ServiceTextKeys.RomKeyOpen, error);

        try
        {
            foreach (var kv in staged.OrderBy(kv => kv.Key))
                key.SetValue(RomSlot.SlotName(kv.Key), kv.Value, RegistryValueKind.Binary);
        }
        catch (Exception ex)
        {
            _log.Error("ROM OVERRIDE: stage failed", ex);
            return ApplyError.Fail(requested, ServiceTextKeys.RomKeyOpen, ex.Message);
        }

        // Read back and verify every staged slot byte-for-byte.
        var readBack = RomSlot.ReadAllSlots(key, RomSlot.DefaultMaxSlot);
        foreach (var kv in staged)
        {
            if (!readBack.TryGetValue(kv.Key, out byte[]? actual) || !actual.AsSpan().SequenceEqual(kv.Value))
                return ApplyError.Fail(requested, ServiceTextKeys.RomReadBackMismatch, RomSlot.SlotName(kv.Key));
        }

        string summary = string.Join(", ", staged.Keys.OrderBy(k => k).Select(k => RomSlot.SlotName(k)));
        _log.Info($"ROM OVERRIDE: staged {staged.Count} slot(s) [{summary}] into {pnpInstanceId}.");
        return new ApplyResult(true, requested, summary + " verified");
    }

    /// <inheritdoc/>
    public ApplyResult ClearAll(string pnpInstanceId)
    {
        const string requested = "clear all slots";
        if (_gate?.IsBlocked == true) return ApplyError.Fail(requested, ServiceTextKeys.TunerBusy);
        using RegistryKey? key = RomSlot.OpenDeviceKey(pnpInstanceId, writable: true, out string error);
        if (key == null)
            return ApplyError.Fail(requested, ServiceTextKeys.RomKeyOpen, error);

        int removed = 0;
        foreach (var kv in RomSlot.ReadAllSlots(key, RomSlot.DefaultMaxSlot).OrderBy(kv => kv.Key))
        {
            try
            {
                key.DeleteValue(RomSlot.SlotName(kv.Key));
                removed++;
            }
            catch (Exception ex)
            {
                return ApplyError.Fail(requested, ServiceTextKeys.RomKeyOpen, ex.Message);
            }
        }
        _log.Info($"ROM OVERRIDE: cleared {removed} slot(s) from {pnpInstanceId}.");
        return new ApplyResult(true, requested, removed + " slot(s) removed");
    }

    /// <inheritdoc/>
    public async Task<ApplyResult> HotRestartAsync(string pnpInstanceId, double? expectedMaxW, CancellationToken ct = default)
    {
        const string requested = "hot restart";
        if (_gate?.IsBlocked == true) return ApplyError.Fail(requested, ServiceTextKeys.TunerBusy);

        // Let the GPU go idle first so the driver can release it cleanly.
        GpuHotRestart.WaitForIdle(UtilizationPct, 20_000, out _);

        double liveBefore = CurrentMaxW();
        if (expectedMaxW.HasValue && !double.IsNaN(liveBefore) &&
            GpuHotRestart.ShouldSkipRestart(liveBefore, expectedMaxW.Value))
        {
            return new ApplyResult(true, requested, string.Create(CultureInfo.InvariantCulture, $"max already {liveBefore:0.##} W"));
        }

        bool ok = GpuHotRestart.RestartDevice(pnpInstanceId, out int code, out string detail);
        if (!ok)
        {
            _log.Warn("ROM OVERRIDE: pnputil /restart-device failed (" + detail + "); trying hard cycle.");
            ok = GpuHotRestart.HardCycle(pnpInstanceId, out string hcDetail);
            if (!ok)
                return ApplyError.Fail(requested, ServiceTextKeys.RomRestartFailed, detail + " | " + hcDetail);
        }

        // The driver re-reads the slots on device start; NVML may need a moment to come back.
        double max = double.NaN;
        var deadline = DateTime.UtcNow.AddSeconds(GpuHotRestart.RestartTimeoutMs / 1000.0 + 5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1_000, ct).ConfigureAwait(false);
            _gpu.Sample(); // self-heals a stale NVML handle after the device restart
            max = CurrentMaxW();
            if (!double.IsNaN(max))
            {
                if (expectedMaxW.HasValue && GpuHotRestart.VerifyMaxW(max, expectedMaxW.Value)) break;
                if (Math.Abs(max - liveBefore) > 0.05) break; // ceiling moved: the restart took effect
            }
        }

        string readBack = double.IsNaN(max) ? "max not readable yet" :
            string.Create(CultureInfo.InvariantCulture, $"max live {max:0.##} W");
        if (double.IsNaN(max))
            return ApplyError.Fail(requested, ServiceTextKeys.RomRestartFailed, "device restarted but NVML max limit is not readable yet");
        if (expectedMaxW.HasValue && !GpuHotRestart.VerifyMaxW(max, expectedMaxW.Value))
        {
            _log.Warn($"ROM OVERRIDE: max after restart is {max:0.##} W, target was {expectedMaxW.Value:0.##} W.");
            return ApplyError.Fail(requested, ServiceTextKeys.RomMaxMismatch, readBack);
        }

        _log.Info("ROM OVERRIDE: hot restart done; " + readBack + ".");
        return new ApplyResult(true, requested, readBack);
    }

    double? UtilizationPct()
    {
        try { return (double)_gpu.Sample().UtilGpu; } catch { return null; }
    }

    double CurrentMaxW()
    {
        try { var info = _gpu.Info; return info?.MaxLimitW ?? double.NaN; } catch { return double.NaN; }
    }

    static string ToHex(byte[]? data)
    {
        if (data == null || data.Length == 0) return "";
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
