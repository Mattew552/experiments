using System.Security.Cryptography;
using System.Text;
using System.Management;
using Microsoft.Win32;

namespace FanGuardian;

internal static class MachineIdentity
{
    private sealed record BoardIdentity(string Manufacturer, string Product, string Version, string Serial, string Bios);
    private static readonly Lazy<BoardIdentity?> board = new(ReadBoard);
    public static string BoardManufacturer => board.Value?.Manufacturer ?? "";
    public static string BoardProduct => board.Value?.Product ?? "";
    public static string HardwareFingerprint => board.Value is { } value ? Hash(string.Join("|", value.Manufacturer, value.Product, value.Version, value.Serial, value.Bios)) : "";
    public static bool IsVerifiedLegacyBoard => board.Value is { } value &&
        value.Manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) &&
        value.Product.Equals("B450 TOMAHAWK MAX (MS-7C02)", StringComparison.OrdinalIgnoreCase);
    public static string Current
    {
        get
        {
            try
            {
                var machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)?.ToString();
                if (string.IsNullOrWhiteSpace(machineGuid)) return "";
                return Hash(machineGuid);
            }
            catch { return ""; }
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())));

    private static BoardIdentity? ReadBoard()
    {
        try
        {
            const string biosKey = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
            var manufacturer = Registry.GetValue(biosKey, "BaseBoardManufacturer", "")?.ToString()?.Trim() ?? "";
            var product = Registry.GetValue(biosKey, "BaseBoardProduct", "")?.ToString()?.Trim() ?? "";
            var version = Registry.GetValue(biosKey, "BaseBoardVersion", "")?.ToString()?.Trim() ?? "";
            var serial = "";
            // Raw identity stays inside this process; settings contain only its hash.
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard");
                searcher.Options.Timeout = TimeSpan.FromSeconds(2);
                using var boards = searcher.Get();
                foreach (ManagementObject item in boards)
                {
                    using (item)
                    {
                        manufacturer = item["Manufacturer"]?.ToString()?.Trim() ?? manufacturer;
                        product = item["Product"]?.ToString()?.Trim() ?? product;
                        version = item["Version"]?.ToString()?.Trim() ?? version;
                        serial = item["SerialNumber"]?.ToString()?.Trim() ?? "";
                    }
                    break;
                }
            }
            catch { /* Registry metadata still detects a changed model/firmware. */ }
            var bios = Registry.GetValue(biosKey, "BIOSVersion", "") switch
            {
                string[] values => string.Join(";", values),
                string value => value,
                _ => ""
            };
            return string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(product)
                ? null : new BoardIdentity(manufacturer, product, version, serial, bios);
        }
        catch { return null; }
    }
}
