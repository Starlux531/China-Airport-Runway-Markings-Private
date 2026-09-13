using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChinaRunwayMarkings;

public sealed record BackupRecord(string Target, string OriginalFile, string OriginalSha256,
    string BeforeSha256, string AppliedSha256, DateTimeOffset CreatedAt);

public static class BackupService
{
    public static string BackupDirectory(string target) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, ".ChinaRunwayMarkingsBackups");
    public static bool HasBackups(string target)
    {
        try { return !string.IsNullOrWhiteSpace(target) && Directory.Exists(BackupDirectory(target)) && Directory.EnumerateFiles(BackupDirectory(target), "record.json", SearchOption.AllDirectories).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void EnsureSimulatorStopped()
    {
        var running = false;
        foreach (var process in Process.GetProcessesByName("X-Plane"))
        {
            using (process) { if (!process.HasExited) running = true; }
        }
        if (running) throw new InvalidOperationException("请先完全退出 X-Plane，再应用或恢复标线。");
    }

    public static string Apply(string target, AptDatExportResult export)
    {
        target = Path.GetFullPath(target);
        using var mutex = Acquire(target);
        EnsureSimulatorStopped();
        using var sourceLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (Hash(target) != export.SourceSha256) throw new IOException("源 apt.dat 已变化，请重新扫描并生成修正版后再应用。");
        if (Hash(export.AptDatPath) != export.OutputSha256) throw new IOException("修正版校验失败，文件可能已损坏或被改动。");
        var directory = Path.Combine(BackupDirectory(target), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var before = Path.Combine(directory, "before.dat");
        CopyVerified(target, before, export.SourceSha256);
        // Continue an existing application chain without overwriting its original baseline.
        var previous = ReadRecords(target).FirstOrDefault(r => r.AppliedSha256 == export.SourceSha256);
        var original = previous?.OriginalFile ?? before;
        var originalHash = previous?.OriginalSha256 ?? export.SourceSha256;
        if (Hash(original) != originalHash) throw new IOException("原版备份校验失败，已停止替换。");
        var record = new BackupRecord(target, original, originalHash, export.SourceSha256, export.OutputSha256, DateTimeOffset.UtcNow);
        var recordPath = Path.Combine(directory, "record.json");
        // Durable recovery metadata is written before touching the target.
        WriteDurable(recordPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        ReplaceVerified(export.AptDatPath, target, export.SourceSha256, export.OutputSha256, directory);
        return original;
    }

    public static string Restore(string target)
    {
        target = Path.GetFullPath(target);
        using var mutex = Acquire(target);
        EnsureSimulatorStopped();
        using var targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var currentHash = Hash(target);
        var records = ReadRecords(target).ToList();
        var record = records.FirstOrDefault(r => r.AppliedSha256 == currentHash);
        if (record is null)
        {
            if (records.Any(r => r.OriginalSha256 == currentHash)) return "当前文件已经是原版备份内容，无需重复恢复。";
            throw new IOException("当前 apt.dat 与已应用版本不匹配，可能已被 X-Plane 更新或其他工具修改。为避免覆盖新数据，未执行恢复。备份仍保存在：" + BackupDirectory(target));
        }
        if (Hash(record.OriginalFile) != record.OriginalSha256) throw new IOException("原版备份损坏或被改动，未执行恢复。");
        var recovery = Path.Combine(BackupDirectory(target), "restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recovery);
        ReplaceVerified(record.OriginalFile, target, currentHash, record.OriginalSha256, recovery);
        return "已恢复首次应用前的原版 apt.dat；所有备份仍保留。";
    }

    private static IEnumerable<BackupRecord> ReadRecords(string target)
    {
        var directory = BackupDirectory(target);
        if (!Directory.Exists(directory)) return [];
        var records = new List<BackupRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "record.json", SearchOption.AllDirectories))
        {
            var record = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("备份记录损坏：" + path);
            if (record.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) records.Add(record);
        }
        return records.OrderByDescending(r => r.CreatedAt);
    }

    private static void ReplaceVerified(string source, string target, string expectedBefore, string expectedAfter, string recoveryDirectory)
    {
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".apt-" + Guid.NewGuid().ToString("N") + ".tmp");
        var rollback = Path.Combine(recoveryDirectory, "rollback.dat");
        try
        {
            CopyVerified(source, staging, expectedAfter);
            EnsureSimulatorStopped();
            if (Hash(target) != expectedBefore) throw new IOException("目标文件在操作期间变化，已停止替换。");
            // Same-volume atomic replacement, also preserving the exact replaced file.
            File.Replace(staging, target, rollback);
            if (Hash(target) != expectedAfter)
            {
                File.Replace(rollback, target, null);
                throw new IOException("替换后校验失败，已回退到替换前文件。");
            }
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static void CopyVerified(string source, string destination, string expectedHash)
    {
        using (var input = File.OpenRead(source))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
            output.Flush(true);
        }
        if (Hash(destination) != expectedHash) throw new IOException("文件复制校验失败，未替换目标文件。");
    }

    private static void WriteDurable(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
        file.Flush(true);
    }

    private static IDisposable Acquire(string target)
    {
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
        var mutex = new Mutex(false, "Local\\ChinaRunwayMarkings-" + id);
        try
        {
            if (!mutex.WaitOne(0)) throw new IOException("另一个工具窗口正在处理这个 apt.dat，请等待完成。");
        }
        catch (AbandonedMutexException) { }
        catch { mutex.Dispose(); throw; }
        return new MutexLease(mutex);
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
