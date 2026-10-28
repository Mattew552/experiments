namespace FanGuardian;

// Explicit diagnostic mode: no LHM, worker, settings load/save or elevation.
internal static class UiSmoke
{
    public static void Run(string imagePath)
    {
        ApplicationConfiguration.Initialize();
        var bindings = Enumerable.Range(0, 6).Select(i => new FanBinding
        {
            ControlId = $"/demo/control/{i}", RpmId = $"/demo/fan/{i}",
            Role = i == 0 ? FanRole.Cpu : FanRole.Case, Label = i == 0 ? "CPU-кулер" : $"Корпусная группа {i}",
            MappingVerified = true, ReturnVerified = true, SpeedVerified = true, MinimumPercent = 50
        }).ToList();
        using var form = new MainForm(new DemoHardware(bindings), new Settings { Bindings = bindings });
        form.Show();
        Application.DoEvents();
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        if (Descendants(form).OfType<RpmGraph>().Count() != bindings.Count)
            throw new Exception("Интерфейс не показал произвольное число групп");
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("UI smoke OK (simulated sensors only)");
    }

    private sealed class DemoHardware(List<FanBinding> bindings) : IFanHardware
    {
        public IReadOnlyList<SensorInfo> Read() => new[] { new SensorInfo("/intelcpu/0/temperature/0", "CPU Package", "Demo CPU", "Temperature", 43) }
            .Concat(bindings.Select((b, i) => new SensorInfo(b.RpmId, "Fan " + i, "Demo board", "Fan", 900 + i * 100))).ToArray();
        public void Set(string id, int percent) => throw new Exception("UI smoke must not issue fan commands");
        public void Default(string id) => throw new Exception("UI smoke must not issue restore commands");
        public void Dispose() { }
    }
}
