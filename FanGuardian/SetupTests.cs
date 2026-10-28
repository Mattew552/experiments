namespace FanGuardian;

internal static class SetupTests
{
    public static void Run()
    {
        var channel = new ControlChannel("/lpc/test/control/0", "/lpc/test", "Fan", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true);
        var result = new ProbeChannelResult(channel, "/lpc/test/fan/0", "matched", true, true, 63, 980);
        var binding = BindingSetup.Create(result, FanRole.Case, "  Верхняя группа  ");
        Check(binding.MinimumPercent == 63 && binding.IsVerified && !binding.StopVerified && binding.Label == "Верхняя группа",
            "Мастер не сохранил проверенный ненулевой диапазон");
        Denied(() => BindingSetup.Create(result, FanRole.Pump, "Помпа"));
        Denied(() => BindingSetup.Create(result with { AssociationVerified = false }, FanRole.Case, "Case"));
        Denied(() => BindingSetup.Create(result with { RestoreVerified = false }, FanRole.Case, "Case"));
        Denied(() => BindingSetup.Create(result with { BaselineRpm = 0 }, FanRole.Case, "Case"));
        Denied(() => BindingSetup.Create(result with { Control = channel with { Origin = HardwareOrigin.Gpu } }, FanRole.Case, "Case"));
        var settings = new Settings();
        BindingSetup.Apply(settings, [binding]);
        Check(settings.Bindings.Count == 1 && settings.Profiles.All(p => p.Curves[binding.ControlId].All(point => point.Percent >= 63)),
            "Исходная кривая вышла за проверенный диапазон");
        var duplicate = BindingSetup.Create(result with { Control = channel with { Id = "/lpc/test/control/1" } }, FanRole.Cpu, "CPU");
        Denied(() => BindingSetup.Apply(settings, [duplicate]));
        Check(settings.Bindings.Count == 1, "Дублирующийся RPM изменил настройки");
        Check(BindingSetup.Contradictions(settings, [result with { RpmId = "/lpc/test/fan/9" }]).Count == 1 &&
              BindingSetup.Contradictions(settings, [result with { RpmId = "/lpc/test/fan/9", AssociationVerified = false }]).Count == 0,
            "Противоречие подтверждённой привязке не обнаружено");
        Check(CurveEditor.Parse("30:63,50:70,85:100", 63).Count == 3, "Редактор кривых");
        Denied(() => CurveEditor.Parse("30:40,85:100", 63));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Denied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentException) { return; }
        throw new Exception("Небезопасная настройка разрешена");
    }
}
