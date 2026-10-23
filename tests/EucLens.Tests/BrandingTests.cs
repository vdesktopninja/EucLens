using System.Buffers.Binary;
using SkiaSharp;
using Xunit;

namespace EucLens.Tests;

/// <summary>Integrity of the generated application and tray icons (tools/branding/generate_icons.py output).</summary>
public sealed class BrandingTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static string Assets(params string[] parts) => Path.Combine([Repo(), "src/EucLens.App/Assets", .. parts]);

    private sealed record Entry(int Size, bool IsPng, byte[] Data);

    private static List<Entry> ReadIco(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        var entries = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var d = bytes.AsSpan(6 + i * 16);
            var size = d[0] == 0 ? 256 : d[0];
            Assert.Equal(size, d[1] == 0 ? 256 : d[1]);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(d[6..]));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[12..]);
            Assert.True(offset + length <= bytes.Length, "ICO entry outside the file.");
            var data = bytes.AsSpan(offset, length).ToArray();
            entries.Add(new(size, data.Length > 8 && data[0] == 0x89 && data[1] == 'P', data));
        }
        return entries;
    }

    /// <summary>RGBA pixels (row 0 on top) of an ICO entry, either PNG or 32 bpp BMP.</summary>
    private static SKColor[] Pixels(Entry e)
    {
        if (e.IsPng)
        {
            using var bitmap = SKBitmap.Decode(e.Data);
            Assert.Equal(e.Size, bitmap.Width);
            Assert.Equal(e.Size, bitmap.Height);
            return bitmap.Pixels;
        }
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(e.Data));
        Assert.Equal(e.Size, BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(4)));
        Assert.Equal(e.Size * 2, BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(8))); // XOR + AND mask
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(e.Data.AsSpan(14)));
        var pixels = new SKColor[e.Size * e.Size];
        for (var y = 0; y < e.Size; y++)
            for (var x = 0; x < e.Size; x++)
            {
                var o = 40 + ((e.Size - 1 - y) * e.Size + x) * 4;
                pixels[y * e.Size + x] = new(e.Data[o + 2], e.Data[o + 1], e.Data[o], e.Data[o + 3]);
            }
        return pixels;
    }

    [Fact]
    public void ApplicationIconHasEveryWindowsSizeWithIndividuallyDrawnSmallEntries()
    {
        var entries = ReadIco(Assets("EucLens.ico"));
        Assert.Equal(new[] { 16, 20, 24, 32, 48, 64, 128, 256 }, entries.Select(e => e.Size).ToArray());
        Assert.All(entries, e => Assert.Equal(e.Size > 48, e.IsPng)); // classic layout: BMP up to 48 px, PNG above
        foreach (var e in entries)
        {
            var pixels = Pixels(e);
            var opaque = pixels.Count(p => p.Alpha == 255);
            Assert.InRange(opaque / (double)pixels.Length, 0.6, 1.0); // a tile, not a faint drawing
            Assert.Contains(pixels, p => p is { Alpha: 255, Blue: > 150, Red: < 60 });                 // deep blue tile
            Assert.Contains(pixels, p => p is { Alpha: 255, Red: > 240, Green: > 240, Blue: > 240 });  // white monitor and magnifier
            Assert.Contains(pixels, p => p is { Alpha: 255, Red: < 80, Green: > 180, Blue: > 220 });   // cyan bars
        }
        // The 16 px entry is its own drawing: crisp whole-pixel strokes rather than a blurred reduction of a large image.
        var small = Pixels(entries[0]);
        Assert.True(small.Count(p => p.Alpha == 255 && p.Red > 240 && p.Green > 240 && p.Blue > 240) >= 30);
        Assert.True(small.Count(p => p.Alpha is > 0 and < 255) <= 4, "16 px icon should not rely on anti-aliasing.");
    }

    public static IEnumerable<object[]> TrayStates => new[] { "idle", "monitoring", "recording", "recording-warning", "warning", "paused" }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(TrayStates))]
    public void TrayIconsHaveEveryTraySize(string state)
    {
        var entries = ReadIco(Assets("Tray", $"tray-{state}.ico"));
        Assert.Equal(new[] { 16, 20, 24, 32 }, entries.Select(e => e.Size).ToArray());
        Assert.All(entries, e => Assert.Contains(Pixels(e), p => p.Alpha == 255));
    }

    private static SKColor[] Tray(string state, int size) => Pixels(ReadIco(Assets("Tray", $"tray-{state}.ico")).Single(e => e.Size == size));

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void TrayStatesDifferInShapeAndNotJustColourAtEveryTraySize(int size)
    {
        var states = TrayStates.Select(s => (string)s[0]).ToArray();
        var images = states.ToDictionary(s => s, s => Tray(s, size));
        // Compare silhouettes (alpha-independent: where the pixel is neither the tile blue nor white nor cyan) and also pure grey-scale shape.
        foreach (var a in states) foreach (var b in states.Where(s => string.CompareOrdinal(s, a) > 0))
        {
            var differing = Enumerable.Range(0, size * size).Count(i => Luma(images[a][i]) - Luma(images[b][i]) is > 40 or < -40) / (double)(size * size);
            Assert.True(differing >= 0.02, $"{a} and {b} tray icons look alike at {size} px in grey-scale ({differing:P1} of the pixels differ).");
        }
        static double Luma(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
    }

    [Fact]
    public void EachBadgeUsesItsOwnColourInTheLowerRight()
    {
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            int Count(string state, Func<SKColor, bool> test)
            {
                var edge = size / 2;
                var pixels = Tray(state, size);
                var n = 0;
                for (var y = size - edge; y < size; y++) for (var x = size - edge; x < size; x++) if (test(pixels[y * size + x])) n++;
                return n;
            }
            Assert.True(Count("monitoring", c => c is { Alpha: 255, Green: > 150, Red: < 90, Blue: < 130 }) >= size / 3, $"green at {size}");
            Assert.True(Count("recording", c => c is { Alpha: 255, Red: > 190, Green: < 90, Blue: < 90 }) >= size, $"red at {size}");
            Assert.True(Count("recording-warning", c => c is { Alpha: 255, Red: > 190, Green: < 90, Blue: < 90 }) >= size, $"red with warning at {size}");
            Assert.True(Count("warning", c => c is { Alpha: 255, Red: > 220, Green: > 130, Blue: < 80 }) >= size / 3, $"amber at {size}");
            Assert.True(Count("paused", c => c is { Alpha: 255, Red: > 130, Red: < 175, Green: > 135, Green: < 180, Blue: > 140 }) >= size / 3, $"grey at {size}");
            Assert.Equal(0, Count("idle", c => c.Alpha == 255 && (c.Red > 190 || c.Green > 150) && c.Blue < 100)); // brand icon has no status colour
            // A recording icon never shows the monitoring green, and a monitoring icon never shows recording red.
            Assert.Equal(0, Count("recording", c => c is { Alpha: 255, Green: > 150, Red: < 90, Blue: < 130 }));
            Assert.Equal(0, Count("monitoring", c => c is { Alpha: 255, Red: > 190, Green: < 90, Blue: < 90 }));
        }
        // The combined state keeps the recording badge and adds the amber triangle in the opposite corner.
        var combined = Tray("recording-warning", 32);
        Assert.Contains(Enumerable.Range(0, 16 * 32).Select(i => combined[i]), c => c is { Alpha: 255, Red: > 220, Green: > 130, Blue: < 80 });
    }

    [Fact]
    public void VectorMasterAndPreviewsAreInTheRepositoryAndTheProjectUsesTheIcon()
    {
        var svg = File.ReadAllText(Path.Combine(Repo(), "assets/branding/euclens-icon.svg"));
        Assert.Contains("viewBox=\"0 0 256 256\"", svg);
        Assert.Contains("#0B4DA2", svg); Assert.Contains("#29D0F0", svg);
        Assert.DoesNotContain("<text", svg);          // no text in the icon
        Assert.DoesNotContain("linearGradient", svg); // no decorative gradients
        foreach (var preview in new[] { "euclens-icon-256.png", "euclens-icon-512.png", "application-icon-sizes-light.png", "application-icon-sizes-dark.png", "tray-states-light.png", "tray-states-dark.png" })
            Assert.True(new FileInfo(Path.Combine(Repo(), "assets/branding/previews", preview)).Length > 1000, preview);
        Assert.True(File.Exists(Path.Combine(Repo(), "tools/branding/generate_icons.py")));

        var project = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/EucLens.App.csproj"));
        Assert.Contains("<ApplicationIcon>Assets\\EucLens.ico</ApplicationIcon>", project);
        Assert.Contains("Assets\\Tray\\*.ico", project);
        Assert.Contains("Icon=\"Assets/EucLens.ico\"", File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/MainWindow.xaml")));
    }
}
