using System.Globalization;
using System.Runtime.InteropServices;

namespace UnlockGpu.Core.Oem;

/// <summary>
/// Minimal late-bound access to the Windows "WbemScripting.SWbemLocator" COM automation object, which ships with
/// Windows. Used instead of System.Management so the app needs no NuGet package.
/// </summary>
internal static class WbemScripting
{
    /// <summary>WBEM_E_NOT_FOUND.</summary>
    public const int WbemNotFound = unchecked((int)0x80041002);
    /// <summary>WBEM_E_ACCESS_DENIED.</summary>
    public const int WbemAccessDenied = unchecked((int)0x80041003);
    /// <summary>WBEM_E_INVALID_CLASS.</summary>
    public const int WbemInvalidClass = unchecked((int)0x80041010);
    /// <summary>WBEM_E_INVALID_NAMESPACE.</summary>
    public const int WbemInvalidNamespace = unchecked((int)0x8004100E);
    /// <summary>E_ACCESSDENIED.</summary>
    public const int EAccessDenied = unchecked((int)0x80070005);

    /// <summary>Connects to a namespace on the local machine. Caller releases the returned SWbemServices.</summary>
    public static object Connect(string wmiNamespace)
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        dynamic locator = Activator.CreateInstance(type)!;
        try
        {
            return (object)locator.ConnectServer(".", wmiNamespace);
        }
        finally
        {
            Release(locator);
        }
    }

    /// <summary>
    /// Materialises an SWbemObjectSet with Count/ItemIndex. (Late-bound foreach over the set can yield nulls instead
    /// of raising access-denied; Count forces the query and reports errors.) Caller releases the returned objects.
    /// </summary>
    public static List<object> Items(dynamic objectSet)
    {
        int count = (int)objectSet.Count;
        var list = new List<object>(count);
        for (int i = 0; i < count; i++)
        {
            object? item = objectSet.ItemIndex(i);
            if (item != null) list.Add(item);
        }
        return list;
    }

    /// <summary>Reads a property of an SWbemObject; null when missing or NULL.</summary>
    public static object? Property(dynamic wbemObject, string name)
    {
        dynamic props = wbemObject.Properties_;
        try
        {
            dynamic prop = props.Item(name);
            try { return (object?)prop.Value; }
            finally { Release(prop); }
        }
        finally
        {
            Release(props);
        }
    }

    /// <summary>Property as trimmed string ("" when null).</summary>
    public static string PropertyString(dynamic wbemObject, string name) =>
        Convert.ToString((object?)Property(wbemObject, name), CultureInfo.InvariantCulture)?.Trim() ?? "";

    /// <summary>Releases a COM object if it is one.</summary>
    public static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
    }

    /// <summary>True for HRESULTs that mean "not allowed" (needs elevation).</summary>
    public static bool IsAccessDenied(COMException ex) => ex.HResult is WbemAccessDenied or EAccessDenied;

    /// <summary>True for HRESULTs that mean the class or namespace does not exist.</summary>
    public static bool IsNotFound(COMException ex) => ex.HResult is WbemNotFound or WbemInvalidClass or WbemInvalidNamespace;
}
