using System.Diagnostics;
using LibreHardwareMonitor.Hardware;

namespace FanGuardian;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") { AppLog.Suppressed = true; SelfTest.Run(); return; }
        if (args.Length == 2 && args[0] == "--ui-smoke") { AppLog.Suppressed = true; UiSmoke.Run(args[1]); return; }
        if (args.Length > 0 && args[0] == "--probe")
        {
            Console.WriteLine(Diagnostics.Read().Summary);
            try
            {
                using var probe = new LhmHardware();
                foreach (var s in probe.Read()) Console.WriteLine($"{s.Type}\t{s.Hardware}\t{s.Name}\t{s.Id}\t{s.Value?.ToString() ?? "?"}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Ошибка чтения датчиков: " + ex.Message);
                Environment.ExitCode = 2;
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--worker" && args.Length == 4) { WorkerHost.Run(int.Parse(args[1]), args[2], args[3]); return; }
        if (!Diagnostics.Read().Elevated)
        {
            try
            {
                Process.Start(ElevatedStartInfo());
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show("Для чтения датчиков платы нужны права администратора. Запустите FanGuardian снова и разрешите запрос Windows.", "FanGuardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        using var single = new Mutex(true, "Local\\FanGuardian_single_instance", out var first);
        if (!first) { MessageBox.Show("FanGuardian уже запущен."); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
    internal static ProcessStartInfo ElevatedStartInfo()
    {
        var host = Environment.ProcessPath!;
        var assembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var arguments = Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? $"\"{assembly}\"" : "";
        return new ProcessStartInfo(host, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
    }
    internal static Process StartWorker(int pid, string heartbeatPath, string directory)
    {
        var host = Environment.ProcessPath!;
        var assembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var prefix = Path.GetFileName(host).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? $"\"{assembly}\" " : "";
        return Process.Start(new ProcessStartInfo(host, $"{prefix}--worker {pid} \"{heartbeatPath}\" \"{directory}\"") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("Сторож управления не запустился");
    }
}
