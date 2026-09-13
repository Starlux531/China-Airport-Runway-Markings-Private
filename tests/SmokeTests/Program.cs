using ChinaRunwayMarkings;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

if (args.Length == 1 && args[0] == "--locate")
{
    foreach (var installation in XPlaneLocator.Discover()) Console.WriteLine(XPlaneLocator.AptDatForRoot(installation));
    return;
}

if ((args.Length == 2 && args[0] == "--preview") || (args.Length == 3 && args[0] == "--render"))
{
    var previewAirports = await AptDatService.ScanChinaAirportsAsync(args[1]);
    var thread = new Thread(() =>
    {
        ApplicationConfiguration.Initialize();
        using var form = new MainForm(new LocalState(Path.Combine(Path.GetTempPath(), "ChinaRunwayPreview-" + Guid.NewGuid().ToString("N"))));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("_autoScanStarted", flags)!.SetValue(form, true);
        form.Show();
        ((TextBox)typeof(MainForm).GetField("_aptPath", flags)!.GetValue(form)!).Text = @"D:\X-Plane 12\Global Scenery\Global Airports\Earth nav data\apt.dat";
        ((TextBox)typeof(MainForm).GetField("_outputPath", flags)!.GetValue(form)!).Text = @"D:\Runway Marking Exports";
        typeof(MainForm).GetField("_airports", flags)!.SetValue(form, previewAirports);
        InvokePrivate(form, "RefreshAirportList", false);
        InvokePrivate(form, "UpdateSummary");
        InvokePrivate(form, "SetBusy", false, "已加载缓存 · apt.dat 路径已记住");
        if (args[0] == "--render")
        {
            Directory.CreateDirectory(args[2]);
            Application.DoEvents();
            using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(args[2], "main-window.png"), System.Drawing.Imaging.ImageFormat.Png);
            InvokePrivate(form, "SelectRecommended");
            ((ComboBox)typeof(MainForm).GetField("_filterBox", flags)!.GetValue(form)!).SelectedIndex = 1;
            Application.DoEvents();
            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(args[2], "recommended-airports.png"), System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
        }
        else Application.Run(form);
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    return;
}

if (args.Length == 2 && File.Exists(args[0]))
{
    var airports = await AptDatService.ScanChinaAirportsAsync(args[0]);
    var selected = airports.Where(a => a.HasSuggestedChanges).ToList();
    var result = await AptDatService.ExportModifiedAptDatAsync(args[0], args[1], selected);
    var exportedAirports = await AptDatService.ScanChinaAirportsAsync(result.AptDatPath);
    Console.WriteLine($"source_bytes={new FileInfo(args[0]).Length}");
    Console.WriteLine($"output_bytes={new FileInfo(result.AptDatPath).Length}");
    Console.WriteLine($"airports_written={result.AirportsWritten}");
    Console.WriteLine($"runway_ends_changed={result.RunwayEndsChanged}");
    Console.WriteLine($"remaining_old_marking_ends={exportedAirports.SelectMany(a => a.Ends).Count(e => e.MarkingCode is 2 or 3)}");
    Console.WriteLine($"output={result.AptDatPath}");
    return;
}

if (args.Length == 1 && File.Exists(args[0]))
{
    var airports = await AptDatService.ScanChinaAirportsAsync(args[0]);
    Console.WriteLine($"airports={airports.Count}");
    Console.WriteLine($"runways={airports.Sum(a => a.Runways.Count)}");
    Console.WriteLine($"ends={airports.Sum(a => a.Runways.Count * 2)}");
    Console.WriteLine($"convertible_airports={airports.Count(a => a.HasSuggestedChanges)}");
    Console.WriteLine($"transparent_airports={airports.Count(a => a.HasTransparentRunway)}");
    foreach (var code in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        Console.WriteLine($"marking_{code}={airports.SelectMany(a => a.Ends).Count(e => e.MarkingCode == code)}");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "ChinaRunwayMarkings-SmokeTest-" + Guid.NewGuid().ToString("N"));
var outputRoot = Path.Combine(root, "Exports");
Directory.CreateDirectory(root);
var source = Path.Combine(root, "apt.dat");
await File.WriteAllTextAsync(source, """
I
1200 Test fixture

1 12 0 0 ZZZZ Mainland Test Airport
1302 country CHN China
1302 icao_code ZZZZ
100 45.00 2 0 0.25 1 3 0  01 31.0 121.0 0 60 3 2 1 1 19 31.1 121.1 0 60 2 2 1 1

1 15 0 0 VHHH Hong Kong Test
1302 country HKG Hong Kong
100 45.00 2 0 0.25 1 3 0  01 22.0 114.0 0 60 3 2 1 1 19 22.1 114.1 0 60 3 2 1 1

99
""", new UTF8Encoding(false));

try
{
    var airports = await AptDatService.ScanChinaAirportsAsync(source);
    Assert(airports.Count == 1, "只应识别 CHN 机场");
    Assert(!airports[0].Selected, "扫描完成后不应默认勾选机场");
    Assert(airports[0].IcaoCode == "ZZZZ", "ICAO 解析失败");
    Assert(airports[0].Runways.Count == 1, "跑道解析失败");
    Assert(airports[0].CurrentSummary.Contains("普通精密") && airports[0].CurrentSummary.Contains("普通非精密"), "标线摘要失败");

    var patched = AptDatService.PatchRunwayLine("100 45.00 2 0 0.25 1 3 0  01 31.0 121.0 0 60 3 2 1 1 19 31.1 121.1 0 60 2 2 1 1", out var changed);
    Assert(changed == 2, "应转换两个跑道端");
    Assert(AptDatService.TryParseRunway(patched, out var runway), "转换后的跑道行不可解析");
    Assert(runway.End1.MarkingCode == 7 && runway.End2.MarkingCode == 6, "2→6 / 3→7 转换失败");

    var sourceBefore = await File.ReadAllBytesAsync(source);
    var sourceLines = await File.ReadAllLinesAsync(source);
    var result = await AptDatService.ExportModifiedAptDatAsync(source, outputRoot, airports);
    Assert(result.AirportsWritten == 1 && result.RunwayEndsChanged == 2, "导出统计错误");
    Assert(File.Exists(result.AptDatPath), "未生成完整 apt.dat");
    Assert(File.Exists(Path.Combine(result.ExportDirectory, "manifest.json")), "未生成导出清单");
    Assert(File.Exists(Path.Combine(result.ExportDirectory, "手动替换说明.txt")), "未生成手动替换说明");
    var sourceAfter = await File.ReadAllBytesAsync(source);
    Assert(sourceBefore.SequenceEqual(sourceAfter), "导出过程修改了源 apt.dat");

    var outputLines = await File.ReadAllLinesAsync(result.AptDatPath);
    Assert(outputLines.Length == sourceLines.Length, "完整导出文件的行数与源文件不同");
    Assert(outputLines.Contains("1200 Test fixture"), "完整导出丢失源文件头");
    Assert(outputLines.Any(line => line.Contains("VHHH Hong Kong Test", StringComparison.Ordinal)), "完整导出丢失未选择机场");
    Assert(outputLines.Any(line => line.Contains("22.0 114.0 0 60 3", StringComparison.Ordinal)), "未选择机场被意外修改");
    Assert(outputLines.Any(line => line.Contains("31.0 121.0 0 60 7", StringComparison.Ordinal) && line.Contains("31.1 121.1 0 60 6", StringComparison.Ordinal)),
        "已选择机场没有写入 3→7 / 2→6 转换");

    var differingLines = sourceLines.Zip(outputLines).Count(pair => pair.First != pair.Second);
    Assert(differingLines == 1, $"除目标跑道行外还有 {differingLines - 1} 行发生变化");
    Assert(result.SourceSha256 == Convert.ToHexString(SHA256.HashData(sourceBefore)), "源文件哈希记录错误");
    var outputBytes = await File.ReadAllBytesAsync(result.AptDatPath);
    Assert(result.OutputSha256 == Convert.ToHexString(SHA256.HashData(outputBytes)), "输出文件哈希记录错误");

    using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.ExportDirectory, "manifest.json"))))
    {
        Assert(manifest.RootElement.GetProperty("ToolVersion").GetString() == "0.5.0", "导出清单版本错误");
        Assert(manifest.RootElement.GetProperty("Airports").GetArrayLength() == 1, "导出清单机场数量错误");
    }

    var fakeXPlaneRoot = Path.Combine(root, "X-Plane 12");
    var fakeEarthNav = Path.Combine(fakeXPlaneRoot, "Global Scenery", "Global Airports", "Earth nav data");
    Directory.CreateDirectory(fakeEarthNav);
    var fakeGameAptDat = Path.Combine(fakeEarthNav, "apt.dat");
    File.Copy(source, fakeGameAptDat);
    var rejectedGameOutput = false;
    try
    {
        await AptDatService.ExportModifiedAptDatAsync(fakeGameAptDat, Path.Combine(fakeXPlaneRoot, "Unsafe Export"), airports);
    }
    catch (InvalidOperationException)
    {
        rejectedGameOutput = true;
    }
    Assert(rejectedGameOutput, "安全导出目录位于 X-Plane 安装目录时必须拒绝写入");

    // Installation resolution returns the file, never only the root.
    Assert(XPlaneLocator.AptDatForRoot(fakeXPlaneRoot) == fakeGameAptDat, "必须直接定位到 apt.dat");
    Assert(XPlaneLocator.RootForAptDat(fakeGameAptDat) == fakeXPlaneRoot, "根目录回溯失败");
    var localState = new LocalState(Path.Combine(root, "state"));
    localState.SaveSettings(new AppSettings { XPlaneRoot = fakeXPlaneRoot, AptDatPath = fakeGameAptDat, OutputDirectory = outputRoot });
    Assert(localState.LoadSettings().AptDatPath == fakeGameAptDat, "未记住 apt.dat 文件路径");
    localState.SaveAirports(source, airports);
    Assert(localState.LoadAirports(source)?.Count == 1, "机场缓存未命中");
    await File.AppendAllTextAsync(source, "\n");
    Assert(localState.LoadAirports(source) is null, "源文件改变后必须使缓存失效");
    await File.WriteAllBytesAsync(source, sourceBefore);

    Console.WriteLine("TEST: apply / restore");
    var backup = BackupService.Apply(fakeGameAptDat, result);
    Assert(BackupService.Hash(backup) == result.SourceSha256, "没有保存字节级原版备份");
    Assert(BackupService.Hash(fakeGameAptDat) == result.OutputSha256, "未应用修正版");
    Assert(BackupService.HasBackups(fakeGameAptDat), "备份未被发现");
    // A second application must retain the first original, not promote modified bytes to original.
    var secondOutput = Path.Combine(root, "second.dat");
    await File.WriteAllTextAsync(secondOutput, await File.ReadAllTextAsync(fakeGameAptDat) + "\n");
    var second = new AptDatExportResult(root, secondOutput, 1, 1, result.OutputSha256, BackupService.Hash(secondOutput));
    var secondBackup = BackupService.Apply(fakeGameAptDat, second);
    Assert(secondBackup == backup, "重复应用覆盖了首次原版备份");
    BackupService.Restore(fakeGameAptDat);
    Assert(BackupService.Hash(fakeGameAptDat) == result.SourceSha256, "恢复未逐字节还原最初版本");
    Assert(BackupService.Restore(fakeGameAptDat).Contains("无需重复恢复"), "重复恢复应安全完成");

    Console.WriteLine("TEST: corruption / external updates");
    // Changed sources and tampered exports/backups must never be overwritten.
    await File.AppendAllTextAsync(fakeGameAptDat, "external update\n");
    var externalHash = BackupService.Hash(fakeGameAptDat);
    AssertThrows(() => BackupService.Apply(fakeGameAptDat, result), "必须拒绝过期导出");
    AssertThrows(() => BackupService.Restore(fakeGameAptDat), "必须拒绝覆盖游戏更新");
    Assert(BackupService.Hash(fakeGameAptDat) == externalHash, "失败操作修改了源文件");
    File.WriteAllBytes(fakeGameAptDat, sourceBefore);
    var outputOriginal = File.ReadAllBytes(result.AptDatPath);
    File.AppendAllText(result.AptDatPath, "tampered");
    AssertThrows(() => BackupService.Apply(fakeGameAptDat, result), "必须拒绝损坏的修正版");
    File.WriteAllBytes(result.AptDatPath, outputOriginal);
    backup = BackupService.Apply(fakeGameAptDat, result);
    var originalBackup = File.ReadAllBytes(backup);
    File.AppendAllText(backup, "tampered");
    AssertThrows(() => BackupService.Restore(fakeGameAptDat), "必须拒绝损坏的原版备份");
    Assert(BackupService.Hash(fakeGameAptDat) == result.OutputSha256, "备份损坏时错误修改了当前文件");
    File.WriteAllBytes(backup, originalBackup);
    BackupService.Restore(fakeGameAptDat);
    using (var exclusive = new FileStream(fakeGameAptDat, FileMode.Open, FileAccess.Read, FileShare.None))
        AssertThrows(() => BackupService.Apply(fakeGameAptDat, result), "目标被占用时必须停止");
    Assert(BackupService.Hash(fakeGameAptDat) == result.SourceSha256, "锁冲突破坏源文件");

    Console.WriteLine("TEST: UI controls");
    Exception? uiError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
    using (var form = new MainForm(localState))
    {
        Console.WriteLine("TEST: form constructed");
        typeof(MainForm).GetField("_autoScanStarted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
        form.Show();
        Application.DoEvents();
        Console.WriteLine("TEST: control created");
        var uiAirports = new List<AirportRecord>
        {
            MakeAirport("ZZC3", "Charlie Airport", 2, 3),
            MakeAirport("ZZA1", "Alpha Airport", 2, 7),
            MakeAirport("ZZB2", "Bravo Airport", 15, 3),
        };
        var formType = typeof(MainForm);
        var airportsField = formType.GetField("_airports", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到机场列表字段");
        airportsField.SetValue(form, uiAirports);
        InvokePrivate(form, "RefreshAirportList", false);

        InvokePrivate(form, "SelectRecommended");
        Assert(uiAirports[0].Selected, "勾选推荐项未选择可转换机场");
        Assert(!uiAirports[1].Selected && !uiAirports[2].Selected,
            "勾选推荐项不应选择已符合或透明跑道机场");

        InvokePrivate(form, "ClearSelection");
        Assert(uiAirports.All(a => !a.Selected), "清除勾选未清空数据状态");
        var list = (ListView)(formType.GetField("_airportList", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(form) ?? throw new InvalidOperationException("找不到列表控件"));
        Assert(list.Items.Cast<ListViewItem>().All(item => !item.Checked), "清除勾选未清空界面复选框");
        _ = list.Handle;

        list.Items[0].Checked = true;
        Assert(uiAirports[0].Selected, "单个复选框未同步到机场选择状态");
        InvokePrivate(form, "ClearSelection");

        InvokePrivate(form, "SortAirportList", 2);
        Assert(uiAirports.Select(a => a.Name).SequenceEqual(new[] { "Alpha Airport", "Bravo Airport", "Charlie Airport" }),
            "机场名称升序排序失败");
        InvokePrivate(form, "SortAirportList", 2);
        Assert(uiAirports.Select(a => a.Name).SequenceEqual(new[] { "Charlie Airport", "Bravo Airport", "Alpha Airport" }),
            "机场名称降序排序失败");
    }
        }
        catch (Exception ex) { uiError = ex; }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    if (!uiThread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("UI tests timed out");
    if (uiError is not null) throw uiError;
    Console.WriteLine("SMOKE TEST PASSED");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static AirportRecord MakeAirport(string code, string name, int surface, int marking) => new()
{
    RecordId = code,
    IcaoCode = code,
    Name = name,
    CountryCode = "CHN",
    Runways = new List<RunwayRecord>
    {
        new(surface, new RunwayEnd("01", marking), new RunwayEnd("19", marking))
    },
    Selected = false,
};

static object? InvokePrivate(object target, string methodName, params object?[] arguments)
{
    var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"找不到方法 {methodName}");
    return method.Invoke(target, arguments);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows(Action action, string message)
{
    try { action(); }
    catch (IOException) { return; }
    throw new InvalidOperationException(message);
}
