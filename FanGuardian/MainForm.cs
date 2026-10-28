using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace FanGuardian;

internal sealed class MainForm : Form
{
    private readonly Settings settings;
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
    private readonly FlowLayoutPanel graphPanel = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Dictionary<string, RpmGraph> graphs = new(StringComparer.Ordinal);
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly string heartbeat = Path.Combine(Settings.DirectoryPath, "heartbeat");
    private CancellationTokenSource? probeCancellation;
    private bool scanning;
    private bool exitRequested;
    private bool cleanedUp;

    public MainForm(IFanHardware? hardwareOverride = null, Settings? settingsOverride = null)
    {
        Text = "FanGuardian — управление вентиляторами";
        Width = 800; Height = 660; MinimumSize = new(780, 620);
        settings = settingsOverride ?? Settings.Load();
        hardware = hardwareOverride ?? new WorkerHardware(heartbeat); engine = new Engine(hardware, settings);
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
        modes.Controls.Add(Button("Исходный режим платы", () => Change(FanMode.Bios)));
        modes.Controls.Add(Button("Корпусные выключены", () => Change(FanMode.CaseOff)));
        modes.Controls.Add(Button("Ручной режим", () => Change(FanMode.Manual)));
        root.Controls.Add(modes);
        var connect = new Button { Text = "Подключить новую систему", AutoSize = true, Margin = new(3, 2, 3, 8) };
        connect.Click += (_, _) => RunAdvanced(ConnectSystem);
        root.Controls.Add(connect);
        var manualSettings = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new(3, 0, 3, 8) };
        manualSettings.Controls.Add(new Label { Text = "Профиль ручного режима:", AutoSize = true, Margin = new(3, 7, 8, 3), ForeColor = Color.FromArgb(165, 174, 190) });
        manualSettings.Controls.Add(profiles);
        var editCurves = new LinkLabel { Text = "Настроить кривые", AutoSize = true, Margin = new(16, 7, 3, 3), LinkColor = Color.FromArgb(75, 194, 255), ActiveLinkColor = Color.White };
        editCurves.LinkClicked += (_, _) => RunAdvanced(EditCurves);
        manualSettings.Controls.Add(editCurves);
        root.Controls.Add(manualSettings);
        root.Controls.Add(graphPanel);
        root.Controls.Add(footer);
        var menu = new MenuStrip();
        var service = new ToolStripMenuItem("Сервис");
        service.DropDownItems.Add("Датчики и диагностика", null, (_, _) => ShowDiagnostics());
        service.DropDownItems.Add("Проверить остановку корпусных", null, (_, _) => RunAdvanced(() => CheckCaseStop()));
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
        trayMenu.Items.Add("Исходный режим платы", null, (_, _) => Change(FanMode.Bios));
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
        timer.Tick += (_, _) =>
        {
            if (hardwareOverride is null) File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToString("O"));
            engine.Tick(); Render();
        };
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
        headline.Text = "CPU  " + (engine.CpuTemperature?.ToString("F1") ?? "—") + " °C     Кулер  " + (cpuRpm?.ToString("F0") ?? "—") + " RPM";
        status.Text = scanning ? "Проверка подключения: состояние показано в окне мастера"
            : engine.Status + (engine.Mode == FanMode.Manual ? "  ·  Профиль: " + settings.Profiles[settings.SelectedProfile].Name : "");
        var access = Diagnostics.Read();
        diagnostics.Text = "Диагностика: " + access.Summary + (access.SensorAccessLikely ? "" : ". При 0 °C и отсутствии RPM управление заблокировано.");
        var controlCount = hardware.Channels.Count(c => c.AllowsFanWrites);
        var rpmCount = FanDiscovery.ActiveTachometers(engine.Sensors).Count;
        discovery.Text = $"Найдено записей Control: {controlCount}; датчиков с ненулевыми RPM: {rpmCount}. Номера Fan # не определяют физическую роль или возможность управления.";
        var verifiedCount = settings.Bindings.Count(b => b.IsVerified && FanRoles.IsControllable(b.Role));
        var detectedFans = FanDiscovery.ActiveTachometers(engine.Sensors);
        var displayedFans = detectedFans.Concat(settings.Bindings.Where(b => b.IsVerified && !detectedFans.Any(f => f.Id == b.RpmId))
            .Select(b =>
            {
                var sensor = engine.Sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == b.RpmId);
                return new DetectedFan(b.RpmId, sensor?.Hardware ?? "Проверенная группа", sensor?.Name ?? b.DisplayLabel, sensor?.Value ?? 0);
            }));
        foreach (var fan in displayedFans)
        {
            if (graphs.ContainsKey(fan.Id)) continue;
            var color = new[] { Color.FromArgb(75, 194, 255), Color.FromArgb(134, 224, 171), Color.FromArgb(255, 193, 113) }[graphs.Count % 3];
            var graph = new RpmGraph(Telemetry.Caption(fan, settings.Bindings), color);
            graphs.Add(fan.Id, graph);
            graphPanel.Controls.Add(graph);
        }
        foreach (var (id, graph) in graphs)
        {
            var fan = engine.Sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == id);
            if (fan != null) graph.Caption = Telemetry.Caption(new DetectedFan(id, fan.Hardware, fan.Name, fan.Value ?? 0), settings.Bindings);
            graph.Add(Telemetry.Rpm(engine.Sensors, id));
        }
        var legacyFronts = settings.Bindings.Any(b => FanRoles.IsFront(b.Role) && b.IsVerified);
        footer.Text = verifiedCount > 0
            ? $"Настроено групп: {verifiedCount}. Новые группы работают в проверенном ненулевом диапазоне; остановка доступна только после отдельной проверки." +
              (legacyFronts ? " На этом ПК остановка касается передних групп; задний Molex продолжает работать." : "")
            : "Датчики найдены автоматически. Для настройки управления нажмите «Подключить новую систему».";
        tray.Text = ("FanGuardian: " + (scanning ? "проверка каналов" : engine.Mode) + "; CPU " + (engine.CpuTemperature?.ToString("F0") ?? "?")) switch { var s when s.Length > 63 => s[..63], var s => s };
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
        if (scanning) throw new InvalidOperationException("Дождитесь завершения проверки каналов");
        if (mode != FanMode.Bios && !settings.Bindings.Any(b => b.MappingVerified && b.ReturnVerified && b.SpeedVerified))
        {
            MessageBox.Show("Показания вентиляторов уже доступны. Для управления нужна однократная проверка соответствия разъёмов и каналов на этом ПК. Пока команды вентиляторам не отправляются.", "Аппаратная проверка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (mode == FanMode.CaseOff)
        {
            engine.Tick();
            if (engine.CpuCoolingProblem == null && settings.Bindings.Any(b => b.IsVerified && FanRoles.IsCase(b.Role)) &&
                !settings.Bindings.Any(b => b.IsVerified && FanRoles.IsCase(b.Role) && b.StopVerified))
            {
                if (!CheckCaseStop()) return;
                engine.Tick();
            }
        }
        engine.SelectMode(mode); Render();
    }
    private void RunAdvanced(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLog.Write("Ошибка UI: " + ex.Message); MessageBox.Show(ex.Message, "FanGuardian", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void ConnectSystem()
    {
        if (scanning) return;
        if (engine.ControlBlockReason != null) throw new InvalidOperationException(engine.ControlBlockReason);
        engine.RestoreBios("Подключение системы");
        if (engine.Status.StartsWith("Возврат BIOS не подтверждён", StringComparison.Ordinal))
            throw new InvalidOperationException("Сначала проверьте возврат управления плате");
        if (hardware is not WorkerHardware worker) throw new InvalidOperationException("Сторож управления недоступен");
        using var dialog = new SystemSetupForm(hardware, worker, settings, BlockCurrentControl);
        scanning = true;
        probeCancellation = dialog.Cancellation;
        try { dialog.ShowDialog(this); }
        finally { scanning = false; probeCancellation = null; }
        engine.Tick();
        Render();
    }
    private void BlockCurrentControl(string reason)
    {
        engine.BlockControl(reason);
        if (hardware is not WorkerHardware worker) return;
        try { worker.BlockControl(); }
        catch (Exception ex) { AppLog.Write("Не удалось подтвердить блокировку сторожа: " + ex.Message); }
    }
    private bool CheckCaseStop()
    {
        if (scanning) throw new InvalidOperationException("Дождитесь завершения текущей проверки");
        if (engine.ControlBlockReason != null) throw new InvalidOperationException(engine.ControlBlockReason);
        engine.RestoreBios("Подготовка проверки остановки");
        if (engine.Status.StartsWith("Возврат BIOS не подтверждён", StringComparison.Ordinal))
            throw new InvalidOperationException("Возврат исходного режима не подтверждён");
        engine.Tick();
        if (engine.CpuCoolingProblem is { } cpuProblem) throw new InvalidOperationException(cpuProblem);
        if (engine.CpuTemperature is not (>= 20 and < 60)) throw new InvalidOperationException("Для проверки остановки нужна температура CPU ниже 60 °C");
        if (!settings.Bindings.Any(b => b.IsVerified && FanRoles.IsCase(b.Role) && !b.StopVerified))
        {
            MessageBox.Show(this, "Нет новых корпусных групп для проверки остановки. Уже проверенные группы доступны обычной кнопкой.", "FanGuardian");
            return false;
        }
        if (hardware is not WorkerHardware worker) throw new InvalidOperationException("Сторож управления недоступен");
        using var dialog = new StopTestForm(worker, settings, BlockCurrentControl);
        scanning = true; probeCancellation = dialog.Cancellation;
        try { return dialog.ShowDialog(this) == DialogResult.OK; }
        finally { scanning = false; probeCancellation = null; engine.Tick(); Render(); }
    }
    private void ShowDiagnostics()
    {
        using var dialog = new Form { Text = "Датчики и диагностика", Width = 900, Height = 520, StartPosition = FormStartPosition.CenterParent };
        var detected = FanDiscovery.ActiveTachometers(engine.Sensors);
        var detectedText = detected.Count == 0
            ? "Активные тахометры не обнаружены."
            : "Активные тахометры (физические роли не определены):" + Environment.NewLine +
              string.Join(Environment.NewLine, detected.Select(f => $"{f.Hardware} / {f.Name}: {f.Rpm:F0} RPM [{f.Id}]"));
        var details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Text = diagnostics.Text + Environment.NewLine + discovery.Text + Environment.NewLine + Environment.NewLine + detectedText +
                   Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, channels.Items.Cast<string>()) +
                   Environment.NewLine + Environment.NewLine + "Возможности выходов:" + Environment.NewLine +
                   string.Join(Environment.NewLine, hardware.Channels.Select(c => $"{c.Name} [{c.Id}]: {c.Origin}, возврат {c.Restore}, запись {(c.AllowsFanWrites ? "доступна после настройки" : "запрещена")}")) };
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
        if (!CurveEditor.Show(this, settings, profiles.SelectedIndex)) return;
        AppLog.Write("Кривые профиля обновлены: " + profiles.Text);
        if (engine.Mode == FanMode.Manual) engine.Tick();
        Render();
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
        probeCancellation?.Cancel();
        timer.Stop();
        try { engine.Dispose(); }
        catch (Exception ex) { AppLog.Write("Ошибка завершения управления: " + ex); }
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        tray.Visible = false;
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume) BeginInvoke(() => { probeCancellation?.Cancel(); engine.RestoreBios("Сон/пробуждение Windows"); Render(); });
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
        if (disposing) { Cleanup(); timer.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}
