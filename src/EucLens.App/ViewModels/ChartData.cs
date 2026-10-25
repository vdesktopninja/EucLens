using System.Collections.ObjectModel;

namespace EucLens.App.ViewModels;

/// <summary>One sample in a chart history; a null value is an unavailable sample and renders as a gap.</summary>
public readonly record struct TimeValue(DateTime At, double? Value);

/// <summary>A configured threshold drawn as a reference line; never a value the engine does not use.</summary>
/// <remarks>
/// <see cref="Conditional"/> lines are drawn only while their condition holds (see <see cref="Applies"/>); <see cref="Condition"/>
/// explains it in words. <see cref="Visible"/> additionally hides a line that lies outside the plotted value range.
/// </remarks>
public sealed record ReferenceLine(string Level, double Value, string Label, string ChartLabel, bool Visible = true, bool Conditional = false,
    bool Applies = true, string? Condition = null);

/// <summary>
/// Chart content owned by a view model: bounded points, axis ranges and reference lines. It holds no LiveCharts
/// objects. LiveCharts keeps every chart a series or point was ever drawn in reachable from that series or point,
/// so each chart view builds its own series, axes and sections from this data (see Presentation.ChartObjects).
/// </summary>
public sealed class ChartData(string name, string unit, int capacity, string? axisName = null, double? yMin = 0, double? yMax = null,
    double yMinStep = 1, double xStepSeconds = 20, double smoothness = 0, double labelSize = 11) : ObservableObject
{
    private double? xMin, xMax, yMaxLimit = yMax;
    private IReadOnlyList<ReferenceLine> references = [];
    public string Name { get; } = name;
    public string Unit { get; } = unit;
    public string? AxisName { get; } = axisName;
    public int Capacity { get; } = capacity;
    public double? YMin { get; } = yMin;
    public double YMinStep { get; } = yMinStep;
    public double XStepSeconds { get; } = xStepSeconds;
    public double Smoothness { get; } = smoothness;
    /// <summary>Axis and threshold-line label size in DIPs.</summary>
    public double LabelSize { get; } = labelSize;
    /// <summary>
    /// Optional formatters for the time axis and tooltip. Without them the x values are read as local wall-clock ticks (live charts). History charts plot UTC ticks
    /// and format each instant to local time, so a recording across a clock change keeps its order and shows the right time.
    /// </summary>
    public Func<DateTime, string>? TimeLabel { get; init; }
    public Func<DateTime, string>? TooltipTimeLabel { get; init; }
    public ObservableCollection<TimeValue> Points { get; } = [];
    /// <summary>Visible time range in DateTime ticks; null lets the chart fit the data.</summary>
    public double? XMin { get => xMin; private set => Set(ref xMin, value); }
    public double? XMax { get => xMax; private set => Set(ref xMax, value); }
    public double? YMax { get => yMaxLimit; private set => Set(ref yMaxLimit, value); }
    public IReadOnlyList<ReferenceLine> References { get => references; private set => Set(ref references, value); }

    public void Add(DateTime at, double? value)
    {
        Points.Add(new(at, value));
        if (Points.Count > Capacity) Points.RemoveAt(0);
    }
    public void TrimBefore(DateTime before) { while (Points.Count > 0 && Points[0].At < before) Points.RemoveAt(0); }
    public void Clear() => Points.Clear();
    public void SetXRange(DateTime? start, DateTime? end) { XMin = start?.Ticks; XMax = end?.Ticks; }
    public void SetYMax(double? value) => YMax = value;
    public void SetReferences(IReadOnlyList<ReferenceLine> value) { if (!value.SequenceEqual(references)) References = value; }
}
