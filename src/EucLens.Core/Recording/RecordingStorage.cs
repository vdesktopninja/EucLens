namespace EucLens.Core;

public sealed record RecordingFileInfo(string Path, long Length, DateTimeOffset LastWriteUtc);

/// <summary>The few file operations a recording needs, so storage failures can be simulated in tests.</summary>
public interface IRecordingStorage
{
    void EnsureDirectory(string directory);
    /// <summary>Creates a new file for writing; fails when it exists.</summary>
    Stream Create(string path);
    Stream OpenRead(string path);
    /// <summary>All regular files directly inside the directory (empty when it does not exist).</summary>
    IReadOnlyList<RecordingFileInfo> List(string directory);
    void Delete(string path);
    void Rename(string from, string to);
    /// <summary>Free bytes on the volume of the directory, or null when it cannot be determined (for example some network shares).</summary>
    long? FreeSpace(string directory);
}

public sealed class PhysicalRecordingStorage : IRecordingStorage
{
    public void EnsureDirectory(string directory) => Directory.CreateDirectory(directory);

    public Stream Create(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

    public IReadOnlyList<RecordingFileInfo> List(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return new DirectoryInfo(directory).EnumerateFiles().Select(f => new RecordingFileInfo(f.FullName, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))).ToArray();
    }

    public void Delete(string path) => File.Delete(path);

    public void Rename(string from, string to) => File.Move(from, to, true);

    public long? FreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }
}

/// <summary>Removes old recordings. Only files that match the recording naming pattern in the recording directory are ever touched.</summary>
public static class RecordingRetention
{
    public sealed record Result(int RecordingsDeleted, long BytesFreed, long BytesKept);

    /// <summary>
    /// Deletes recordings older than <paramref name="maxAge"/>, then the oldest recordings until at most <paramref name="allowedBytes"/>
    /// remain. The recording whose short id is <paramref name="activeId"/> is never deleted.
    /// </summary>
    public static Result Apply(IRecordingStorage storage, string directory, long allowedBytes, TimeSpan? maxAge, DateTimeOffset now, string? activeId)
    {
        var groups = storage.List(directory)
            .Select(f => (File: f, Name: Path.GetFileName(f.Path)))
            .Where(x => RecordingFormat.IsRecordingFile(x.Name))
            .Select(x => (x.File, Id: Id(x.Name)))
            .GroupBy(x => x.Id)
            .Select(g => (Id: g.Key, Files: g.Select(x => x.File).ToArray(), Newest: g.Max(x => x.File.LastWriteUtc), Bytes: g.Sum(x => x.File.Length)))
            .OrderBy(g => g.Newest)
            .ToList();
        int deleted = 0; long freed = 0;
        var kept = groups.Sum(g => g.Bytes);
        foreach (var group in groups)
        {
            if (group.Id == activeId) continue;
            var tooOld = maxAge is { } age && now - group.Newest > age;
            if (!tooOld && kept <= allowedBytes) continue;
            try
            {
                foreach (var file in group.Files) storage.Delete(file.Path);
                deleted++; freed += group.Bytes; kept -= group.Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a file in use or protected stays; the next pass retries */ }
        }
        return new(deleted, freed, kept);
    }

    private static string Id(string name) => RecordingFormat.TryParsePart(name, out _, out var id, out _) ? id : RecordingFormat.TryParseMeta(name, out _, out var metaId) ? metaId : "";
}
