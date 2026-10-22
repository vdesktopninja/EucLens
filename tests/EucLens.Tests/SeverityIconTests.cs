using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EucLens.App;
using EucLens.App.Presentation;
using EucLens.App.ViewModels;
using EucLens.Core;
using EucLens.Diagnostics;
using SkiaSharp;
using Xunit;

namespace EucLens.Tests;

/// <summary>
/// Severity icons of the Diagnostics page: state-to-icon mapping, the vector geometry resources, the central style and a real raster
/// of every geometry (SkiaSharp, no window). The WPF rendering itself is verified by the Windows smoke test.
/// </summary>
public sealed class SeverityIconTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static XDocument Xaml(string relative) => XDocument.Load(Path.Combine(Repo(), "src/EucLens.App", relative));

    private sealed class FakeDesktop : IDesktopServices
    {
        public string? SelectExportPath(bool json) => null;
        public bool ConfirmNewRecording() => true;
        public void ApplyTheme(bool dark) { }
        public Task SavePreferencesAsync(Preferences preferences) => Task.CompletedTask;
        public Task<Preferences?> LoadPreferencesAsync() => Task.FromResult<Preferences?>(null);
        public bool CopyText(string text) => true;
    }

    private static DiagnosticFinding Finding(string id, Severity severity, int detected, int? recovered = null) =>
        new(id, DiagnosticDomain.Connection, severity, id, $"{id} description", T0.AddSeconds(detected), T0.AddSeconds(50), recovered is { } r ? T0.AddSeconds(r) : null,
            recovered is null ? null : "Recovered for test.", 60, 58, 60, [new("Window P95", "165 ms", null, "≥ 150 ms warning")], "Rule.", [], [], "Limitation.");

    private static DiagnosticsViewModel Controlled()
    {
        var vm = new DiagnosticsViewModel(new FakeDesktop());
        vm.Apply(new DiagnosticReport(HealthStatus.Critical, [], Assessments:
        [
            new(DiagnosticDomain.Connection, HealthState.Critical, AssessmentCoverage.Full, "", "", 60),
            new(DiagnosticDomain.Graphics, HealthState.Warning, AssessmentCoverage.Full, "", "", 60),
            new(DiagnosticDomain.Guest, HealthState.Healthy, AssessmentCoverage.Full, "", "", 60),
        ], Findings:
        [
            Finding("info", Severity.Info, 1), Finding("warn", Severity.Warning, 10), Finding("crit", Severity.Critical, 20), Finding("recovered", Severity.Warning, 2, recovered: 30),
        ], Hypotheses: [], Inputs: []), DiagnosticsPresentationTests.Snap(T0));
        return vm;
    }

    // ---------- mapping ----------

    [Fact]
    public void FindingSeverityMapsToItsIconAndRecoveredAlwaysWins()
    {
        Assert.Equal(SeverityKind.Info, SeverityKinds.ForFinding(Severity.Info, true));
        Assert.Equal(SeverityKind.Warning, SeverityKinds.ForFinding(Severity.Warning, true));
        Assert.Equal(SeverityKind.Critical, SeverityKinds.ForFinding(Severity.Critical, true));
        foreach (var severity in Enum.GetValues<Severity>()) Assert.Equal(SeverityKind.Recovered, SeverityKinds.ForFinding(severity, false));
    }

    [Theory]
    [InlineData("Healthy", SeverityKind.Healthy)]
    [InlineData("Warning", SeverityKind.Warning)]
    [InlineData("Critical", SeverityKind.Critical)]
    [InlineData("Recovered", SeverityKind.Recovered)]
    [InlineData("Informational", SeverityKind.Info)]
    [InlineData("Info", SeverityKind.Info)]
    [InlineData("Insufficient Data", SeverityKind.NoData)]
    [InlineData("Neutral", SeverityKind.NoData)]
    [InlineData("", SeverityKind.NoData)]
    [InlineData(null, SeverityKind.NoData)]
    public void StateTextMapsToItsIcon(string? state, SeverityKind expected) => Assert.Equal(expected, SeverityKinds.ForState(state));

    [Fact]
    public void EveryKindHasItsOwnGeometryKey()
    {
        Assert.Equal(6, SeverityKinds.All.Count);
        Assert.Equal(SeverityKinds.All.Count, SeverityKinds.All.Select(SeverityKinds.GeometryKey).Distinct().Count());
        Assert.Equal("SeverityGeometryCritical", SeverityKinds.GeometryKey(SeverityKind.Critical));
    }

    [Fact]
    public void ViewModelsExposeTheIconKindsAndKeepSeverityTextAndColourKeys()
    {
        var vm = Controlled();
        vm.SelectedFilter = vm.Filters.Single(f => f.Kind == FindingFilterKind.All);
        FindingItem Item(string id) => vm.Findings.Single(f => f.RuleId == id);
        Assert.Equal("Info", Item("info").IconKind);
        Assert.Equal("Warning", Item("warn").IconKind);
        Assert.Equal("Critical", Item("crit").IconKind);
        Assert.Equal("Recovered", Item("recovered").IconKind);
        // Classification and the text labels are untouched: the icon is an addition, never a replacement.
        Assert.Equal(new[] { "Info", "Warning", "Critical", "Warning" }, new[] { "info", "warn", "crit", "recovered" }.Select(id => Item(id).SeverityLabel));
        Assert.Equal(new[] { "Informational", "Active", "Active", "Recovered" }, new[] { "info", "warn", "crit", "recovered" }.Select(id => Item(id).State));
        Assert.Equal(new[] { "Neutral", "Warning", "Critical", "Neutral" }, new[] { "info", "warn", "crit", "recovered" }.Select(id => Item(id).Marker));

        Assert.Equal("Critical", vm.HeadlineIcon);
        string Tile(DiagnosticDomain d) => vm.Domains.Single(x => x.DomainKey == d).StateIcon;
        Assert.Equal(new[] { "Critical", "Warning", "Healthy" }, new[] { Tile(DiagnosticDomain.Connection), Tile(DiagnosticDomain.Graphics), Tile(DiagnosticDomain.Guest) });
        Assert.Equal("NoData", new DiagnosticsViewModel(new FakeDesktop()).HeadlineIcon);
    }

    [Fact]
    public void IconPropertiesChangeWithTheirState()
    {
        var item = new DomainHealthItem(DiagnosticDomain.Connection, "Connection Health");
        var raised = new List<string?>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("NoData", item.StateIcon);
        item.State = "Warning";
        Assert.Equal("Warning", item.StateIcon);
        Assert.Contains(nameof(DomainHealthItem.StateIcon), raised);
    }

    // ---------- geometry resources ----------

    private static Dictionary<SeverityKind, string> Geometries() => SeverityKinds.All.ToDictionary(k => k,
        k => Xaml("Themes/Foundations.xaml").Root!.Elements().Single(e => e.Name.LocalName == "StreamGeometry" && (string?)e.Attribute(X + "Key") == SeverityKinds.GeometryKey(k)).Value.Trim());

    [Fact]
    public void EveryGeometryIsPopulatedClosedAndInsideTheIconGrid()
    {
        foreach (var (kind, data) in Geometries())
        {
            Assert.StartsWith("F0 M", data); // even-odd: the symbol is a cut-out, or a fill inside a ring
            Assert.Matches(@"^[FMLHVAZ0-9 ,.\-]+$", data);
            var figures = Regex.Matches(data, "M").Count;
            Assert.True(figures >= 2, $"{kind} has no symbol figure.");
            Assert.Equal(figures, Regex.Matches(data, "Z").Count);
            var numbers = Regex.Matches(Regex.Replace(data, @"A[\d.,]+ \d [01] [01] ", "A "), @"-?\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            Assert.NotEmpty(numbers);
            Assert.All(numbers, n => Assert.InRange(n, 0, 16));
        }
        Assert.Equal(Geometries().Count, Geometries().Values.Distinct().Count());
    }

    [Fact]
    public void SeverityIconIsOneCentralStyleWithAShapePerKindAndNoFontDependency()
    {
        var controls = Xaml("Themes/Controls.xaml").Root!;
        var style = controls.Elements().Single(e => (string?)e.Attribute(X + "Key") == "SeverityIcon");
        Assert.Equal("Path", (string?)style.Attribute("TargetType"));
        string Setter(XElement scope, string property) => (string)scope.Elements().Single(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property).Attribute("Value")!;

        var tokens = Xaml("Themes/Foundations.xaml").Root!.Elements().ToDictionary(e => (string?)e.Attribute(X + "Key") ?? "", e => e.Value.Trim());
        var size = double.Parse(tokens["SeverityIconSize"], CultureInfo.InvariantCulture);
        Assert.InRange(size, 14, 16);
        Assert.Equal("{StaticResource SeverityIconSize}", Setter(style, "Width"));
        Assert.Equal("{StaticResource SeverityIconSize}", Setter(style, "Height"));
        Assert.Equal("None", Setter(style, "Stretch")); // the 16 x 16 grid is drawn 1:1, so every kind keeps its optical size

        var triggers = style.Elements().Single(e => e.Name.LocalName == "Style.Triggers").Elements().ToDictionary(t => (string)t.Attribute("Value")!);
        Assert.Equal(["Info", "Warning", "Critical", "Healthy", "Recovered"], triggers.Keys.ToArray());
        var brush = new Dictionary<SeverityKind, string>
        {
            [SeverityKind.Info] = "AccentBrush", [SeverityKind.Warning] = "WarningBrush", [SeverityKind.Critical] = "CriticalBrush",
            [SeverityKind.Healthy] = "SuccessBrush", [SeverityKind.Recovered] = "MutedBrush", [SeverityKind.NoData] = "MutedBrush"
        };
        foreach (var kind in SeverityKinds.All)
        {
            var scope = kind == SeverityKind.NoData ? style : triggers[kind.ToString()];
            Assert.Equal($"{{StaticResource {SeverityKinds.GeometryKey(kind)}}}", Setter(scope, "Data"));
            var fill = scope.Elements().Where(e => (string?)e.Attribute("Property") == "Fill").Select(e => (string)e.Attribute("Value")!).SingleOrDefault()
                ?? Setter(style, "Fill"); // a kind without its own colour inherits the default
            Assert.Equal($"{{DynamicResource {brush[kind]}}}", fill);
        }
        // Neutral kinds use neutral grey, never a status colour.
        Assert.Equal("{DynamicResource MutedBrush}", Setter(style, "Fill"));

        var all = Xaml("Themes/Controls.xaml").ToString() + Xaml("Themes/Foundations.xaml");
        Assert.DoesNotContain("StateMarker", all);
        var region = Regex.Match(style.ToString(), "<Style .*", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("FontFamily", region);
        Assert.DoesNotContain("IconFont", region);
    }

    [Fact]
    public void DiagnosticsUsesTheSharedIconEverywhereASeverityOrStateIsShown()
    {
        var doc = Xaml("Views/DiagnosticsView.xaml");
        var text = doc.ToString();
        Assert.DoesNotContain("StateMarker", text);
        Assert.DoesNotContain("IconFont", text);
        Assert.DoesNotContain("Segoe", text);
        Assert.DoesNotMatch(@"[←-⯿-]|[\uD83C-\uD83E][\uDC00-\uDFFF]", text); // no symbol, private-use or emoji characters

        var icons = doc.Descendants().Where(e => e.Name.LocalName == "Path").ToArray();
        Assert.Equal(["{Binding StateIcon}", "{Binding HeadlineIcon}", "{Binding IconKind}", "{Binding IconKind}"],
            icons.Select(i => (string)i.Attribute("Tag")!).OrderBy(t => t == "{Binding StateIcon}" ? 0 : t == "{Binding HeadlineIcon}" ? 1 : 2).ToArray());
        Assert.All(icons, i =>
        {
            Assert.Equal("{StaticResource SeverityIcon}", (string)i.Attribute("Style")!);
            // The label is the icon's sibling, so severity is stated in words as well.
            Assert.Contains(i.Parent!.Elements(), e => e.Name.LocalName == "TextBlock");
        });
    }

    // ---------- colour ----------

    private static double Luminance(string hex)
    {
        static double Channel(int v) { var s = v / 255d; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var c = Convert.ToInt32(hex.TrimStart('#'), 16);
        return 0.2126 * Channel(c >> 16 & 255) + 0.7152 * Channel(c >> 8 & 255) + 0.0722 * Channel(c & 255);
    }

    [Theory]
    [InlineData("Light.xaml")]
    [InlineData("Dark.xaml")]
    public void IconColoursHaveAtLeastThreeToOneContrastOnEverySurface(string theme)
    {
        var colours = Xaml("Themes/" + theme).Root!.Elements().Where(e => e.Name.LocalName == "Color").ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());
        foreach (var icon in new[] { "AccentColor", "WarningColor", "CriticalColor", "SuccessColor", "MutedColor" })
        foreach (var surface in new[] { "SurfaceColor", "WindowColor", "ElevatedColor" })
        {
            var (a, b) = (Luminance(colours[icon]), Luminance(colours[surface]));
            var ratio = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
            Assert.True(ratio >= 3, $"{theme}: {icon} on {surface} has {ratio:0.0}:1 contrast, below 3:1.");
        }
    }

    // ---------- raster ----------

    /// <summary>Alpha mask of a geometry at the given pixel size (the 16 DIP grid scaled), using the even-odd rule as WPF does for "F0".</summary>
    private static byte[] Mask(string data, int px)
    {
        using var path = SKPath.ParseSvgPathData(data["F0 ".Length..]);
        path.FillType = SKPathFillType.EvenOdd;
        using var bitmap = new SKBitmap(new SKImageInfo(px, px, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(px / 16f);
            canvas.DrawPath(path, paint);
        }
        return bitmap.Pixels.Select(p => p.Alpha).ToArray();
    }

    [Theory]
    [InlineData(16)] // 100 %
    [InlineData(20)] // 125 %
    [InlineData(24)] // 150 %
    [InlineData(64)] // enlarged capture
    public void EveryIconDrawsAVisibleDistinctShapeWithItsSymbol(int px)
    {
        var masks = Geometries().ToDictionary(g => g.Key, g => Mask(g.Value, px));
        foreach (var (kind, alpha) in masks)
        {
            var coverage = alpha.Count(a => a > 128) / (double)alpha.Length;
            Assert.InRange(coverage, 0.18, 0.85);
            Assert.True(alpha.Count(a => a == 255) >= 20, $"{kind}: too few opaque pixels at {px} px.");
            int lo = (int)Math.Round(px * 5 / 16d), hi = (int)Math.Round(px * 11 / 16d), total = 0, hole = 0;
            for (var y = lo; y < hi; y++) for (var x = lo; x < hi; x++) { total++; if (alpha[y * px + x] < 128) hole++; }
            var holes = hole / (double)total;
            // Disc and triangle: the symbol is a transparent cut-out. Ring kinds: the symbol is filled inside an empty ring.
            if (kind is SeverityKind.Recovered or SeverityKind.NoData) Assert.InRange(holes, 0.30, 0.92); else Assert.True(holes >= 0.08, $"{kind}: symbol not visible at {px} px ({holes:P0}).");
        }
        foreach (var a in masks.Keys) foreach (var b in masks.Keys.Where(k => k > a))
        {
            var differing = Enumerable.Range(0, px * px).Count(i => Math.Abs(masks[a][i] - masks[b][i]) > 64) / (double)(px * px);
            Assert.True(differing >= 0.04, $"{a} and {b} look alike at {px} px ({differing:P1} of the pixels differ).");
        }
    }

    [Fact]
    public void WindowsSmokeTestChecksGeometryPixelsColourAndLabelsOfTheIcons()
    {
        var code = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/DesktopValidation.cs"));
        // Run on every Diagnostics visit (24 layout cases) and once per theme with controlled data for all six kinds.
        Assert.Contains("CheckSeverityIcons(window, vm)", code);
        Assert.Contains("CheckSeveritySpecimens(window, directory, dark)", code);
        // Populated geometry of the right kind, not merely a container.
        Assert.Contains("icon.Data.IsEmpty()", code);
        Assert.Contains("ReferenceEquals(icon.Data, expected)", code);
        // Real pixels: coverage, visible symbol, theme colour, pairwise distinct shapes, at 100 / 125 / 150 %.
        Assert.Contains("new[] { 1d, 1.25, 1.5, 4d }", code);
        Assert.Contains("the symbol is not visible", code);
        Assert.Contains("are not the {brushName} colour", code);
        Assert.Contains("look alike", code);
        Assert.Contains("severity-icons-{theme}-", code);
        // Contrast and the text label beside the icon.
        Assert.Contains("below 3:1", code);
        Assert.Contains("has no text label next to it", code);
        Assert.Contains("f.IconKind == \"Info\"", File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/App.xaml.cs")));
    }
}
