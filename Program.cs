namespace PCStatus;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--selftest")
        {
            SelfTest(args[1]);
            return;
        }
        // Used by the installer / uninstaller.
        if (args.Length == 1 && args[0] == "--enable-autostart")
        {
            Environment.ExitCode = Autostart.Enable() ? 0 : 1;
            return;
        }
        if (args.Length == 1 && args[0] == "--disable-autostart")
        {
            Autostart.Disable();
            return;
        }
        if (args.Length == 2 && args[0] == "--make-icon")
        {
            MakeAppIcon(args[1]);
            return;
        }

        using var mutex = new Mutex(true, @"Local\PCStatus.SingleInstance", out bool isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
    }

    /// <summary>Writes the app/installer .ico (PNG-compressed entries) using the tray bar design.</summary>
    private static void MakeAppIcon(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        float[] values = [45, 70, 25, 90];
        var pngs = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float r = size * 0.2f;
                using var bg = new System.Drawing.Drawing2D.GraphicsPath();
                bg.AddArc(0, 0, r * 2, r * 2, 180, 90);
                bg.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
                bg.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
                bg.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
                bg.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(32, 32, 32))) g.FillPath(b, bg);

                int inset = Math.Max(2, size / 6);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                using var bars = UI.TrayIconRenderer.RenderBitmap(values, false, size - inset * 2);
                g.DrawImageUnscaled(bars, inset, inset);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }

    /// <summary>Writes a few sensor samples to a text file, for checking readings without the UI.</summary>
    private static void SelfTest(string outPath)
    {
        var sb = new System.Text.StringBuilder();
        using var sensors = new Sensors.SensorService();
        foreach (var g in sensors.Gpus)
            sb.AppendLine($"GPU: {g.ShortName} | {g.Name} | luid=0x{g.Luid:X} | vendor=0x{g.VendorId:X4} | integrated={g.IsIntegrated} | ded={g.DedicatedGB:0.0} GB | shared={g.SharedGB:0.0} GB");

        var cpuH = new History();
        var ramH = new History();
        var gpuH = sensors.Gpus.Select(_ => new History()).ToList();
        Sensors.SensorSnapshot? last = null;
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(1000);
            var s = sensors.Sample();
            last = s;
            cpuH.Add(s.CpuPct);
            ramH.Add(s.RamPct);
            for (int j = 0; j < s.Gpus.Count; j++) gpuH[j].Add(s.Gpus[j].LoadPct);
            sb.Append($"CPU {s.CpuPct:0.0}% temp={s.CpuTempC?.ToString("0") ?? "-"} | RAM {s.RamPct:0.0}% {s.RamUsedGB:0.0}/{s.RamTotalGB:0.0} GB | pawnio={s.PawnIoInstalled}");
            foreach (var g in s.Gpus)
                sb.Append($" | {g.Info.ShortName} {g.LoadPct:0.0}% temp={g.TempC?.ToString("0") ?? (g.TempSkippedIdle ? "idle" : "-")} mem={g.MemUsedGB:0.00}/{g.MemTotalGB:0.0} GB");
            sb.AppendLine();
        }
        File.WriteAllText(outPath, sb.ToString());

        // Images: tray icon at common sizes (both taskbar themes, upscaled 8x) and the detail panel.
        string dir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        var values = new List<float> { last!.CpuPct };
        values.AddRange(last.Gpus.Select(g => g.LoadPct));
        values.Add(last.RamPct);
        values[0] = 92; // force one red bar so all colors show up
        foreach (int size in new[] { 16, 20, 24, 32 })
            foreach (bool light in new[] { true, false })
            {
                using var icon = UI.TrayIconRenderer.RenderBitmap(values, light, size);
                using var big = new Bitmap(size * 8, size * 8);
                using (var g = Graphics.FromImage(big))
                {
                    g.Clear(light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(icon, 0, 0, size * 8, size * 8);
                }
                big.Save(Path.Combine(dir, $"icon_{size}_{(light ? "light" : "dark")}.png"));
            }

        using var panel = new UI.DetailPanel();
        var rows = TrayApp.BuildRows(last, TrayApp.ReadCpuName(), cpuH, gpuH, ramH);
        using var pbmp = panel.RenderToBitmap(rows, 1.5f);
        pbmp.Save(Path.Combine(dir, "panel.png"));
    }
}
