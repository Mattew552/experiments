using System.Text.Json;

namespace FanGuardian;

internal enum FanRole { Cpu, FrontTop, FrontPair }
internal static class FanRoles
{
    public static bool IsFront(FanRole role) => role is FanRole.FrontTop or FanRole.FrontPair;
    public static string Label(FanRole role) => role switch
    {
        FanRole.Cpu => "CPU-кулер (ожидается CPU_FAN1)",
        FanRole.FrontTop => "Верхний передний (SYS_FAN4)",
        FanRole.FrontPair => "Два передних вместе (SYS_FAN3)",
        _ => "Неизвестный канал"
    };
}
internal enum FanMode { Bios, CaseOff, Manual }
internal sealed record CurvePoint(int Temperature, int Percent);
internal sealed class FanBinding
{
    public string ControlId { get; set; } = "";
    public string RpmId { get; set; } = "";
    public string Label { get; set; } = "";
    public FanRole Role { get; set; }
    public int MinimumPercent { get; set; } = 30;
    public bool MappingVerified { get; set; }
    public bool ReturnVerified { get; set; }
    public bool SpeedVerified { get; set; }
    public bool StopVerified { get; set; }
}
internal sealed class FanProfile
{
    public string Name { get; set; } = "Профиль";
    public Dictionary<string, List<CurvePoint>> Curves { get; set; } = new();
}
internal sealed class Settings
{
    public int SchemaVersion { get; set; }
    public string MachineFingerprint { get; set; } = "";
    public List<FanBinding> Bindings { get; set; } = new();
    public List<FanProfile> Profiles { get; set; } = new()
    {
        new() { Name = "Тихий" }, new() { Name = "Сбалансированный" }, new() { Name = "Производительный" }
    };
    public int SelectedProfile { get; set; } = 1;
    public bool BelongsTo(string fingerprint) => fingerprint.Length > 0 && MachineFingerprint == fingerprint;
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanGuardian");
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public static Settings Load()
    {
        try
        {
            var result = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            if (result.Profiles.Count != 3) throw new InvalidDataException("Требуется ровно три профиля");
            if (result.SchemaVersion < 3)
            {
                result.Bindings.Clear();
                result.SchemaVersion = 3;
                AppLog.Write("Старые привязки сброшены после уточнения подключения и этапа сопоставления BIOS");
                result.Save();
            }
            var machine = MachineIdentity.Current;
            if (!result.BelongsTo(machine))
            {
                if (result.Bindings.Count > 0) AppLog.Write("Привязки отключены: настройки принадлежат другому ПК или идентификатор недоступен");
                result.Bindings.Clear();
            }
            var renamed = false;
            foreach (var binding in result.Bindings.Where(b => Enum.IsDefined(b.Role)))
            {
                var label = FanRoles.Label(binding.Role);
                if (binding.Label == label) continue;
                binding.Label = label;
                renamed = true;
            }
            if (renamed) result.Save();
            return result;
        }
        catch (FileNotFoundException) { return new() { SchemaVersion = 3, MachineFingerprint = MachineIdentity.Current }; }
        catch (Exception ex) { AppLog.Write("Настройки повреждены: " + ex.Message); return new(); }
    }
    public void Save()
    {
        SchemaVersion = 3;
        MachineFingerprint = MachineIdentity.Current;
        Directory.CreateDirectory(DirectoryPath);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }
}
internal static class Curves
{
    public static List<CurvePoint> Default(FanRole role, int profileIndex = 1) => (role, profileIndex) switch
    {
        (FanRole.Cpu, 0) => [new(30, 40), new(50, 55), new(70, 80), new(85, 100)],
        (FanRole.Cpu, 1) => [new(30, 50), new(50, 65), new(70, 85), new(85, 100)],
        (FanRole.Cpu, 2) => [new(30, 65), new(50, 75), new(70, 90), new(85, 100)],
        (_, 0) => [new(30, 40), new(50, 50), new(70, 75), new(85, 100)],
        (_, 1) => [new(30, 55), new(50, 65), new(70, 85), new(85, 100)],
        (_, 2) => [new(30, 70), new(50, 80), new(70, 90), new(85, 100)],
        _ => throw new ArgumentOutOfRangeException(nameof(profileIndex))
    };
    public static void Validate(IReadOnlyList<CurvePoint> points, int min)
    {
        if (points.Count < 2 || points.Count > 12) throw new ArgumentException("Нужно от 2 до 12 точек");
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Temperature < 20 || points[i].Temperature > 95 || points[i].Percent < min || points[i].Percent > 100)
                throw new ArgumentException($"Температура 20–95 °C, скорость {min}–100 %");
            if (i > 0 && (points[i].Temperature <= points[i - 1].Temperature || points[i].Percent < points[i - 1].Percent))
                throw new ArgumentException("Температуры должны расти, скорость не должна снижаться");
        }
    }
    public static int Evaluate(IReadOnlyList<CurvePoint> points, float temperature)
    {
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (int i = 1; i < points.Count; i++)
        {
            if (temperature <= points[i].Temperature)
            {
                var a = points[i - 1]; var b = points[i];
                return (int)Math.Round(a.Percent + (temperature - a.Temperature) * (b.Percent - a.Percent) / (b.Temperature - a.Temperature));
            }
        }
        return points[^1].Percent;
    }
}
