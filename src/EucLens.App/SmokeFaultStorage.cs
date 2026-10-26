using System.IO;
using EucLens.Core;

namespace EucLens.App;

/// <summary>
/// Used only by --smoke-test: the real file storage with a switch that makes every write and flush fail with "disk full". It lets the Windows validation
/// prove through the actual tray action that a marker is never confirmed when the disk refuses it. The streams are real <see cref="FileStream"/>s, so the
/// writer takes exactly the same durable path (including the flush to disk) as in normal use.
/// </summary>
internal sealed class SmokeFaultStorage : IRecordingStorage
{
    public static volatile bool FailWrites;
    private readonly PhysicalRecordingStorage inner = new();

    private static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    public void EnsureDirectory(string directory) => inner.EnsureDirectory(directory);
    public Stream Create(string path) => new FaultFileStream(path);
    public Stream OpenRead(string path) => inner.OpenRead(path);
    public IReadOnlyList<RecordingFileInfo> List(string directory) => inner.List(directory);
    public void Delete(string path) => inner.Delete(path);
    public void Rename(string from, string to) => inner.Rename(from, to);
    public long? FreeSpace(string directory) => inner.FreeSpace(directory);

    private sealed class FaultFileStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.SequentialScan)
    {
        public override void Write(byte[] buffer, int offset, int count) { if (FailWrites) throw DiskFull(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (FailWrites) throw DiskFull(); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => FailWrites ? ValueTask.FromException(DiskFull()) : base.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => FailWrites ? Task.FromException(DiskFull()) : base.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() { if (FailWrites) throw DiskFull(); base.Flush(); }
        public override void Flush(bool flushToDisk) { if (FailWrites) throw DiskFull(); base.Flush(flushToDisk); }
        public override Task FlushAsync(CancellationToken cancellationToken) => FailWrites ? Task.FromException(DiskFull()) : base.FlushAsync(cancellationToken);
    }
}
