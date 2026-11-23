using System.Globalization;
using System.Runtime.InteropServices;

namespace UnlockGpu.Core.Oem;

/// <summary>Raw access to GetSetULong. Implementations are not required to be thread-safe; callers serialise.</summary>
public interface IUniwillEcChannel : IDisposable
{
    /// <summary>Checks that the WMI class exists and that an instance can be opened. Read-only.</summary>
    OemStatus Probe();

    /// <summary>Calls GetSetULong(data) and returns the raw uint32 result.</summary>
    /// <exception cref="UnauthorizedAccessException">Not elevated.</exception>
    /// <exception cref="InvalidOperationException">Class/instance missing or WMI failure.</exception>
    uint Invoke(ulong data);
}

/// <summary>
/// <see cref="IUniwillEcChannel"/> over WMI root\wmi AcpiTest_MULong using the built-in WbemScripting COM objects.
/// The connection and the chosen instance are opened lazily and kept until <see cref="Dispose"/>.
/// </summary>
public sealed class WmiUniwillEcChannel : IUniwillEcChannel
{
    object? _services;
    object? _instance;
    bool _disposed;

    /// <inheritdoc />
    public OemStatus Probe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureOpen();
            return OemStatus.Available;
        }
        catch (UnauthorizedAccessException) { return OemStatus.AccessDenied; }
        catch (EcClassMissingException) { return OemStatus.WmiClassMissing; }
        catch (InvalidOperationException) { return OemStatus.Error; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return OemStatus.Error; }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The data is neither a well-formed EC read nor an EC write.</exception>
    public uint Invoke(ulong data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Last line of defence: only well-formed reads (op 0x0100) and writes (op 0x0000/0x0001) reach the firmware.
        if (!UniwillEc.IsRead(data) && !UniwillEc.IsWrite(data))
            throw new NotSupportedException("oem.writeDisabled");
        dynamic instance = EnsureOpen();
        dynamic? methods = null, method = null, inDef = null, inParams = null, props = null, dataProp = null, outParams = null;
        try
        {
            methods = instance.Methods_;
            method = methods.Item(UniwillEc.WmiMethod);
            inDef = method.InParameters;
            inParams = inDef.SpawnInstance_();
            props = inParams.Properties_;
            dataProp = props.Item("Data");
            // CIM uint64 travels as a decimal string in the scripting API.
            dataProp.Value = data.ToString(CultureInfo.InvariantCulture);
            outParams = instance.ExecMethod_(UniwillEc.WmiMethod, inParams);
            return UniwillEc.ToUInt32((object?)WbemScripting.Property(outParams, "Return"));
        }
        catch (COMException ex) when (WbemScripting.IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException("oem.accessDenied", ex);
        }
        catch (COMException ex)
        {
            // Conexao/instancia podem ter ficado invalidas (WMI reiniciado etc.): descarta para reconectar na proxima chamada.
            Reset();
            throw new InvalidOperationException($"GetSetULong failed (0x{ex.HResult:X8}).", ex);
        }
        finally
        {
            foreach (object? o in new object?[] { outParams, dataProp, props, inParams, inDef, method, methods })
                WbemScripting.Release(o);
        }
    }

    object EnsureOpen()
    {
        if (_instance != null) return _instance;
        try
        {
            _services ??= WbemScripting.Connect(UniwillEc.WmiNamespace);
            dynamic services = _services;
            dynamic classDef;
            try { classDef = services.Get(UniwillEc.WmiClass); }
            catch (COMException ex) when (WbemScripting.IsNotFound(ex)) { throw new EcClassMissingException(ex); }
            WbemScripting.Release(classDef);
            _instance = PickInstance(services);
            return _instance;
        }
        catch (COMException ex) when (WbemScripting.IsAccessDenied(ex))
        {
            throw new UnauthorizedAccessException("oem.accessDenied", ex);
        }
        catch (COMException ex) when (WbemScripting.IsNotFound(ex))
        {
            throw new EcClassMissingException(ex);
        }
        catch (COMException ex)
        {
            // Nao deixa um SWbemServices quebrado em cache.
            Reset();
            throw new InvalidOperationException($"WMI {UniwillEc.WmiClass} unavailable (0x{ex.HResult:X8}).", ex);
        }
    }

    /// <summary>Releases the cached COM objects so the next call reconnects from scratch.</summary>
    void Reset()
    {
        object? instance = _instance, services = _services;
        _instance = null;
        _services = null;
        WbemScripting.Release(instance);
        WbemScripting.Release(services);
    }

    /// <summary>First active instance by InstanceName (ordinal), matching what the ACPI WMI mapper exposes as instance 0.</summary>
    static object PickInstance(dynamic services)
    {
        dynamic set = services.InstancesOf(UniwillEc.WmiClass);
        var found = new List<(string Name, bool Active, object Obj)>();
        try
        {
            foreach (object item in WbemScripting.Items(set))
            {
                string name = WbemScripting.PropertyString(item, "InstanceName");
                object? active = WbemScripting.Property(item, "Active");
                found.Add((name, active is bool b ? b : true, item));
            }
        }
        finally
        {
            WbemScripting.Release(set);
        }

        var chosen = found.Where(f => f.Active).OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Obj).FirstOrDefault();
        foreach (var f in found)
            if (!ReferenceEquals(f.Obj, chosen)) WbemScripting.Release(f.Obj);
        return chosen ?? throw new EcClassMissingException(null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    sealed class EcClassMissingException(Exception? inner) : InvalidOperationException("oem.classMissing", inner);
}
