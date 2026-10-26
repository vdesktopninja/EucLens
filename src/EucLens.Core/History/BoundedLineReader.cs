using System.Text;

namespace EucLens.Core;

/// <summary>Raised when a part needs more than its reading budget; the reader stops and the analysis is reported as partial.</summary>
internal sealed class HistoryLimitException(string message) : Exception(message);

/// <summary>
/// Reads UTF-8 lines from a stream (a compressed part is decompressed as it is read) without ever holding more than <paramref name="maximumLineBytes"/> of one line:
/// a longer line is skipped while it streams past and reported, so a file without line breaks cannot make the reader allocate a string of the size of the file.
/// The number of bytes read is limited, so a small compressed file that expands enormously cannot keep the reader busy without end, and the cancellation token is
/// checked whenever more data is fetched.
/// </summary>
internal sealed class BoundedLineReader(Stream stream, int maximumLineBytes, long maximumBytes, CancellationToken cancellationToken)
{
    private readonly byte[] buffer = new byte[64 * 1024];
    private byte[] line = new byte[1024];
    private int position, length;

    /// <summary>Bytes read from the stream so far (after decompression).</summary>
    public long BytesRead { get; private set; }

    /// <returns>False at the end of the stream. For a line longer than the limit, <paramref name="text"/> is null and <paramref name="tooLong"/> is true.</returns>
    public bool TryReadLine(out string? text, out bool tooLong)
    {
        var count = 0; tooLong = false; var any = false;
        while (true)
        {
            if (position == length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                length = stream.Read(buffer, 0, buffer.Length); position = 0;
                if (length == 0) { if (!any) { text = null; return false; } break; }
                BytesRead += length;
                if (BytesRead > maximumBytes) throw new HistoryLimitException($"more than {maximumBytes >> 20:N0} MiB were read from one part");
            }
            any = true;
            var span = buffer.AsSpan(position, length - position);
            var newline = span.IndexOf((byte)'\n');
            var take = newline < 0 ? span.Length : newline;
            if (!tooLong)
            {
                if (count + take > maximumLineBytes) tooLong = true;
                else
                {
                    if (count + take > line.Length) Array.Resize(ref line, Math.Min(maximumLineBytes, Math.Max(line.Length * 2, count + take)));
                    span[..take].CopyTo(line.AsSpan(count)); count += take;
                }
            }
            position += newline < 0 ? take : take + 1;
            if (newline >= 0) break;
        }
        if (tooLong) { text = null; return true; }
        if (count > 0 && line[count - 1] == (byte)'\r') count--;
        text = Encoding.UTF8.GetString(line, 0, count);
        return true;
    }
}
