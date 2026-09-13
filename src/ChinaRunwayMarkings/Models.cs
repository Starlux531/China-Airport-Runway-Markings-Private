using System.Text.Json.Serialization;

namespace ChinaRunwayMarkings;

public sealed class AirportRecord
{
    public required string RecordId { get; init; }
    public required string IcaoCode { get; set; }
    public required string Name { get; init; }
    public required string CountryCode { get; set; }
    public required List<RunwayRecord> Runways { get; init; }
    public bool Selected { get; set; }

    [JsonIgnore]
    public IEnumerable<RunwayEnd> Ends => Runways.SelectMany(r => new[] { r.End1, r.End2 });

    [JsonIgnore]
    public bool HasTransparentRunway => Runways.Any(r => r.SurfaceCode == 15);

    [JsonIgnore]
    public bool HasSuggestedChanges => Ends.Any(e => e.MarkingCode is 2 or 3);

    [JsonIgnore]
    public bool HasEasaMarkings => Ends.Any(e => e.MarkingCode is 6 or 7);

    [JsonIgnore]
    public bool HasOtherAutomaticMarkings => Ends.Any(e => e.MarkingCode is 4 or 5);

    [JsonIgnore]
    public string RunwaySummary => Runways.Count == 0
        ? "—"
        : string.Join(", ", Runways.Select(r => $"{r.End1.Designator}/{r.End2.Designator}"));

    [JsonIgnore]
    public string CurrentSummary => MarkingSummary(Ends, convert: false);

    [JsonIgnore]
    public string SuggestedSummary => HasSuggestedChanges ? MarkingSummary(Ends, convert: true) : "无需变更";

    [JsonIgnore]
    public string Status
    {
        get
        {
            if (Runways.Count == 0) return "无铺筑跑道";
            if (HasTransparentRunway) return HasSuggestedChanges ? "透明跑道，需检查" : "透明跑道";
            if (HasSuggestedChanges && HasEasaMarkings) return "混合标准，可转换";
            if (HasSuggestedChanges) return "建议转换";
            if (HasOtherAutomaticMarkings) return "其他标准，需检查";
            if (HasEasaMarkings) return "已符合 ICAO/EASA";
            return "无/目视标线";
        }
    }

    private static string MarkingSummary(IEnumerable<RunwayEnd> ends, bool convert)
    {
        var endList = ends.ToList();
        if (endList.Count == 0) return "—";

        var counts = endList
            .Select(e => convert ? AptDatService.ConvertMarkingCode(e.MarkingCode) : e.MarkingCode)
            .GroupBy(code => code)
            .OrderBy(group => group.Key)
            .Select(group => $"{AptDatService.MarkingName(group.Key)} ×{group.Count()}");
        return string.Join("；", counts);
    }
}

public sealed record RunwayRecord(int SurfaceCode, RunwayEnd End1, RunwayEnd End2);

public sealed record RunwayEnd(string Designator, int MarkingCode);

public sealed record ScanProgress(long BytesRead, long TotalBytes, int ChinaAirportCount)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesRead * 100L / TotalBytes, 0, 100);
}

public sealed record BuildProgress(long BytesRead, long TotalBytes, int AirportsWritten)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesRead * 100L / TotalBytes, 0, 100);
}

public sealed class AptDatExportManifest
{
    public int FormatVersion { get; init; } = 1;
    public required string ToolVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string SourceAptDat { get; init; }
    public required long SourceSize { get; init; }
    public required DateTime SourceLastWriteUtc { get; init; }
    public required string SourceSha256 { get; init; }
    public required string OutputSha256 { get; init; }
    public required List<string> AirportRecordIds { get; init; }
    public required List<string> Airports { get; init; }
    public required int RunwayEndsChanged { get; init; }
}

public sealed record AptDatExportResult(
    string ExportDirectory,
    string AptDatPath,
    int AirportsWritten,
    int RunwayEndsChanged,
    string SourceSha256,
    string OutputSha256);