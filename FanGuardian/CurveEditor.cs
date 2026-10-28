namespace FanGuardian;

internal static class CurveEditor
{
    public static bool Show(IWin32Window owner, Settings settings, int profileIndex)
    {
        var bindings = settings.Bindings.Where(b => b.IsVerified && FanRoles.IsControllable(b.Role)).ToArray();
        if (bindings.Length == 0) throw new InvalidOperationException("Сначала настройте группы вентиляторов");
        var profile = settings.Profiles[profileIndex];
        using var dialog = new Form { Text = "Кривые · " + profile.Name, Width = 740, Height = 560,
            MinimumSize = new(650, 360), StartPosition = FormStartPosition.CenterParent };
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        rows.Controls.Add(new Label { Text = "Температура °C : скорость %. Например: 30:55, 50:65, 70:85, 85:100\nПри нагреве скорость не должна уменьшаться.", AutoSize = true, Margin = new(3, 3, 3, 15) });
        var editors = new List<(FanBinding Binding, TextBox Input)>();
        foreach (var binding in bindings)
        {
            var min = Math.Max(40, binding.MinimumPercent);
            rows.Controls.Add(new Label { Text = $"{binding.Label} · диапазон {min}–100%", AutoSize = true });
            var points = profile.Curves.GetValueOrDefault(binding.ControlId) ?? Curves.Default(binding.Role, profileIndex)
                .Select(p => p with { Percent = Math.Max(p.Percent, min) }).ToList();
            var input = new TextBox { Text = string.Join(", ", points.Select(p => $"{p.Temperature}:{p.Percent}")), Width = 650, Margin = new(3, 3, 3, 16) };
            rows.Controls.Add(input);
            editors.Add((binding, input));
        }
        save.Click += (_, _) =>
        {
            try
            {
                var changes = editors.ToDictionary(x => x.Binding.ControlId,
                    x => Parse(x.Input.Text, Math.Max(40, x.Binding.MinimumPercent)), StringComparer.Ordinal);
                var previous = profile.Curves;
                profile.Curves = new Dictionary<string, List<CurvePoint>>(profile.Curves, StringComparer.Ordinal);
                foreach (var (id, points) in changes) profile.Curves[id] = points;
                try { settings.Save(); }
                catch { profile.Curves = previous; throw; }
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Проверьте кривые", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        dialog.Controls.Add(rows); dialog.Controls.Add(buttons);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    internal static List<CurvePoint> Parse(string text, int minimum)
    {
        var result = new List<CurvePoint>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = token.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !int.TryParse(pair[0], out var temperature) || !int.TryParse(pair[1], out var percent))
                throw new ArgumentException("Используйте формат 30:55, 50:65, 70:85, 85:100");
            result.Add(new(temperature, percent));
        }
        Curves.Validate(result, minimum);
        return result;
    }
}
