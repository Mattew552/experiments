using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace FanGuardian;

internal sealed record AccessReport(bool PawnIoInstalled, bool Elevated, string[] MsiBackgroundProcesses, string[] OtherFanPrograms)
{
    public bool SensorAccessLikely => PawnIoInstalled && Elevated;
    public string Summary =>
        "PawnIO: " + (PawnIoInstalled ? "установлен" : "не установлен") +
        "; права администратора: " + (Elevated ? "да" : "нет") +
        "; фоновые компоненты MSI: " + (MsiBackgroundProcesses.Length == 0 ? "не обнаружены" : string.Join(", ", MsiBackgroundProcesses)) +
        "; другие программы управления вентиляторами: " + (OtherFanPrograms.Length == 0 ? "не обнаружены" : string.Join(", ", OtherFanPrograms)) +
        (MsiBackgroundProcesses.Length == 0 ? "" : ". Наличие служб MSI не доказывает, что они сейчас управляют вентиляторами");
}
internal static class Diagnostics
{
    private static readonly string[] MsiBackgroundNames = ["MSI.CentralServer", "MSI_Central_Service", "MSI_Companion_Service"];
    private static readonly string[] OtherFanProgramNames = ["FanControl", "ArgusMonitor"];
    internal static (string[] MsiBackground, string[] OtherFanPrograms) ClassifyProcesses(IEnumerable<string> processNames)
    {
        var names = processNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (names.Where(x => MsiBackgroundNames.Contains(x, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x).ToArray(),
                names.Where(x => OtherFanProgramNames.Contains(x, StringComparer.OrdinalIgnoreCase)).OrderBy(x => x).ToArray());
    }
    public static AccessReport Read()
    {
        bool pawn;
        try { using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); pawn = service != null; }
        catch { pawn = false; }
        bool elevated;
        try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { elevated = false; }
        (string[] msi, string[] other) processes;
        try { processes = ClassifyProcesses(Process.GetProcesses().Select(p => p.ProcessName)); }
        catch { processes = ([], []); }
        return new(pawn, elevated, processes.msi, processes.other);
    }
}
