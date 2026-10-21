using EucLens.App.Presentation;
using Xunit;

namespace EucLens.Tests;

/// <summary>The responsive findings / evidence split: arrangement, minimum usable sizes and splitter limits.</summary>
public sealed class SplitLayoutTests
{
    private static readonly SplitOptions Defaults = SplitOptions.Default;

    [Theory]
    [InlineData(886, false)]
    [InlineData(899, false)]
    [InlineData(900, true)]
    [InlineData(1100, true)]
    [InlineData(2560, true)]
    [InlineData(double.PositiveInfinity, false)]
    public void SideBySideOnlyWhenBothPanesFitAtTheirMinimum(double width, bool expected) => Assert.Equal(expected, SplitLayout.IsSideBySide(width, Defaults));

    [Fact]
    public void DefaultOptionsCarryTheDocumentedValues()
    {
        Assert.Equal(900, Defaults.SplitWidth);
        Assert.Equal((520, 360, 160, 320, 6), (Defaults.MinMasterWidth, Defaults.MinDetailWidth, Defaults.MinMasterHeight, Defaults.MinDetailHeight, Defaults.Thickness));
    }

    [Fact]
    public void LargerMinimumsRaiseTheSwitchWidthBeyondTheConfiguredSplitWidth()
    {
        var wide = SplitOptions.Default with { MinMasterWidth = 700, MinDetailWidth = 500 };
        Assert.False(SplitLayout.IsSideBySide(1000, wide));
        Assert.True(SplitLayout.IsSideBySide(1206, wide));
    }

    [Theory]
    [InlineData(0.15)]
    [InlineData(0.55)]
    [InlineData(0.9)]
    public void SideBySidePanesNeverShrinkBelowTheirMinimumAndFillTheWidth(double fraction)
    {
        var o = Defaults with { WideFraction = fraction };
        for (var width = 900; width <= 2560; width += 37)
        {
            var r = SplitLayout.Compute(width, 640, o);
            Assert.True(r.SideBySide);
            Assert.True(r.MasterWidth >= o.MinMasterWidth - 1e-9, $"master {r.MasterWidth} at {width}");
            Assert.True(r.DetailWidth >= o.MinDetailWidth - 1e-9, $"detail {r.DetailWidth} at {width}");
            Assert.Equal(width, r.MasterWidth + r.SplitterWidth + r.DetailWidth, 6);
            Assert.Equal(640, r.MasterHeight);
            Assert.Equal(640, r.DetailHeight);
            Assert.Equal(r.MasterWidth, r.SplitterX, 6);
        }
    }

    [Theory]
    [InlineData(400, 520)]
    [InlineData(726, 520)]
    [InlineData(899, 700)]
    [InlineData(600, 1000)]
    public void StackedEvidenceKeepsAUsableHeightAndTheListStaysVisible(double width, double height)
    {
        var r = SplitLayout.Compute(width, height, Defaults);
        Assert.False(r.SideBySide);
        Assert.True(r.DetailHeight >= Defaults.MinDetailHeight - 1e-9, $"detail {r.DetailHeight}");
        Assert.True(r.MasterHeight >= Defaults.MinMasterHeight - 1e-9, $"master {r.MasterHeight}");
        Assert.Equal(height, r.MasterHeight + r.SplitterHeight + r.DetailHeight, 6);
        Assert.Equal(width, r.MasterWidth);
        Assert.Equal(width, r.DetailWidth);
        Assert.Equal(r.MasterHeight + Defaults.Thickness, r.DetailY, 6);
    }

    [Theory]
    [InlineData(700, 80, 520, 620)]
    [InlineData(1300, 80, 520, 1220)]
    [InlineData(400, 80, 520, 520)]
    [InlineData(0, 80, 520, 520)]
    [InlineData(double.NaN, 80, 520, 520)]
    public void TheFindingsRegionFillsTheVisiblePageButNeverDropsBelowItsMinimum(double viewport, double reserve, double minimum, double expected) =>
        Assert.Equal(expected, SplitLayout.RegionHeight(viewport, reserve, minimum));

    [Fact]
    public void AVeryShortStackedRegionSharesTheHeightInsteadOfLosingAPane()
    {
        var r = SplitLayout.Compute(600, 300, Defaults);
        Assert.True(r.MasterHeight > 90 && r.DetailHeight > 150, $"{r.MasterHeight} / {r.DetailHeight}");
        Assert.Equal(300, r.MasterHeight + r.SplitterHeight + r.DetailHeight, 6);
    }

    [Fact]
    public void MovingTheSplitterIsClampedToLegalLayouts()
    {
        var (width, height) = (1100d, 640d);
        var start = SplitLayout.Compute(width, height, Defaults);
        var grown = Defaults with { WideFraction = SplitLayout.FractionAfterMove(width, height, Defaults, 24) };
        Assert.Equal(start.MasterExtent + 24, SplitLayout.Compute(width, height, grown).MasterExtent, 6);

        var farRight = Defaults with { WideFraction = SplitLayout.FractionAfterMove(width, height, Defaults, 100000) };
        var maxed = SplitLayout.Compute(width, height, farRight);
        Assert.Equal(Defaults.MinDetailWidth, maxed.DetailWidth, 6);

        var farLeft = Defaults with { WideFraction = SplitLayout.FractionAfterMove(width, height, Defaults, -100000) };
        Assert.Equal(Defaults.MinMasterWidth, SplitLayout.Compute(width, height, farLeft).MasterWidth, 6);
    }

    [Fact]
    public void TheSplitSurvivesResizingProportionallyWithinItsLimits()
    {
        var o = Defaults with { WideFraction = 0.6 };
        var narrow = SplitLayout.Compute(1000, 640, o);
        var wide = SplitLayout.Compute(2000, 640, o);
        Assert.True(wide.MasterWidth > narrow.MasterWidth);
        Assert.Equal(0.6, wide.MasterWidth / wide.Total, 3);
        Assert.True(narrow.DetailWidth >= o.MinDetailWidth);
        // Crossing the switch width and returning keeps the same stored fraction.
        Assert.False(SplitLayout.Compute(800, 640, o).SideBySide);
        Assert.Equal(narrow, SplitLayout.Compute(1000, 640, o));
    }
}
