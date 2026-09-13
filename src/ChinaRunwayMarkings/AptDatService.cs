using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ChinaRunwayMarkings;

public static partial class AptDatService
{
    public const string ExportFolderPrefix = "China_Runway_Marking_Export";

    private static readonly HashSet<string> MainlandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZB", "ZG", "ZH", "ZJ", "ZL", "ZP", "ZS", "ZU", "ZW", "ZY"
    };

    public static async Task<List<AirportRecord>> ScanChinaAirportsAsync(
        string aptDatPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAptDat(aptDatPath);
        var fileInfo = new FileInfo(aptDatPath);
        var results = new List<AirportRecord>(400);
        AirportBuilder? current = null;
        long lastReported = 0;

        await using var stream = OpenLargeFile(aptDatPath);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false), true, 1024 * 1024, leaveOpen: true);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trimmed = line.TrimStart();

            if (TryParseAirportHeader(trimmed, out var header))
            {
                FinalizeAirport(current, results);
                current = header;
            }
            else if (current is not null)
            {
                if (trimmed.StartsWith("1302 ", StringComparison.Ordinal))
                {
                    ParseMetadata(trimmed, current);
                }
                else if (trimmed.StartsWith("100 ", StringComparison.Ordinal) && TryParseRunway(trimmed, out var runway))
                {
                    current.Runways.Add(runway);
                }
            }

            var position = stream.Position;
            if (position - lastReported >= 8L * 1024 * 1024)
            {
                lastReported = position;
                progress?.Report(new ScanProgress(position, fileInfo.Length, results.Count));
            }
        }

        FinalizeAirport(current, results);
        progress?.Report(new ScanProgress(fileInfo.Length, fileInfo.Length, results.Count));
        return results
            .OrderBy(a => a.IcaoCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static async Task<AptDatExportResult> ExportModifiedAptDatAsync(
        string aptDatPath,
        string outputRootPath,
        IReadOnlyCollection<AirportRecord> selectedAirports,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAptDat(aptDatPath);
        if (string.IsNullOrWhiteSpace(outputRootPath))
            throw new DirectoryNotFoundException("请选择导出目录。");

        var outputRoot = Path.GetFullPath(outputRootPath);
        EnsureOutputIsOutsideXPlane(aptDatPath, outputRoot);
        Directory.CreateDirectory(outputRoot);
        var selectedIds = selectedAirports
            .Where(a => a.HasSuggestedChanges)
            .Select(a => a.RecordId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedIds.Count == 0)
            throw new InvalidOperationException("请至少选择一个包含 2/3 型标线的机场。");

        using var stableSource = new FileStream(aptDatPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceInfo = new FileInfo(aptDatPath);
        var originalSourceSize = sourceInfo.Length;
        var originalSourceLastWriteUtc = sourceInfo.LastWriteTimeUtc;
        var createdAt = DateTimeOffset.Now;
        var folderName = $"{ExportFolderPrefix}_{createdAt:yyyyMMdd-HHmmss}";
        var exportDirectory = Path.Combine(outputRoot, folderName);
        if (Directory.Exists(exportDirectory))
            exportDirectory = Path.Combine(outputRoot, $"{folderName}-{Guid.NewGuid():N}"[..(folderName.Length + 9)]);
        var stagingDirectory = Path.Combine(outputRoot, $".{ExportFolderPrefix}.exporting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var outputAptDat = Path.Combine(stagingDirectory, "apt.dat");
        var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runwayEndsChanged = 0;
        try
        {
            await using (var output = new StreamWriter(outputAptDat, false, new UTF8Encoding(false), 1024 * 1024))
            {
                output.NewLine = "\n";
                await using var inputStream = OpenLargeFile(aptDatPath);
                using var reader = new StreamReader(inputStream, new UTF8Encoding(false, false), true, 1024 * 1024, leaveOpen: true);
                var patchCurrentAirport = false;
                long lastReported = 0;

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var trimmed = line.TrimStart();
                    if (TryParseAirportHeader(trimmed, out var header))
                    {
                        patchCurrentAirport = selectedIds.Contains(header.RecordId);
                        if (patchCurrentAirport) foundIds.Add(header.RecordId);
                    }
                    else if (trimmed == "99")
                    {
                        patchCurrentAirport = false;
                    }

                    if (patchCurrentAirport && trimmed.StartsWith("100 ", StringComparison.Ordinal))
                    {
                        var patched = PatchRunwayLine(line, out var changed);
                        runwayEndsChanged += changed;
                        await output.WriteLineAsync(patched);
                    }
                    else
                    {
                        await output.WriteLineAsync(line);
                    }

                    var position = inputStream.Position;
                    if (position - lastReported >= 8L * 1024 * 1024)
                    {
                        lastReported = position;
                        progress?.Report(new BuildProgress(position, sourceInfo.Length, foundIds.Count));
                    }
                }
            }

            if (foundIds.Count != selectedIds.Count)
                throw new InvalidDataException($"只在源文件中找到 {foundIds.Count}/{selectedIds.Count} 个所选机场，已停止导出。请重新扫描源文件。");
            if (runwayEndsChanged == 0)
                throw new InvalidDataException("没有实际转换任何跑道端，已停止导出。");

            var sourceSha256 = await ComputeSha256Async(aptDatPath, cancellationToken);
            sourceInfo.Refresh();
            if (sourceInfo.Length != originalSourceSize || sourceInfo.LastWriteTimeUtc != originalSourceLastWriteUtc)
                throw new IOException("源 apt.dat 在导出过程中发生变化，已停止交付输出。请重新扫描后再试。");
            var outputSha256 = await ComputeSha256Async(outputAptDat, cancellationToken);
            var manifest = new AptDatExportManifest
            {
                ToolVersion = "0.5.0",
                CreatedAt = createdAt,
                SourceAptDat = Path.GetFullPath(aptDatPath),
                SourceSize = originalSourceSize,
                SourceLastWriteUtc = originalSourceLastWriteUtc,
                SourceSha256 = sourceSha256,
                OutputSha256 = outputSha256,
                AirportRecordIds = selectedAirports.Where(a => selectedIds.Contains(a.RecordId)).Select(a => a.RecordId).Order().ToList(),
                Airports = selectedAirports.Where(a => selectedIds.Contains(a.RecordId)).Select(a => a.IcaoCode).Order().ToList(),
                RunwayEndsChanged = runwayEndsChanged
            };
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(stagingDirectory, "手动替换说明.txt"),
                BuildExportReadme(manifest),
                new UTF8Encoding(false), cancellationToken);

            Directory.Move(stagingDirectory, exportDirectory);
            progress?.Report(new BuildProgress(sourceInfo.Length, sourceInfo.Length, foundIds.Count));
            return new AptDatExportResult(
                exportDirectory,
                Path.Combine(exportDirectory, "apt.dat"),
                foundIds.Count,
                runwayEndsChanged,
                sourceSha256,
                outputSha256);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    public static int ConvertMarkingCode(int code) => code switch { 2 => 6, 3 => 7, _ => code };

    public static string MarkingName(int code) => code switch
    {
        0 => "无",
        1 => "目视",
        2 => "普通非精密",
        3 => "普通精密",
        4 => "UK 非精密",
        5 => "UK 精密",
        6 => "ICAO/EASA 非精密",
        7 => "ICAO/EASA 精密",
        _ => $"未知({code})"
    };

    public static bool TryParseRunway(string line, out RunwayRecord runway)
    {
        runway = null!;
        var tokens = SplitTokens(line);
        if (tokens.Length < 25 || tokens[0] != "100") return false;
        if (!int.TryParse(tokens[2], out var surface) ||
            !int.TryParse(tokens[13], out var marking1) ||
            !int.TryParse(tokens[22], out var marking2)) return false;

        runway = new RunwayRecord(surface,
            new RunwayEnd(tokens[8], marking1),
            new RunwayEnd(tokens[17], marking2));
        return true;
    }

    public static string PatchRunwayLine(string line, out int changedEnds)
    {
        changedEnds = 0;
        var parts = WhitespaceSplitRegex().Split(line);
        var tokenPartIndexes = new List<int>(25);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && !char.IsWhiteSpace(parts[i][0])) tokenPartIndexes.Add(i);
        }

        if (tokenPartIndexes.Count < 25 || parts[tokenPartIndexes[0]] != "100") return line;
        foreach (var tokenIndex in new[] { 13, 22 })
        {
            var partIndex = tokenPartIndexes[tokenIndex];
            if (!int.TryParse(parts[partIndex], out var oldCode)) continue;
            var newCode = ConvertMarkingCode(oldCode);
            if (newCode == oldCode) continue;
            parts[partIndex] = newCode.ToString();
            changedEnds++;
        }
        return string.Concat(parts);
    }

    private static FileStream OpenLargeFile(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void ValidateAptDat(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("找不到 apt.dat，请重新选择文件。", path);
        if (!string.Equals(Path.GetFileName(path), "apt.dat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择名为 apt.dat 的机场数据库文件。");
    }

    private static bool TryParseAirportHeader(string line, out AirportBuilder builder)
    {
        builder = null!;
        var tokens = SplitTokens(line, 6);
        if (tokens.Length < 6 || tokens[0] is not ("1" or "16" or "17")) return false;
        builder = new AirportBuilder
        {
            RecordId = tokens[4],
            IcaoCode = tokens[4],
            Name = tokens[5]
        };
        return true;
    }

    private static void ParseMetadata(string line, AirportBuilder builder)
    {
        var tokens = SplitTokens(line, 3);
        if (tokens.Length < 3) return;
        switch (tokens[1])
        {
            case "country":
                var countryTokens = SplitTokens(tokens[2], 2);
                builder.CountryCode = countryTokens.Length > 0 ? countryTokens[0].ToUpperInvariant() : "";
                break;
            case "icao_code":
                builder.IcaoCode = tokens[2].Trim();
                break;
        }
    }

    private static void FinalizeAirport(AirportBuilder? builder, List<AirportRecord> results)
    {
        if (builder is null) return;
        var code = string.IsNullOrWhiteSpace(builder.IcaoCode) ? builder.RecordId : builder.IcaoCode;
        var prefix = code.Length >= 2 ? code[..2] : "";
        var isMainlandChina = builder.CountryCode.Equals("CHN", StringComparison.OrdinalIgnoreCase) ||
                              (string.IsNullOrEmpty(builder.CountryCode) && MainlandPrefixes.Contains(prefix));
        if (!isMainlandChina) return;

        var airport = new AirportRecord
        {
            RecordId = builder.RecordId,
            IcaoCode = code,
            Name = builder.Name,
            CountryCode = string.IsNullOrEmpty(builder.CountryCode) ? "CHN?" : builder.CountryCode,
            Runways = builder.Runways,
        };
        airport.Selected = false;
        results.Add(airport);
    }

    private static string[] SplitTokens(string value, int count = int.MaxValue) =>
        value.Split((char[]?)null, count, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void EnsureOutputIsOutsideXPlane(string aptDatPath, string outputRoot)
    {
        var source = new FileInfo(Path.GetFullPath(aptDatPath));
        var earthNavData = source.Directory;
        var globalAirports = earthNavData?.Parent;
        var globalScenery = globalAirports?.Parent;
        var xPlaneRoot = globalScenery?.Parent;
        if (earthNavData is null || globalAirports is null || globalScenery is null || xPlaneRoot is null) return;
        if (!earthNavData.Name.Equals("Earth nav data", StringComparison.OrdinalIgnoreCase) ||
            !globalAirports.Name.Equals("Global Airports", StringComparison.OrdinalIgnoreCase) ||
            !globalScenery.Name.Equals("Global Scenery", StringComparison.OrdinalIgnoreCase)) return;

        var relative = Path.GetRelativePath(xPlaneRoot.FullName, outputRoot);
        var insideXPlane = relative == "." ||
            (!relative.Equals("..", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !Path.IsPathRooted(relative));
        if (insideXPlane)
            throw new InvalidOperationException("安全导出目录不能位于 X-Plane 安装目录内。请选择桌面、文档或其他独立目录。");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string BuildExportReadme(AptDatExportManifest manifest) => $"""
        中国机场跑道标线工具 0.5.0 — apt.dat 备份与应用版
        ======================================================

        生成时间：{manifest.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}
        修改机场：{manifest.Airports.Count} 个
        转换跑道端：{manifest.RunwayEndsChanged} 个
        转换规则：2 → 6（非精密），3 → 7（精密）
        源文件 SHA-256：{manifest.SourceSha256}
        输出文件 SHA-256：{manifest.OutputSha256}

        本目录中的 apt.dat 是源文件的完整修改副本，不是独立 Custom Scenery 地景包。
        生成副本不会修改源文件。生成后选择“是”会自动备份并替换所选源文件；选择“否”仅导出。
        程序不修改 scenery_packs.ini。自动应用的原版备份在源文件同目录的 .ChinaRunwayMarkingsBackups 中。
        可在程序中点击“一键恢复原版备份”。以下步骤适用于选择“否”后的手动操作。

        手动测试步骤：
        1. 完全退出 X-Plane。
        2. 找到 X-Plane 12/Global Scenery/Global Airports/Earth nav data/apt.dat。
        3. 将游戏原始 apt.dat 复制到安全位置并保留，不要只改名后留在同一目录。
        4. 把本目录中的 apt.dat 手动复制到上述位置，替换游戏文件。
        5. 启动 X-Plane 检查跑道标线、航站楼和机场设施。
        6. 如需回退，退出 X-Plane，再用备份的原始 apt.dat 覆盖回去。

        Steam 验证文件或 X-Plane 更新可能恢复 Global Airports 的 apt.dat；重新应用前请重新扫描更新后的源文件。
        """;

    [GeneratedRegex(@"(\s+)")]
    private static partial Regex WhitespaceSplitRegex();

    private sealed class AirportBuilder
    {
        public required string RecordId { get; init; }
        public required string IcaoCode { get; set; }
        public required string Name { get; init; }
        public string CountryCode { get; set; } = "";
        public List<RunwayRecord> Runways { get; } = new();
    }
}
