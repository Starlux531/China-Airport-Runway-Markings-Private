using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ChinaRunwayMarkings;

public sealed class AppSettings
{
    public string XPlaneRoot { get; set; } = "";
    public string AptDatPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
}

public sealed class LocalState
{
    private readonly string _directory;
    public LocalState(string? directory = null) => _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChinaRunwayMarkings");

    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(_directory, "settings.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void SaveSettings(AppSettings settings) => Save("settings.json", settings);

    private void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, name);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public List<AirportRecord>? LoadAirports(string path)
    {
        try
        {
            var cache = JsonSerializer.Deserialize<AirportCache>(File.ReadAllText(Path.Combine(_directory, "airports.json")));
            var info = new FileInfo(path);
            if (cache is null || cache.Airports is null || cache.Airports.Any(a => a is null || a.Runways is null) || cache.Version != "0.5.0" || !info.Exists ||
                !Path.GetFullPath(path).Equals(cache.Path, StringComparison.OrdinalIgnoreCase) ||
                info.Length != cache.Length || info.LastWriteTimeUtc != cache.LastWriteUtc) return null;
            foreach (var airport in cache.Airports) airport.Selected = false;
            return cache.Airports;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public void SaveAirports(string path, List<AirportRecord> airports)
    {
        var info = new FileInfo(path);
        Save("airports.json", new AirportCache("0.5.0", info.FullName, info.Length, info.LastWriteTimeUtc, airports));
    }

    public sealed record AirportCache(string Version, string Path, long Length, DateTime LastWriteUtc, List<AirportRecord> Airports);
}

public static class XPlaneLocator
{
    private static readonly string[] AptPaths =
    {
        Path.Combine("Global Scenery", "Global Airports", "Earth nav data", "apt.dat"),
        Path.Combine("Custom Scenery", "Global Airports", "Earth nav data", "apt.dat")
    };

    public static string? AptDatForRoot(string root) => AptPaths.Select(p => Path.Combine(root, p)).FirstOrDefault(File.Exists);

    public static string? RootForAptDat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = Path.GetFullPath(path);
        foreach (var suffix in AptPaths)
            if (full.EndsWith(Path.DirectorySeparatorChar + suffix, StringComparison.OrdinalIgnoreCase))
                return full[..^(suffix.Length + 1)];
        return null;
    }

    public static List<string> Discover(string? savedRoot = null, bool searchDrives = true)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim().Trim('"')));
                if (File.Exists(Path.Combine(full, "X-Plane.exe")) && AptDatForRoot(full) is not null) roots.Add(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        Add(savedRoot);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var version in new[] { "12", "11" })
        {
            var record = Path.Combine(local, $"x-plane_install_{version}.txt");
            try { if (File.Exists(record)) foreach (var line in File.ReadLines(record)) Add(line); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        };
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam) steamRoots.Add(steam);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
        foreach (var steam in steamRoots.ToArray())
        {
            try
            {
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                    steamRoots.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var steam in steamRoots)
            foreach (var version in new[] { "12", "11" }) Add(Path.Combine(steam, "steamapps", "common", "X-Plane " + version));
        foreach (var parent in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) })
            foreach (var version in new[] { "12", "11" }) Add(Path.Combine(parent, "X-Plane " + version));
        // Only search disks when installation records/common locations yielded no installation.
        if (roots.Count == 0 && searchDrives)
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var pending = new Stack<(string Path, int Depth)>();
                pending.Push((drive.RootDirectory.FullName, 0));
                var visited = 0;
                while (pending.TryPop(out var item) && visited++ < 50000)
                {
                    Add(item.Path);
                    if (item.Depth >= 7 || roots.Contains(item.Path)) continue;
                    try
                    {
                        foreach (var dir in new DirectoryInfo(item.Path).EnumerateDirectories())
                        {
                            if ((dir.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 ||
                                dir.Name is "Windows" or "node_modules" or ".git" or "AppData" or "$RECYCLE.BIN") continue;
                            pending.Push((dir.FullName, item.Depth + 1));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        return roots.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
