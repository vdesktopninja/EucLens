using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;

namespace EucLens.Tests;

/// <summary>
/// The application's typography is one shared system: a font stack with a fallback, a size scale that meets the readability targets, no per-control font families or literal sizes, and text
/// colours that reach WCAG AA contrast on the surfaces they are drawn on, in both themes. WPF rendering, the resolved font and clipping are validated on Windows by the smoke test.
/// </summary>
public sealed class TypographyTests(ITestOutputHelper output)
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../.."));
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XDocument Theme(string name) => XDocument.Load(Path.Combine(Repo(), "src/EucLens.App/Themes", name + ".xaml"));
    private static Dictionary<string, string> Resources(string name) =>
        Theme(name).Root!.Elements().Where(e => e.Attribute(X + "Key") is not null).ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim());
    private static double Size(string key) => double.Parse(Resources("Foundations")[key], CultureInfo.InvariantCulture);

    // ---------- the font stack ----------

    [Fact]
    public void TheBodyFontIsSegoeUIVariableWithSegoeUIAsTheFallbackAndNoFontIsBundled()
    {
        var fonts = Resources("Foundations");
        Assert.Equal("Segoe UI Variable Text, Segoe UI", fonts["BodyFont"]);
        Assert.Equal("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI", fonts["DisplayFont"]);
        var project = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/EucLens.App.csproj"));
        Assert.DoesNotMatch(new Regex(@"\.(ttf|otf|ttc|woff2?)", RegexOptions.IgnoreCase), project);                  // no font file is embedded or redistributed
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(Repo(), "src"), "*.*", SearchOption.AllDirectories), f => Regex.IsMatch(f, @"\.(ttf|otf|ttc|woff2?)$", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void NoViewOrStyleSetsItsOwnFontFamilyOrALiteralFontSize()
    {
        var app = Path.Combine(Repo(), "src/EucLens.App");
        foreach (var file in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var xaml = File.ReadAllText(file); var name = Path.GetFileName(file);
            foreach (Match m in Regex.Matches(xaml, @"FontFamily=""([^""]*)"""))
                Assert.True(m.Groups[1].Value is "{StaticResource BodyFont}" or "{StaticResource DisplayFont}" or "{StaticResource IconFont}", $"{name}: font family {m.Groups[1].Value} is not a shared typography resource.");
            foreach (Match m in Regex.Matches(xaml, @"FontSize=""([^""]*)"""))
                Assert.True(m.Groups[1].Value.StartsWith("{StaticResource ", StringComparison.Ordinal) && m.Groups[1].Value.EndsWith("FontSize}", StringComparison.Ordinal), $"{name}: font size {m.Groups[1].Value} is a literal, not a typography token.");
            foreach (Match m in Regex.Matches(xaml, @"<Setter Property=""FontSize"" Value=""([^""]*)"""))
                Assert.True(m.Groups[1].Value.StartsWith("{StaticResource ", StringComparison.Ordinal), $"{name}: a style sets the literal font size {m.Groups[1].Value}.");
            foreach (Match m in Regex.Matches(xaml, @"<Setter Property=""FontFamily"" Value=""([^""]*)"""))
                Assert.True(m.Groups[1].Value.StartsWith("{StaticResource ", StringComparison.Ordinal), $"{name}: a style sets the literal font family {m.Groups[1].Value}.");
        }
    }

    [Fact]
    public void TheWindowStyleAndEveryPopupSurfaceCarryTheSharedFontBecauseAPopupDoesNotInheritItFromTheWindow()
    {
        var controls = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Themes/Controls.xaml"));
        Assert.Contains("<Style TargetType=\"Window\"><Setter Property=\"UseLayoutRounding\" Value=\"True\" /><Setter Property=\"SnapsToDevicePixels\" Value=\"True\" /><Setter Property=\"FontFamily\" Value=\"{StaticResource BodyFont}\" /><Setter Property=\"FontSize\" Value=\"{StaticResource BodyFontSize}\" />", controls);
        Assert.Contains("<Style TargetType=\"ToolTip\"><Setter Property=\"FontFamily\" Value=\"{StaticResource BodyFont}\" /><Setter Property=\"FontSize\" Value=\"{StaticResource TooltipFontSize}\" />", controls);
        Assert.Contains("<Style x:Key=\"Heading\" TargetType=\"TextBlock\" BasedOn=\"{StaticResource {x:Type TextBlock}}\"><Setter Property=\"FontFamily\" Value=\"{StaticResource DisplayFont}\" />", controls);
    }

    // ---------- the scale ----------

    [Theory]
    [InlineData("HeadingFontSize", 22, 24)]        // main page title, semibold
    [InlineData("SectionFontSize", 18, 18)]        // section heading, semibold
    [InlineData("FindingTitleFontSize", 15, 16)]   // finding title, semibold
    [InlineData("BodyFontSize", 14, 16)]           // body text and buttons: at least 14
    [InlineData("ReadingFontSize", 14, 16)]
    [InlineData("MetaFontSize", 12, 13)]           // secondary labels
    [InlineData("LabelFontSize", 12, 13)]
    [InlineData("MetricFontSize", 22, 26)]         // key diagnostic values
    [InlineData("TooltipFontSize", 13, 16)]        // tooltips: at least 13
    public void EachSizeIsInsideItsTarget(string key, double minimum, double maximum) => Assert.InRange(Size(key), minimum, maximum);

    [Fact]
    public void TheScaleIsOrderedSoTheHierarchyReadsAtAGlance()
    {
        Assert.True(Size("HeadingFontSize") > Size("SectionFontSize"));
        Assert.True(Size("SectionFontSize") >= Size("StateFontSize"));
        Assert.True(Size("StateFontSize") > Size("ValueFontSize"));
        Assert.True(Size("ValueFontSize") > Size("FindingTitleFontSize"));
        Assert.True(Size("FindingTitleFontSize") > Size("BodyFontSize"));
        Assert.True(Size("BodyFontSize") > Size("MetaFontSize"));
        Assert.True(Size("MetricFontSize") > Size("ValueFontSize"));
        Assert.Equal(Size("BodyFontSize"), Size("ReadingFontSize"));
        Assert.True(Size("ReadingLineHeight") >= Size("ReadingFontSize") * 1.35);                               // room between lines
    }

    [Fact]
    public void TitlesAndKeyValuesAreSemiboldAndNumbersAreTabular()
    {
        var controls = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Themes/Controls.xaml"));
        foreach (var style in new[] { "Heading", "Section", "ValueText", "MetricValue", "SectionLabel" })
        {
            var m = Regex.Match(controls, $@"<Style x:Key=""{style}""[^>]*>(.*?)</Style>", RegexOptions.Singleline);
            Assert.True(m.Success && m.Value.Contains("FontWeight\" Value=\"SemiBold\"", StringComparison.Ordinal), $"{style} is not semibold.");
        }
        foreach (var style in new[] { "ValueText", "MetricValue" })
            Assert.Contains("Typography.NumeralAlignment\" Value=\"Tabular\"", Regex.Match(controls, $@"<Style x:Key=""{style}""[^>]*>(.*?)</Style>", RegexOptions.Singleline).Value);
        Assert.Contains("Typography.NumeralAlignment=\"Tabular\" FontWeight=\"SemiBold\" Margin=\"0,4\"", controls);   // the metric tile value
        var history = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views/SessionHistoryView.xaml"));
        Assert.True(Regex.Matches(history, "Typography.NumeralAlignment=\"Tabular\"").Count >= 30, "times, durations and values on the History page use tabular figures");
    }

    [Fact]
    public void NothingDrawnByTheCustomControlsIsSmallerThanTheSecondaryLabelSize()
    {
        foreach (var file in new[] { "FindingsTimeline.cs", "HistoryNavigation.cs" })
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Presentation", file)), @"Text\([^;]*?,\s*(\d+(?:\.\d+)?),\s*\w+Brush\)"))
                Assert.True(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 12, $"{file}: text drawn at {m.Groups[1].Value} DIPs");
        Assert.True(new EucLens.App.ViewModels.ChartData("t", "u", 10).LabelSize >= 12);
    }

    // ---------- keyboard and screen reader ----------

    [Fact]
    public void EveryInteractiveControlStyleDrawsAVisibleKeyboardFocusIndicator()
    {
        var controls = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Themes/Controls.xaml"));
        string Body(string pattern) { var m = Regex.Match(controls, pattern, RegexOptions.Singleline); Assert.True(m.Success, pattern); return m.Value; }
        foreach (var (name, pattern, trigger) in new[]
        {
            ("Button", @"<Style TargetType=""Button"">.*?</Style>", "IsKeyboardFocused"),
            ("ComboBox", @"<Style TargetType=""ComboBox"">.*?</Style>", "IsKeyboardFocusWithin"),
            ("ComboBoxItem", @"<Style TargetType=""ComboBoxItem"">.*?</Style>", "IsHighlighted"),
            ("TextBox", @"<Style TargetType=""TextBox"">.*?</Style>", "IsKeyboardFocused"),
            ("Expander", @"<Style TargetType=""Expander"">.*?</Style>", "IsKeyboardFocused"),
            ("TableRow", @"<Style x:Key=""TableRow"".*?</Style>", "IsKeyboardFocused"),
            ("SegmentItem", @"<Style x:Key=""SegmentItem"".*?</Style>", "IsKeyboardFocused"),
            ("NavigationItem", @"<Style x:Key=""NavigationItem"".*?</Style>", "IsKeyboardFocusWithin"),
        })
            Assert.Contains(trigger, Body(pattern));
        // the focus indicator is a border in the accent colour, not only a change of background, so it does not depend on colour contrast of fills
        Assert.Contains("FocusRing", Body(@"<Style TargetType=""ComboBox"">.*?</Style>"));
        var history = File.ReadAllText(Path.Combine(Repo(), "src/EucLens.App/Views/SessionHistoryView.xaml"));
        Assert.Contains("IsKeyboardFocused", Regex.Match(history, @"<Style x:Key=""LinkButton"".*?</Style>", RegexOptions.Singleline).Value);
    }

    [Fact]
    public void TheTabOrderIsTheReadingOrderBecauseNoViewSetsATabIndex()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.App"), "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            Assert.DoesNotContain("TabIndex=", File.ReadAllText(file));
    }

    [Fact]
    public void EveryButtonTextBoxAndComboBoxOnAPageHasAnAccessibleName()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Repo(), "src/EucLens.App/Views"), "*.xaml"))
        {
            var xaml = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(xaml, @"<(Button|TextBox|ComboBox)(?=[\s/>])[^>]*?/?>", RegexOptions.Singleline))
            {
                var tag = m.Value;
                var named = tag.Contains("AutomationProperties.Name=", StringComparison.Ordinal) || (m.Groups[1].Value == "Button" && Regex.IsMatch(tag, @"\bContent=""[^""{]+""")) || tag.Contains("Content=\"{Binding", StringComparison.Ordinal);
                Assert.True(named, $"{Path.GetFileName(file)}: {tag[..Math.Min(120, tag.Length)]} has no accessible name.");
            }
        }
    }

    // ---------- contrast (WCAG 2.x) ----------

    private static (double R, double G, double B) Rgb(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#')[^6..], 16);
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
    private static double Luminance(string hex)
    {
        static double Lin(double c) { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        var (r, g, b) = Rgb(hex);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }
    internal static double Contrast(string a, string b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    [Fact]
    public void TheContrastFunctionAgreesWithTheKnownReferenceValues()
    {
        Assert.Equal(21, Contrast("#000000", "#FFFFFF"), 6);
        Assert.Equal(1, Contrast("#777777", "#777777"), 6);
        Assert.Equal(4.54, Contrast("#767676", "#FFFFFF"), 2);                                                 // the well-known smallest grey that passes AA on white
    }

    public static IEnumerable<object[]> ThemeAndPair()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            foreach (var text in new[] { "Text", "Muted", "Accent", "Success", "Warning", "Critical" })
                foreach (var surface in new[] { "Window", "Surface", "Elevated" })
                    yield return [theme, text, surface];
            yield return [theme, "Accent", "AccentSurface"];
            yield return [theme, "Text", "AccentSurface"];
            yield return [theme, "Muted", "AccentSurface"];
        }
    }

    [Theory]
    [MemberData(nameof(ThemeAndPair))]
    public void TextReachesWcagAaContrastOnTheSurfacesItIsDrawnOn(string theme, string text, string surface)
    {
        var colors = Resources(theme);
        var ratio = Contrast(colors[text + "Color"], colors[surface + "Color"]);
        output.WriteLine($"{theme}: {text} {colors[text + "Color"]} on {surface} {colors[surface + "Color"]} = {ratio:0.00}:1");
        Assert.True(ratio >= 4.5, $"{theme}: {text} on {surface} is {ratio:0.00}:1, below WCAG AA (4.5:1).");
    }
}
