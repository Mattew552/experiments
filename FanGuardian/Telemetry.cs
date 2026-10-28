namespace FanGuardian;

// These RPM IDs were confirmed on this PC by individual 80%, 40% and 0% trials.
// Merely reading a graph never changes the control allowlist.
internal static class Telemetry
{
    public const string CpuFan = "/lpc/nct6797d/0/fan/1";
    public const string FrontPair = "/lpc/nct6797d/0/fan/4";
    public const string FrontTop = "/lpc/nct6797d/0/fan/5";

    public static float? Rpm(IReadOnlyList<SensorInfo> sensors, string id)
    {
        var value = sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == id)?.Value;
        return value is >= 0 and < 10000 ? value : null;
    }

    public static float? RpmForRole(IReadOnlyList<SensorInfo> sensors, IEnumerable<FanBinding> bindings, FanRole role)
    {
        var binding = bindings.FirstOrDefault(b => b.Role == role && b.MappingVerified && b.ReturnVerified && b.SpeedVerified);
        return binding is null ? null : Rpm(sensors, binding.RpmId);
    }
}

internal sealed class RpmGraph : Control
{
    private readonly Queue<float?> samples = new();
    private readonly Color lineColor;
    public string Caption { get; }
    private float? current;

    public RpmGraph(string caption, Color color)
    {
        Caption = caption;
        lineColor = color;
        DoubleBuffered = true;
        Height = 150;
        Width = 740;
        BackColor = Color.FromArgb(25, 29, 37);
        ForeColor = Color.White;
        Margin = new Padding(0, 8, 0, 8);
    }

    public void Add(float? rpm)
    {
        current = rpm;
        samples.Enqueue(rpm);
        while (samples.Count > 60) samples.Dequeue();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var title = new Font(Font.FontFamily, 11, FontStyle.Bold);
        using var valueFont = new Font(Font.FontFamily, 18, FontStyle.Bold);
        using var muted = new SolidBrush(Color.FromArgb(165, 174, 190));
        using var linePen = new Pen(lineColor, 2.5f);
        using var gridPen = new Pen(Color.FromArgb(55, 62, 76));
        g.DrawString(Caption, title, Brushes.White, 18, 13);
        var value = current.HasValue ? $"{current.Value:F0} RPM" : "нет показаний";
        var valueSize = g.MeasureString(value, valueFont);
        g.DrawString(value, valueFont, Brushes.White, Width - valueSize.Width - 18, 9);
        var left = 18f; var right = Width - 18f; var top = 64f; var bottom = Height - 16f;
        g.DrawLine(gridPen, left, bottom, right, bottom);
        g.DrawLine(gridPen, left, (top + bottom) / 2, right, (top + bottom) / 2);
        var data = samples.ToArray();
        var maximum = Math.Max(1500f, (float)Math.Ceiling(data.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(0).Max() / 500f) * 500f);
        for (var i = 1; i < data.Length; i++)
        {
            if (!data[i - 1].HasValue || !data[i].HasValue) continue;
            var x1 = right - (data.Length - i) * (right - left) / 59f;
            var x2 = right - (data.Length - i - 1) * (right - left) / 59f;
            var y1 = bottom - Math.Clamp(data[i - 1]!.Value / maximum, 0, 1) * (bottom - top);
            var y2 = bottom - Math.Clamp(data[i]!.Value / maximum, 0, 1) * (bottom - top);
            g.DrawLine(linePen, x1, y1, x2, y2);
        }
        if (data.Length < 2) g.DrawString("История появится через несколько секунд", Font, muted, left, bottom - 24);
    }
}
