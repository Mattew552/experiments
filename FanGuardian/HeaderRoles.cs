using System.Text.RegularExpressions;

namespace FanGuardian;

internal sealed record HeaderHint(string Name, FanRole Role, string Source)
{
    public string Description => Source switch
    {
        "BoardProfile" => "профиль этой модели платы",
        "BackendName" => "название разъёма из библиотеки",
        "Excluded" => "выход исключён из управления",
        _ => "назначение не определено"
    };
}

internal static class HeaderRoles
{
    public static HeaderHint Resolve(ControlChannel channel)
    {
        var direct = Resolve(channel, "", "");
        if (direct.Role != FanRole.Unknown || direct.Source != "Unknown" || channel.DeviceId != "/lpc/nct6797d/0") return direct;
        return Resolve(channel, MachineIdentity.BoardManufacturer, MachineIdentity.BoardProduct);
    }

    public static HeaderHint Resolve(ControlChannel channel, string manufacturer, string product)
    {
        if (channel.Origin != HardwareOrigin.Motherboard) return new(channel.Name, FanRole.Unknown, "Unknown");
        var name = Regex.Replace(channel.Name.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (name.Contains("GPU", StringComparison.Ordinal) || name.Contains("VGA", StringComparison.Ordinal))
            return new(channel.Name, FanRole.Unknown, "Excluded");
        if (name.Contains("PUMP", StringComparison.Ordinal) || name.StartsWith("AIO", StringComparison.Ordinal))
            return new(channel.Name, FanRole.Pump, "BackendName");
        if (name.StartsWith("CPUOPT", StringComparison.Ordinal)) return new(channel.Name, FanRole.Unknown, "BackendName");
        // This exact board/header mapping was independently exercised on the
        // reference PC. It is not a numbering rule for other NCT6797D boards.
        if (manufacturer.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) &&
            product.Equals("B450 TOMAHAWK MAX (MS-7C02)", StringComparison.OrdinalIgnoreCase) &&
            channel.DeviceId.Equals("/lpc/nct6797d/0", StringComparison.OrdinalIgnoreCase))
        {
            var hint = channel.Id switch
            {
                "/lpc/nct6797d/0/control/1" => new HeaderHint("CPU_FAN1", FanRole.Cpu, "BoardProfile"),
                "/lpc/nct6797d/0/control/4" => new HeaderHint("SYS_FAN3", FanRole.Case, "BoardProfile"),
                "/lpc/nct6797d/0/control/5" => new HeaderHint("SYS_FAN4", FanRole.Case, "BoardProfile"),
                _ => null
            };
            if (hint != null) return hint;
        }
        if (Regex.IsMatch(name, "^CPUFAN[0-9]*$")) return new(channel.Name, FanRole.Cpu, "BackendName");
        if (Regex.IsMatch(name, "^(SYSFAN|SYSTEMFAN|CHAFAN|CHASSISFAN|CASEFAN)[0-9]*$"))
            return new(channel.Name, FanRole.Case, "BackendName");
        return new(channel.Name, FanRole.Unknown, "Unknown");
    }
}
