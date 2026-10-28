using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace FanGuardian;

internal static class MachineIdentity
{
    public static string Current
    {
        get
        {
            try
            {
                var machineGuid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)?.ToString();
                if (string.IsNullOrWhiteSpace(machineGuid)) return "";
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(machineGuid.Trim().ToUpperInvariant()));
                return Convert.ToHexString(bytes);
            }
            catch { return ""; }
        }
    }
}
