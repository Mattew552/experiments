namespace FanGuardian;

internal enum HardwareOrigin { Unknown, Motherboard, ExternalController, Gpu }
internal enum RestoreStrategy { None, RestoreOriginal, FirmwareAuto, SafeFallback }

internal sealed record ControlChannel(
    string Id,
    string DeviceId,
    string Name,
    HardwareOrigin Origin,
    RestoreStrategy Restore,
    bool CanSetDuty,
    int MinimumPercent = 0,
    int MaximumPercent = 100,
    bool IsIndependent = true)
{
    public bool AllowsFanWrites => CanSetDuty && IsIndependent &&
        (Origin is HardwareOrigin.Motherboard or HardwareOrigin.ExternalController) &&
        (Restore is RestoreStrategy.RestoreOriginal or RestoreStrategy.FirmwareAuto) &&
        MinimumPercent >= 0 && MaximumPercent <= 100 && MinimumPercent <= MaximumPercent &&
        !string.IsNullOrWhiteSpace(DeviceId);
}

internal sealed record HardwareSnapshot(SensorInfo[] Sensors, ControlChannel[] Channels, string? StopTestPhase = null);

internal static class SuperIoRestoreCatalog
{
    // Audited against LibreHardwareMonitor v0.9.6: Lpc/Nct677X.cs and F718XX.cs.
    // These Nct677X branches save
    // both mode and duty; F718XX saves the only register its SetControl changes.
    // W836XX only restores mode bits and ITE may alter a separate vendor
    // controller, so they need a board-specific restore policy before writing.
    private static readonly HashSet<string> OriginalRestoreChips = new(StringComparer.OrdinalIgnoreCase)
    {
        "nct610xd", "nct6771f", "nct6776f", "nct6779d", "nct6791d", "nct6792d", "nct6792da",
        "nct6793d", "nct6795d", "nct6796d", "nct6796dr", "nct6796ds", "nct6797d", "nct6798d",
        "nct6799d", "nct6701d", "nct5585d", "f71878ad", "f71889ad", "f71882"
    };

    public static RestoreStrategy RestoreFor(string deviceId)
    {
        var segments = deviceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && segments[0].Equals("lpc", StringComparison.OrdinalIgnoreCase) &&
               OriginalRestoreChips.Contains(segments[1]) ? RestoreStrategy.RestoreOriginal : RestoreStrategy.None;
    }
}
