namespace FanGuardian;

internal sealed class SystemSetupForm : Form
{
    private readonly IFanHardware hardware;
    private readonly WorkerHardware worker;
    private readonly Settings settings;
    private readonly Action<string> blockControl;
    private readonly HashSet<string> revoked = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Label progress = new() { Dock = DockStyle.Top, Height = 80, Padding = new(12) };
    private readonly FlowLayoutPanel results = new() { Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
    private readonly Button save = new() { Text = "Сохранить выбранные группы", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Остановить проверку", AutoSize = true };
    private readonly List<(ProbeChannelResult Result, ComboBox Role, TextBox Label, HeaderHint Hint)> choices = new();
    private readonly List<(FanBinding Binding, ComboBox Role, TextBox Label, HeaderHint Hint)> existingChoices = new();
    private bool running = true;

    public CancellationTokenSource Cancellation => cancellation;

    public SystemSetupForm(IFanHardware hardware, WorkerHardware worker, Settings settings, Action<string> blockControl)
    {
        this.hardware = hardware; this.worker = worker; this.settings = settings;
        this.blockControl = blockControl;
        Text = "Подключить новую систему"; Width = 860; Height = 620; MinimumSize = new(720, 400);
        StartPosition = FormStartPosition.CenterParent;
        progress.Text = "Собираем исходные показания. Проверка повышает скорость по одному каналу, измеряет устойчивый отклик и возврат. Это может занять несколько минут.";
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close); buttons.Controls.Add(save);
        Controls.Add(results); Controls.Add(progress); Controls.Add(buttons);
        close.Click += (_, _) => { if (running) cancellation.Cancel(); else Close(); };
        save.Click += (_, _) => SaveSelection();
        FormClosing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancellation.Cancel();
            progress.Text = "Завершаем пробу и проверяем возврат исходного режима…";
        };
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var coordinator = new ProbeCoordinator(hardware, worker.ProbeStart, worker.ProbeEnd,
                ensureSafe: () =>
                {
                    if (Diagnostics.Read().OtherFanPrograms.Length > 0)
                        throw new InvalidOperationException("Обнаружена другая программа управления вентиляторами");
                });
            var report = await coordinator.RunAsync(new Progress<ProbeProgress>(p => progress.Text = p.Message), cancellation.Token);
            if (report.Error == null && !report.Cancelled) RevokeContradictions(report.Results);
            foreach (var result in report.Results) AddResult(result);
            if (report.Error != null)
            {
                blockControl("Управление заблокировано после незавершённой проверки: " + report.Error);
                progress.Text = "Проверка прервана: " + report.Error + ". Управление в этой сессии заблокировано; проверьте охлаждение и журнал.";
            }
            else if (report.Cancelled) progress.Text = "Проверка остановлена. Завершённые результаты показаны ниже; управление не включено.";
            else progress.Text = choices.Count + existingChoices.Count == 0
                ? "Однозначных новых связей с проверенным диапазоном не найдено. Доступные датчики продолжат отображаться."
                : "Роли подставлены по именам разъёмов или профилю платы. Проверьте подключение: CPU — кулер процессора, корпус — корпусные вентиляторы. Помпы оставьте без управления. Остановка новых групп проверяется отдельно.";
            save.Enabled = report.Error == null && !report.Cancelled && choices.Count + existingChoices.Count > 0;
        }
        catch (Exception ex)
        {
            blockControl("Проверка системы завершилась ошибкой; проверьте охлаждение и журнал");
            progress.Text = "Проверка прервана: " + ex.Message;
            AppLog.Write("Мастер подключения: " + ex);
        }
        finally { running = false; close.Text = "Закрыть"; }
    }

    private void AddResult(ProbeChannelResult result)
    {
        var hint = HeaderRoles.Resolve(result.Control);
        results.Controls.Add(new Label { Text = $"{hint.Name}: {result.Description}", AutoSize = true, MaximumSize = new(780, 0), Margin = new(3, 12, 3, 4) });
        var existing = settings.Bindings.FirstOrDefault(b => b.ControlId == result.Control.Id && b.IsVerified);
        if (revoked.Contains(result.Control.Id))
            results.Controls.Add(new Label { Text = "Обнаружено другое соответствие RPM. Прежняя привязка и разрешение остановки отключены.", AutoSize = true, MaximumSize = new(780, 0) });
        if (existing == null && (!result.AssociationVerified || !result.RestoreVerified || result.BaselineRpm <= 0 || result.BaselinePercent <= 0)) return;
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var role = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        role.Items.AddRange(["Не управлять / помпа", "Корпусной вентилятор / группа", "Кулер CPU"]);
        var suggestedRole = existing is { RoleSource.Length: > 0 } ? existing.Role : hint.Role;
        if (suggestedRole == FanRole.Unknown && existing != null) suggestedRole = existing.Role;
        role.SelectedIndex = suggestedRole == FanRole.Cpu ? 2 : FanRoles.IsCase(suggestedRole) ? 1 : 0;
        var label = new TextBox { Width = 310, Text = existing?.DisplayLabel ?? hint.Name, MaxLength = 80 };
        row.Controls.Add(role); row.Controls.Add(label);
        results.Controls.Add(row);
        results.Controls.Add(new Label { Text = $"Источник имени: {hint.Description}. Диапазон {Math.Max(40, existing?.MinimumPercent ?? result.BaselinePercent)}–100%." +
            (existing?.StopVerified == true ? " Остановка была проверена ранее." : " Остановка ещё не проверена."), AutoSize = true });
        if (existing != null) existingChoices.Add((existing, role, label, hint));
        else choices.Add((result, role, label, hint));
    }

    private void SaveSelection()
    {
        try
        {
            var selected = choices.Where(c => c.Role.SelectedIndex > 0)
                .Select(c => BindingSetup.Create(c.Result, c.Role.SelectedIndex == 2 ? FanRole.Cpu : FanRole.Case, c.Label.Text, c.Hint))
                .Concat(existingChoices.Select(c => BindingSetup.UpdateRole(c.Binding,
                    c.Role.SelectedIndex == 2 ? FanRole.Cpu : c.Role.SelectedIndex == 1
                        ? (FanRoles.IsCase(c.Binding.Role) ? c.Binding.Role : FanRole.Case) : FanRole.Unknown,
                    c.Label.Text, c.Hint))).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("Выберите хотя бы одну известную группу или закройте окно");
            var originalBindings = settings.Bindings.ToList();
            var originalCurves = settings.Profiles.Select(p => new Dictionary<string, List<CurvePoint>>(p.Curves, StringComparer.Ordinal)).ToArray();
            try
            {
                BindingSetup.Apply(settings, selected);
                settings.Save();
                worker.ReloadBindings();
            }
            catch
            {
                settings.Bindings = originalBindings;
                for (var i = 0; i < originalCurves.Length; i++) settings.Profiles[i].Curves = originalCurves[i];
                blockControl("Не удалось подтвердить сохранение привязок в стороже; управление заблокировано");
                save.Enabled = false;
                progress.Text = "Настройки не применены. Управление в этой сессии заблокировано; проверьте журнал.";
                settings.Save();
                throw;
            }
            AppLog.Write("Настроено новых групп: " + selected.Length);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Настройка групп", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void RevokeContradictions(IReadOnlyList<ProbeChannelResult> report)
    {
        var ids = BindingSetup.Contradictions(settings, report);
        if (ids.Count == 0) return;
        settings.Bindings = settings.Bindings.Where(b => !ids.Contains(b.ControlId, StringComparer.Ordinal)).ToList();
        foreach (var id in ids) revoked.Add(id);
        settings.Save();
        worker.ReloadBindings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation.Dispose();
        base.Dispose(disposing);
    }
}
