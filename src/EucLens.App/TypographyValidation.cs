using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace EucLens.App;

/// <summary>
/// Windows-only checks of the application's typography, run by --smoke-test: which font family the shared stack really resolves to on this machine (Segoe UI Variable where installed, else
/// Segoe UI), the runtime values of the typography tokens, and, on every page visit of the layout matrix, that no visible text is smaller than the secondary-label size, clipped, truncated without
/// its full value being available, cut vertically, overlapping a neighbour or outside the window, and that the page never scrolls sideways. Problems are collected over the whole matrix and reported
/// together, so one run shows every page, size, scale and theme that fails. Windows text-size scaling (Settings → Accessibility) is not honoured by WPF; display scaling is, and is simulated by the matrix.
/// </summary>
internal static class TypographyValidation
{
    internal static readonly List<string> Problems = [];
    private static readonly HashSet<string> Seen = [];

    internal static void Reset() { Problems.Clear(); Seen.Clear(); }
    internal static void Report(string visit, string text) => Problem(visit, text);

    /// <summary>The chain of ancestors of an element (type, name or accessible name), to find it in the XAML.</summary>
    internal static string Where(DependencyObject element)
    {
        var parts = new List<string>();
        var node = VisualTreeHelper.GetParent(element);
        for (var depth = 0; node is not null && depth < 6; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement fe) continue;
            var label = !string.IsNullOrEmpty(fe.Name) ? fe.Name : AutomationProperties.GetName(fe);
            if (node is Panel || !string.IsNullOrEmpty(label) || node is UserControl) { parts.Add(string.IsNullOrEmpty(label) ? node.GetType().Name : $"{node.GetType().Name} '{label}'"); depth++; }
        }
        return "in " + string.Join(" ← ", parts);
    }

    private static void Problem(string visit, string text)
    {
        // The same defect at every size and scale is one line, with the first visit that showed it: numbers are ignored when comparing.
        if (Seen.Add(System.Text.RegularExpressions.Regex.Replace(text, @"\d+(\.\d+)?", "#"))) Problems.Add($"{visit}: {text}");
    }

    /// <summary>The resolved font, the token values and the styles that carry them; one report line each. Throws when the font stack does not resolve to a Segoe UI face.</summary>
    internal static string CheckResources(Window window)
    {
        var report = new StringBuilder();
        var stack = (FontFamily)window.FindResource("BodyFont");
        var display = (FontFamily)window.FindResource("DisplayFont");
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Resolve(FontFamily family)
        {
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyphs)) return "(no glyph typeface)";
            return glyphs.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out var name) ? name : glyphs.FamilyNames.Values.First();
        }
        var body = Resolve(stack); var heading = Resolve(display);
        var variable = installed.Contains("Segoe UI Variable Text") || installed.Contains("Segoe UI Variable");
        report.AppendLine($"Typography: body stack '{stack.Source}' resolves to '{body}', heading stack '{display.Source}' to '{heading}'; Segoe UI Variable installed: {variable}; Segoe UI installed: {installed.Contains("Segoe UI")}.");
        if (!body.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase) || !heading.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The font stack resolves to '{body}' / '{heading}', not to a Segoe UI face: the fallback does not work on this machine.");
        if (variable != body.StartsWith("Segoe UI Variable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Segoe UI Variable installed: {variable}, but the stack resolves to '{body}'.");

        double Size(string key) => (double)window.FindResource(key);
        void Range(string key, double min, double max) { if (Size(key) < min || Size(key) > max) throw new InvalidOperationException($"{key} is {Size(key)}, outside {min}–{max}."); }
        Range("HeadingFontSize", 22, 24); Range("SectionFontSize", 18, 18); Range("FindingTitleFontSize", 15, 16); Range("BodyFontSize", 14, 16); Range("ReadingFontSize", 14, 16);
        Range("MetaFontSize", 12, 13); Range("LabelFontSize", 12, 13); Range("MetricFontSize", 22, 26); Range("TooltipFontSize", 13, 16);
        report.AppendLine($"Typography tokens at runtime: page title {Size("HeadingFontSize"):0.#}, section {Size("SectionFontSize"):0.#}, finding title {Size("FindingTitleFontSize"):0.#}, body {Size("BodyFontSize"):0.#}, secondary {Size("MetaFontSize"):0.#}, key values {Size("MetricFontSize"):0.#}, tooltips {Size("TooltipFontSize"):0.#}.");
        if (window.FontSize != Size("BodyFontSize") || !window.FontFamily.Source.Equals(stack.Source, StringComparison.Ordinal))
            throw new InvalidOperationException($"The window does not carry the shared body font ({window.FontFamily.Source} {window.FontSize}).");
        var tooltip = (Style)window.FindResource(typeof(ToolTip));
        var tipSize = tooltip.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.FontSizeProperty);
        if (tipSize is null || tipSize.Value is not double size || size < 13) throw new InvalidOperationException("The tooltip style does not set a font size of at least 13.");
        report.AppendLine("Typography: window, tooltip and heading styles carry the shared fonts and sizes.");
        return report.ToString();
    }

    /// <summary>The checks of one page visit (the whole window: navigation, header, page and footer).</summary>
    internal static void CheckPage(Window window, string visit)
    {
        // The layout matrix hosts the real client in a canvas that gives it the size under test: positions are checked against that client, not against the canvas.
        var root = window.Content is Canvas { Children.Count: > 0 } host && host.Children[0] is FrameworkElement hosted ? hosted : window.Content as FrameworkElement;
        if (root is null) return;
        var blocks = DesktopValidation.Descendants<TextBlock>(root)
            .Where(t => t.IsVisible && t.ActualWidth > 0 && t.ActualHeight > 0 && !string.IsNullOrWhiteSpace(t.Text) && !t.FontFamily.Source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var t in blocks)
        {
            var label = t.Text.Length > 48 ? t.Text[..48] + "…" : t.Text;
            if (t.FontSize < 12.99) Problem(visit, $"'{label}' is {t.FontSize:0.#} DIPs, below the 13 DIP secondary-label size");
            if (t.TextWrapping == TextWrapping.NoWrap)
            {
                var natural = DesktopValidation.NaturalWidth(t);
                if (natural > t.ActualWidth + 1)
                {
                    if (t.TextTrimming == TextTrimming.None) Problem(visit, $"'{label}' is clipped: it needs {natural:0} DIPs and has {t.ActualWidth:0} ({Where(t)})");
                    else if (!HasFullValue(t)) Problem(visit, $"'{label}' is truncated and its full value is not available (no tooltip or accessible name)");
                }
            }
            else
            {
                var needed = DesktopValidation.NeededHeight(t, t.ActualWidth);
                if (needed > t.ActualHeight + 1) Problem(visit, $"'{label}' is cut vertically: it needs {needed:0} DIPs at its width and has {t.ActualHeight:0} ({Where(t)})");
            }
            var bounds = t.TransformToAncestor(root).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
            if (bounds.Right > root.ActualWidth + 1 || bounds.Left < -1) Problem(visit, $"'{label}' lies outside the window ({bounds.Left:0}–{bounds.Right:0} of {root.ActualWidth:0}; {Where(t)})");
        }
        foreach (var siblings in blocks.GroupBy(t => VisualTreeHelper.GetParent(t)).Where(g => g.Key is not null && g.Count() > 1))
        {
            var parent = (Visual)siblings.Key!;
            var rects = siblings.Select(t => (Text: t.Text, Rect: t.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight)))).ToArray();
            for (var i = 0; i < rects.Length; i++)
                for (var j = i + 1; j < rects.Length; j++)
                {
                    var overlap = Rect.Intersect(rects[i].Rect, rects[j].Rect);
                    if (!overlap.IsEmpty && overlap.Width > 2 && overlap.Height > 2) Problem(visit, $"'{Short(rects[i].Text)}' and '{Short(rects[j].Text)}' overlap ({overlap.Width:0}×{overlap.Height:0} DIPs; {Where(siblings.First())})");
                }
        }
        if (DesktopValidation.PageHost(window) is { } page && page.ExtentWidth > page.ViewportWidth + 1) Problem(visit, $"the page scrolls sideways (content {page.ExtentWidth:0} DIPs in a viewport of {page.ViewportWidth:0})");
    }

    private static string Short(string text) => text.Length > 32 ? text[..32] + "…" : text;

    /// <summary>A truncated text still gives its full value to the mouse (a tooltip, on the text or an ancestor) and to a screen reader (an accessible name, or the text itself).</summary>
    private static bool HasFullValue(TextBlock t)
    {
        for (DependencyObject? node = t; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { ToolTip: not null }) return true;
        return !string.IsNullOrEmpty(AutomationProperties.GetName(t));
    }
}
