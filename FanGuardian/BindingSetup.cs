namespace FanGuardian;

internal static class BindingSetup
{
    public static FanBinding Create(ProbeChannelResult result, FanRole role, string label)
    {
        if (!result.AssociationVerified || !result.RestoreVerified || string.IsNullOrEmpty(result.RpmId) ||
            !float.IsFinite(result.BaselineRpm) || result.BaselineRpm <= 0 || result.BaselinePercent is < 1 or >= 95 ||
            !result.Control.AllowsFanWrites || result.Control.Origin != HardwareOrigin.Motherboard ||
            result.Control.Restore != RestoreStrategy.RestoreOriginal ||
            role is not (FanRole.Cpu or FanRole.Case))
            throw new InvalidOperationException("Для этой группы ещё не подтверждены связь, возврат и ненулевой диапазон");
        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length > 80)
            throw new ArgumentException("Название группы: от 1 до 80 символов");
        return new FanBinding
        {
            ControlId = result.Control.Id,
            RpmId = result.RpmId,
            Label = label.Trim(),
            Role = role,
            MinimumPercent = Math.Max(result.Control.MinimumPercent, Math.Max(40, result.BaselinePercent)),
            MinimumRunningRpm = Math.Max(1, (int)(result.BaselineRpm * 0.5f)),
            MappingVerified = true,
            ReturnVerified = true,
            SpeedVerified = true,
            StopVerified = false
        };
    }

    public static void Apply(Settings settings, IReadOnlyList<FanBinding> additions)
    {
        if (additions.Select(b => b.ControlId).Distinct(StringComparer.Ordinal).Count() != additions.Count ||
            additions.Select(b => b.RpmId).Distinct(StringComparer.Ordinal).Count() != additions.Count)
            throw new InvalidOperationException("Дублирующееся сопоставление; нужна повторная проверка");
        var remaining = settings.Bindings.Where(b => !additions.Any(a => a.ControlId == b.ControlId)).ToList();
        if (remaining.Any(b => additions.Any(a => a.RpmId == b.RpmId)))
            throw new InvalidOperationException("Этот тахометр уже связан с другой группой");
        foreach (var binding in additions)
        {
            for (var i = 0; i < settings.Profiles.Count; i++)
                settings.Profiles[i].Curves[binding.ControlId] = Curves.Default(binding.Role, i)
                    .Select(p => p with { Percent = Math.Max(p.Percent, binding.MinimumPercent) }).ToList();
        }
        settings.Bindings = remaining.Concat(additions).ToList();
    }

    public static IReadOnlyList<string> Contradictions(Settings settings, IReadOnlyList<ProbeChannelResult> results) => results
        .Where(r => r.AssociationVerified && r.RestoreVerified && r.RpmId != null &&
                    settings.Bindings.Any(b => b.IsVerified && b.ControlId == r.Control.Id && b.RpmId != r.RpmId))
        .Select(r => r.Control.Id).Distinct(StringComparer.Ordinal).ToArray();
}
