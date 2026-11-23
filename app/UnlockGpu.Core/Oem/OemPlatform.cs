using System.Runtime.InteropServices;

namespace UnlockGpu.Core.Oem;

/// <summary>Reads SMBIOS identity strings (read-only).</summary>
public interface ISystemIdentitySource
{
    /// <summary>Returns the identity, or <see cref="SystemIdentity.Unknown"/> fields when unreadable.</summary>
    SystemIdentity Read();
}

/// <summary>Reads Win32_ComputerSystemProduct and Win32_BaseBoard through WMI root\cimv2 (no elevation needed).</summary>
public sealed class WmiSystemIdentitySource : ISystemIdentitySource
{
    /// <inheritdoc />
    public SystemIdentity Read()
    {
        object? services = null;
        try
        {
            services = WbemScripting.Connect(@"root\cimv2");
            var (sysVendor, sysProduct) = First(services, "SELECT Vendor, Name FROM Win32_ComputerSystemProduct", "Vendor", "Name");
            var (boardVendor, boardProduct) = First(services, "SELECT Manufacturer, Product FROM Win32_BaseBoard", "Manufacturer", "Product");
            return new SystemIdentity(sysVendor, sysProduct, boardVendor, boardProduct);
        }
        catch (COMException)
        {
            return SystemIdentity.Unknown;
        }
        finally
        {
            WbemScripting.Release(services);
        }
    }

    static (string, string) First(object services, string query, string p1, string p2)
    {
        dynamic set = ((dynamic)services).ExecQuery(query);
        List<object> items = [];
        try
        {
            items = WbemScripting.Items(set);
            return items.Count == 0 ? ("", "")
                : (WbemScripting.PropertyString(items[0], p1), WbemScripting.PropertyString(items[0], p2));
        }
        finally
        {
            foreach (var item in items) WbemScripting.Release(item);
            WbemScripting.Release(set);
        }
    }
}

/// <summary>Known Uniwill (Tongfang/Uniwill ODM) platforms. Static, immutable lists.</summary>
public static class UniwillBoards
{
    /// <summary>Vendor strings seen on Uniwill-built machines (system or baseboard manufacturer).</summary>
    public static readonly IReadOnlyList<string> Vendors =
        ["AiStone", "Uniwill", "TUXEDO", "SchenkerTechnologiesGmbH", "Schenker", "XMG", "PCSpecialist", "Eluktronics", "Tongfang", "Mechrevo"];

    /// <summary>Boards whose register map was checked on real hardware for this build.</summary>
    public static readonly IReadOnlyList<string> VerifiedBoards = ["X6AF559Y"];

    /// <summary>True when the identity looks like a Uniwill design (vendor match; the WMI class is checked separately).</summary>
    public static bool LooksLikeUniwill(SystemIdentity id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Vendors.Any(v => Contains(id.BoardVendor, v) || Contains(id.SystemVendor, v));
    }

    /// <summary>True when the board product is in <see cref="VerifiedBoards"/>.</summary>
    public static bool IsVerified(SystemIdentity id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return VerifiedBoards.Any(b => string.Equals(b, id.BoardProduct, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(b, id.SystemProduct, StringComparison.OrdinalIgnoreCase));
    }

    static bool Contains(string text, string token) => text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
