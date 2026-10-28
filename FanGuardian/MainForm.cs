using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace FanGuardian;

internal sealed class MainForm : Form
{
    private readonly Settings settings = Settings.Load();
    private readonly IFanHardware hardware;
    private readonly Engine engine;
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Label headline = new() { AutoSize = true };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label footer = new() { AutoSize = true, MaximumSize = new(740, 0), ForeColor = Color.FromArgb(165, 174, 190) };
    private readonly Label diagnostics = new() { AutoSize = true, MaximumSize = new(740, 0) };
    private readonly Label discovery = new() { AutoSize = true, MaximumSize = new(740, 0) };
    private readonly ListBox channels = new() { Width = 740, Height = 190, HorizontalScrollbar = true };
    private readonly RpmGraph pairGraph = new("Два передних вместе · SYS_FAN3", Color.FromArgb(75, 194, 255));
    private readonly RpmGraph topGraph = new("Верхний передний · SYS_FAN4", Color.FromArgb(134, 224, 171));
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly string heartbeat = Path.Combine(Settings.DirectoryPath, "heartbeat");
    private bool exitRequested;
    private bool cleanedUp;

    public MainForm()
    {
        Text = "FanGuardian — управление вентиляторами";
        Width = 800; Height = 660; MinimumSize = new(780, 620);
        hardware = new WorkerHardware(heartbeat); engine = new Engine(hardware, settings);
        BackColor = Color.FromArgb(18, 21, 28);
        ForeColor = Color.White;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(18), BackColor = BackColor };
        Controls.Add(root);
        root.Controls.Add(new Label { Text = "FanGuardian", Font = new Font(Font.FontFamily, 20, FontStyle.Bold), AutoSize = true, ForeColor = Color.White });
        headline.Font = new Font(Font.FontFamily, 12, FontStyle.Regular);
        headline.ForeColor = Color.White;
        status.ForeColor = Color.FromArgb(165, 174, 190);
        status.MaximumSize = new(740, 0);
        root.Controls.Add(headline);
        root.Controls.Add(status);
        var modes = new FlowLayoutPanel { AutoSize = true };
        modes.Controls.Add(Button("BIOS / Автоматически", () => Change(FanMode.Bios)));
        modes.Controls.Add(Button("Передние выключены", () => Change(FanMode.CaseOff)));
        modes.Controls.Add(Button("Ручной режим", () => Change(FanMode.Manual)));
        root.Controls.Add(modes);
        var manualSettings = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new(3, 0, 3, 8) };
        manualSettings.Controls.Add(new Label { Text = "Профиль ручного режима:", AutoSize = true, Margin = new(3, 7, 8, 3), ForeColor = Color.FromArgb(165, 174, 190) });
        manualSettings.Controls.Add(profiles);
        var editCurves = new LinkLabel { Text = "Настроить кривые", AutoSize = true, Margin = new(16, 7, 3, 3), LinkColor = Color.FromArgb(75, 194, 255), ActiveLinkColor = Color.White };
        editCurves.LinkClicked += (_, _) => RunAdvanced(EditCurves);
        manualSettings.Controls.Add(editCurves);
        root.Controls.Add(manualSettings);
        root.Controls.Add(pairGraph);
        root.Controls.Add(topGraph);
        root.Controls.Add(footer);
        var menu = new MenuStrip();
        var service = new ToolStripMenuItem("Сервис");
        service.DropDownItems.Add("Датчики и диагностика", null, (_, _) => ShowDiagnostics());
        var profileMenu = new ToolStripMenuItem("Профиль");
        profileMenu.DropDownItems.Add("Переименовать", null, (_, _) => RunAdvanced(RenameProfile));
        profileMenu.DropDownItems.Add("Настроить кривые", null, (_, _) => RunAdvanced(EditCurves));
        service.DropDownItems.Add(profileMenu);
        service.DropDownItems.Add("Журнал", null, (_, _) => RunAdvanced(() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppLog.PathName}\"") { UseShellExecute = true })));
        service.DropDownItems.Add("Автозапуск", null, (_, _) => RunAdvanced(ToggleAutostart));
        service.DropDownItems.Add("Выход", null, (_, _) => Exit());
        menu.Items.Add(service);
        menu.BackColor = Color.FromArgb(30, 34, 44);
        menu.ForeColor = Color.White;
        Controls.Add(menu);
        MainMenuStrip = menu;
        tray.Icon = SystemIcons.Application; tray.Text = "FanGuardian: охлаждение BIOS"; tray.Visible = true;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        trayMenu.Items.Add("BIOS / Автоматически", null, (_, _) => Change(FanMode.Bios));
        trayMenu.Items.Add("Выход (вернуть BIOS)", null, (_, _) => Exit());
        tray.ContextMenuStrip = trayMenu;
        FormClosing += (_, e) =>
        {
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(2500, "FanGuardian", "Приложение продолжает работать в трее и управлять вентиляторами.", ToolTipIcon.Info);
            }
            else Cleanup();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        profiles.Items.AddRange(settings.Profiles.Select(p => p.Name).ToArray());
        profiles.SelectedIndex = Math.Clamp(settings.SelectedProfile, 0, 2);
        profiles.SelectedIndexChanged += (_, _) =>
        {
            if (profiles.SelectedIndex < 0) return;
            settings.SelectedProfile = profiles.SelectedIndex;
            settings.Save();
            AppLog.Write("Профиль: " + profiles.Text);
            if (engine.Mode == FanMode.Manual) engine.Tick();
            Render();
        };
        Directory.CreateDirectory(Settings.DirectoryPath);
        engine.Tick(); Render();
        timer.Tick += (_, _) => { File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToString("O")); engine.Tick(); Render(); };
        timer.Start();
    }
    private static Button Button(string title, Action action)
    {
        var b = new Button { Text = title, AutoSize = true, Margin = new(3, 6, 3, 6) };
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { AppLog.Write("Ошибка UI: " + ex.Message); MessageBox.Show(ex.Message, "FanGuardian", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        return b;
    }
    private void Render()
    {
        var cpuRpm = Telemetry.RpmForRole(engine.Sensors, settings.Bindings, FanRole.Cpu);
        var waitingForFront = engine.Mode == FanMode.Bios && settings.Bindings.Where(b => FanRoles.IsFront(b.Role) && b.StopVerified)
            .Any(b => engine.Sensors.FirstOrDefault(s => s.Id == b.RpmId)?.Value is null or < 250);
        headline.Text = "CPU  " + (engine.CpuTemperature?.ToString("F1") ?? "—") + " °C     Кулер  " + (cpuRpm?.ToString("F0") ?? "—") + " RPM";
        status.Text = engine.Mode == FanMode.Bios
            ? (waitingForFront ? "Возврат режима платы запрошен; передние вентиляторы раскручиваются" :
                engine.Status.Contains("ожидание", StringComparison.OrdinalIgnoreCase) ? engine.Status : "Приложение не задаёт скорость. Фактический режим платы автоматически не определяется.")
            : engine.Status + "  ·  Профиль: " + settings.Profiles[settings.SelectedProfile].Name;
        var access = Diagnostics.Read();
        diagnostics.Text = "Диагностика: " + access.Summary + (access.SensorAccessLikely ? "" : ". При 0 °C и отсутствии RPM управление заблокировано.");
        var controlCount = engine.Sensors.Count(s => s.Type == "Control" && s.Id.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase));
        var rpmCount = engine.Sensors.Count(s => s.Type == "Fan" && s.Id.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase) && s.Value is > 0);
        discovery.Text = $"Найдено записей Control: {controlCount}; датчиков с ненулевыми RPM: {rpmCount}. Номера Fan # не определяют физическую роль. Ожидаются только два передних управляемых канала и CPU-кулер; остальные записи остаются без привязки.";
        var hasPair = settings.Bindings.Any(b => b.Role == FanRole.FrontPair && b.MappingVerified && b.ReturnVerified && b.SpeedVerified);
        var hasTop = settings.Bindings.Any(b => b.Role == FanRole.FrontTop && b.MappingVerified && b.ReturnVerified && b.SpeedVerified);
        pairGraph.Visible = hasPair;
        topGraph.Visible = hasTop;
        pairGraph.Add(Telemetry.RpmForRole(engine.Sensors, settings.Bindings, FanRole.FrontPair));
        topGraph.Add(Telemetry.RpmForRole(engine.Sensors, settings.Bindings, FanRole.FrontTop));
        footer.Text = hasPair && hasTop
            ? "Графики обновляются автоматически. Соответствие передних каналов подтверждено на этом ПК. Задний вентилятор с питанием Molex не управляется программой."
            : "Датчики читаются автоматически. Соответствие каналов этому ПК не подтверждено, управление выключено.";
        tray.Text = ("FanGuardian: " + engine.Mode + "; CPU " + (engine.CpuTemperature?.ToString("F0") ?? "?")) switch { var s when s.Length > 63 => s[..63], var s => s };
        var selected = channels.SelectedIndex;
        channels.Items.Clear();
        foreach (var s in engine.Sensors.Where(s => s.Type is "Fan" or "Control"))
        {
            var b = settings.Bindings.FirstOrDefault(b => b.ControlId == s.Id || b.RpmId == s.Id);
            channels.Items.Add($"{s.Hardware} / {s.Name}: {(s.Value.HasValue ? s.Value.Value.ToString("F0") : "?")} {(s.Type == "Fan" ? "RPM" : "%")}   [{s.Id}] {(b == null ? "не привязан" : b.Label + (b.SpeedVerified ? " ✓" : b.MappingVerified ? " (нужна проверка возврата)" : " (не сопоставлен)"))}");
        }
        if (selected >= 0 && selected < channels.Items.Count) channels.SelectedIndex = selected;
    }
    private void Change(FanMode mode)
    {
        if (mode != FanMode.Bios && !settings.Bindings.Any(b => b.MappingVerified && b.ReturnVerified && b.SpeedVerified))
        {
            MessageBox.Show("Показания вентиляторов уже доступны. Для управления нужна однократная проверка соответствия разъёмов и каналов на этом ПК. Пока команды вентиляторам не отправляются.", "Аппаратная проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        engine.SelectMode(mode); Render();
    }
    private void RunAdvanced(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Write("Ошибка UI: " + ex.Message); MessageBox.Show(ex.Message, "FanGuardian", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void ShowDiagnostics()
    {
        using var dialog = new Form { Text = "Датчики и диагностика", Width = 900, Height = 520, StartPosition = FormStartPosition.CenterParent };
        var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Text = diagnostics.Text + Environment.NewLine + discovery.Text + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, channels.Items.Cast<string>()) };
        dialog.Controls.Add(details);
        dialog.ShowDialog(this);
    }
    private static string? Prompt(string title, string value)
    {
        using var dialog = new Form { Text = title, Width = 550, Height = 150, StartPosition = FormStartPosition.CenterParent };
        var input = new TextBox { Text = value, Left = 12, Top = 12, Width = 510 };
        var ok = new Button { Text = "Сохранить", DialogResult = DialogResult.OK, Left = 430, Top = 45 };
        dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.AcceptButton = ok;
        return dialog.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }
    private void RenameProfile()
    {
        var name = Prompt("Название профиля", profiles.Text);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40) return;
        settings.Profiles[profiles.SelectedIndex].Name = name;
        profiles.Items[profiles.SelectedIndex] = name;
        settings.Save(); AppLog.Write("Профиль переименован: " + name);
    }
    private void EditCurves()
    {
        var bindings = new[] { FanRole.Cpu, FanRole.FrontPair, FanRole.FrontTop }
            .Select(role => settings.Bindings.FirstOrDefault(b => b.Role == role && b.MappingVerified && b.ReturnVerified && b.SpeedVerified))
            .OfType<FanBinding>().ToArray();
        if (bindings.Length == 0) throw new InvalidOperationException("Сначала нужны подтверждённые каналы");
        var profileIndex = profiles.SelectedIndex;
        var profile = settings.Profiles[profileIndex];
        using var dialog = new Form { Text = "Кривые · " + profile.Name, Width = 700, Height = 345, MinimumSize = new(700, 345), StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(25, 29, 38), ForeColor = Color.White, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false };
        dialog.Controls.Add(new Label { Text = "Температура °C : скорость %. Например: 30:40, 50:55, 70:80, 85:100\nСкорость 40–100%; при нагреве она не должна уменьшаться.", Left = 18, Top = 15, Width = 650, Height = 50 });
        var editors = new List<(FanBinding Binding, TextBox Input)>();
        for (var i = 0; i < bindings.Length; i++)
        {
            var b = bindings[i];
            var y = 82 + i * 51;
            var name = b.Role switch { FanRole.Cpu => "Кулер CPU", FanRole.FrontPair => "Два передних вместе", _ => "Верхний передний" };
            dialog.Controls.Add(new Label { Text = name, Left = 18, Top = y + 4, Width = 190, Height = 26 });
            var points = profile.Curves.GetValueOrDefault(b.ControlId) ?? Curves.Default(b.Role, profileIndex);
            var input = new TextBox { Text = string.Join(", ", points.Select(x => $"{x.Temperature}:{x.Percent}")), Left = 215, Top = y, Width = 450 };
            dialog.Controls.Add(input);
            editors.Add((b, input));
        }
        var save = new Button { Text = "Сохранить", Left = 545, Top = 260, Width = 120 };
        var cancel = new Button { Text = "Отмена", Left = 420, Top = 260, Width = 110, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try
            {
                var changes = new Dictionary<string, List<CurvePoint>>();
                foreach (var (binding, input) in editors)
                {
                    var points = new List<CurvePoint>();
                    foreach (var token in input.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var pair = token.Split(':', StringSplitOptions.TrimEntries);
                        if (pair.Length != 2 || !int.TryParse(pair[0], out var temperature) || !int.TryParse(pair[1], out var percent))
                            throw new ArgumentException("Используйте формат 30:40, 50:55, 70:80, 85:100");
                        points.Add(new CurvePoint(temperature, percent));
                    }
                    Curves.Validate(points, Math.Max(40, binding.MinimumPercent));
                    changes.Add(binding.ControlId, points);
                }
                foreach (var (id, points) in changes) profile.Curves[id] = points;
                settings.Save();
                AppLog.Write("Кривые профиля обновлены: " + profile.Name);
                if (engine.Mode == FanMode.Manual) engine.Tick();
                Render();
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Проверьте кривые", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        dialog.Controls.Add(cancel); dialog.Controls.Add(save);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        dialog.ShowDialog(this);
    }
    private void Exit()
    {
        exitRequested = true;
        Cleanup();
        Close();
    }
    private void Cleanup()
    {
        if (cleanedUp) return;
        cleanedUp = true;
        timer.Stop();
        try { engine.Dispose(); }
        catch (Exception ex) { AppLog.Write("Ошибка завершения управления: " + ex); }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        tray.Visible = false;
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) BeginInvoke(() => { engine.RestoreBios("Сон/пробуждение Windows"); Render(); });
    }
    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        const string name = "FanGuardian";
        if (key.GetValue(name) != null)
        {
            key.DeleteValue(name); AppLog.Write("Автозапуск выключен"); MessageBox.Show("Автозапуск выключен."); return;
        }
        if (MessageBox.Show("Включить автозапуск при входе в Windows? Приложение стартует в режиме BIOS. Для управления вентилятором могут потребоваться права администратора; если доступа нет, охлаждение останется у BIOS. Постоянные запросы UAC этот способ не устраняет.", "Автозапуск", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var host = Environment.ProcessPath!;
        var assembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
        key.SetValue(name, "\"" + host + "\"" + (Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? " \"" + assembly + "\"" : ""));
        AppLog.Write("Автозапуск включён"); MessageBox.Show("Автозапуск включён для текущего пользователя.");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}
