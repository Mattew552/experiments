namespace FanGuardian;

internal sealed class StopTestForm : Form
{
    private readonly WorkerHardware worker;
    private readonly Settings settings;
    private readonly Action<string> blockControl;
    private readonly FanBinding[] candidates;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Label progress = new() { Dock = DockStyle.Top, Height = 95, Padding = new(12) };
    private readonly CheckedListBox groups = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly CheckBox confirm = new() { Dock = DockStyle.Bottom, Height = 55,
        Text = "Подтверждаю: выбраны корпусные вентиляторы, а не CPU-кулер и не помпа." };
    private readonly Button start = new() { Text = "Проверить остановку и запуск", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Закрыть", AutoSize = true };
    private bool running;
    public CancellationTokenSource Cancellation => cancellation;

    public StopTestForm(WorkerHardware worker, Settings settings, Action<string> blockControl)
    {
        this.worker = worker; this.settings = settings; this.blockControl = blockControl;
        candidates = settings.Bindings.Where(b => b.IsVerified && FanRoles.IsCase(b.Role) && !b.StopVerified).ToArray();
        Text = "Проверка остановки корпусных вентиляторов";
        Width = 760; Height = 430; StartPosition = FormStartPosition.CenterParent; MinimumSize = new(650, 380);
        progress.Text = "Проверка скорости уже пройдена. Теперь выбранные корпусные группы по очереди получат 0%, затем 100% для повторного запуска и возврат исходного режима. CPU-кулер не получает команд; его RPM и температура контролируются.";
        foreach (var binding in candidates) groups.Items.Add(binding.DisplayLabel, true);
        confirm.CheckedChanged += (_, _) => start.Enabled = confirm.Checked && candidates.Length > 0 && !running;
        start.Click += async (_, _) => await RunAsync();
        close.Click += (_, _) => { if (running) cancellation.Cancel(); else Close(); };
        FormClosing += (_, e) => { if (running) { cancellation.Cancel(); e.Cancel = true; progress.Text = "Проверяем повторный запуск и возвращаем исходный режим…"; } };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(close); buttons.Controls.Add(start);
        Controls.Add(groups); Controls.Add(progress); Controls.Add(confirm); Controls.Add(buttons);
    }

    private async Task RunAsync()
    {
        var selected = groups.CheckedIndices.Cast<int>().Select(i => candidates[i]).ToArray();
        if (!confirm.Checked || selected.Length == 0) return;
        running = true; start.Enabled = false; groups.Enabled = false; confirm.Enabled = false; close.Text = "Отменить";
        var verified = new List<FanBinding>();
        try
        {
            foreach (var binding in selected)
            {
                progress.Text = "Проверка: " + binding.DisplayLabel + ". Ожидаем остановку и повторный запуск.";
                var coordinator = new StopTestCoordinator(worker, worker.BeginStopTest, worker.EndStopTest,
                    () => worker.StopTestPhase, ensureSafe: () =>
                    {
                        if (Diagnostics.Read().OtherFanPrograms.Length > 0)
                            throw new InvalidOperationException("Обнаружена другая программа управления вентиляторами");
                    });
                var result = await coordinator.RunAsync(binding, settings.Bindings, cancellation.Token);
                if (result.Error != null) throw new InvalidOperationException(result.Error);
                if (result.Cancelled) { progress.Text = "Проверка отменена. Остановка не разрешена новым группам."; return; }
                if (!result.Verified)
                {
                    progress.Text = binding.DisplayLabel + ": " + (result.Note ?? "Остановка не подтверждена. Управление скоростью остаётся доступным.");
                    return;
                }
                verified.Add(binding);
            }
            foreach (var binding in verified) binding.StopVerified = true;
            try { settings.Save(); worker.ReloadBindings(); }
            catch
            {
                foreach (var binding in verified) binding.StopVerified = false;
                blockControl("Не удалось подтвердить сохранение результатов остановки; управление заблокировано");
                settings.Save();
                throw;
            }
            progress.Text = "Остановка, повторный запуск и возврат режима подтверждены.";
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex)
        {
            blockControl("Проверка остановки не завершена: " + ex.Message);
            progress.Text = "Проверка прервана: " + ex.Message + ". Проверьте охлаждение и журнал.";
            AppLog.Write("Проверка остановки: " + ex);
        }
        finally
        {
            running = false; close.Text = "Закрыть";
            if (DialogResult == DialogResult.OK) Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) cancellation.Dispose();
        base.Dispose(disposing);
    }
}
