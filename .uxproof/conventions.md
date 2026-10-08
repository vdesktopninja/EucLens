# EUC Lens house rules

This contract records the WPF implementation and the redesign intent separately. JSON is extracted by `tools/ux-contract.py`; this file is maintained by hand. Obtain the operator's refresh decision before regenerating JSON. Preserve this file and its manual section. Manual rules outrank generated observations.

## Extracted implementation

WPF on .NET 10 with MVVM; native controls, Segoe MDL2 icons and LiveCharts2 2.0.0. Theme dictionaries remain the first merged dictionary because `DesktopServices.ApplyTheme` replaces index 0. Foundations load second, controls third. No styling dependency is added.

The authoritative resource sources are `src/EucLens.App/Themes/Light.xaml`, `Dark.xaml`, `Foundations.xaml` and `Controls.xaml`. Colors use semantic `Color` resources for Skia conversion and paired brushes for WPF. Shared colors must use `DynamicResource` in controls so switching themes takes effect. Dimensions/fonts use `StaticResource`. Both themes must expose identical keys.

Screen examples in `contract.json` are the five implemented views; Windows visual acceptance remains pending. `MetricTemplate`/`MetricGrid` remain compatibility resources; new structured details should use `MetricRows`. `Card` remains an alias-compatible compact surface; use `Panel` for new sections. No tokens came from scratch/generated files: extraction reads only the four maintained theme dictionaries.

## Manual rules

<!-- uxproof:manual-start -->
Authority: the operator's EUC Lens redesign brief, 2026-10-08. These decisions were supplied explicitly; no additional team preference is assumed.

- Identity and tone: professional Windows engineering software. Use precise technical labels: Session overview, Diagnostics, Session recorder, System information, Settings. No slogans, hero headings or decorative badges. Synthetic metrics and unavailable integrations must remain clearly identified.
- Typography: Segoe UI, body 13 DIP, labels 12, section titles 14 semibold, page headings 18 semibold, primary metric values 22 semibold. Use tabular numerals for measurements where supported. Do not uppercase all body text or shrink content to fit.
- Dimensions: 4/8 DIP spacing, multiples 12/16/24 for grouping. Panels use 12 DIP padding; pages 16; controls and navigation target 32 DIP height. Borders are 1 DIP. Normal radii are 4 DIP; popups may use 6. No gradients or shadows. Layout rounding is enabled.
- Color: neutral light surfaces and complementary graphite dark surfaces. Blue denotes selection/action. Green/amber/red denotes healthy/warning/critical, accompanied by text. Avoid semantic status colors for unrelated chart series. Use AccentColor for RTT and ChartSecondaryColor for FPS; recorder RTT uses the same RTT color. All chart axes, gridlines, tooltips and backgrounds adapt to the active theme.
- Layout: compact 184 DIP navigation with small consistent native glyphs, one technical page header, a single metrics strip for RTT/FPS/bandwidth/GPU/CPU, two equal monitoring panes, structured Connection/Graphics/GPU/System rows. Keep protocol/scenario/workload selectors operable. Add session identity without duplicating the entire inventory on the overview.
- Charts: retain LiveCharts2, real time axes and units (ms, FPS, Mbps, percent); thin 1 DIP gridlines, 2 DIP series strokes, no point markers, no area fill unless it carries meaning. Animation remains disabled. Bound history remains 120 live and 600 recorder samples. Ensure clear unavailable states instead of zero-looking data.
- Responsiveness: validate physical resolutions 1366×768, 1536×864, 1920×1080, 2560×1440 at Windows scaling 100/125/150%. Work in DIPs. At 1366×768 and 150%, usable window dimensions are below 911×512 DIP after chrome/work area; the old 1000×700 minimum cannot satisfy this matrix. Allow a compact minimum and reflow to one chart/detail column at narrow widths. Primary metrics and recording controls stay accessible; intentional scrolling for extended details is acceptable. Do not claim DPI conformance from source inspection.
- Accessibility: preserve labels, automation names, keyboard navigation, visible focus and disabled states. Status is never color alone. Long values wrap or expose full content without overlap. Detail tooltips are supplemental, not the sole means of accessing important information.
- Preservation: no metrics-engine rewrite, extra UI dependency, removal of demo provider, selectors, diagnostics, recording, JSON/CSV export, preference persistence, theme switching or existing tests. Preserve MVVM and bounded sampling.
- Exception: raw colors belong only in theme dictionaries. A Skia paint adapter may consume theme Color resources at the view/service boundary; avoid WPF resource lookups inside the portable/tested metrics model. Fixed vendor/protocol identities may be documented individually, not inferred by the scanner.
<!-- uxproof:manual-end -->

## Verification and refresh

Run `python3 tools/ux-contract.py --check` for deterministic contract/resource checks. It verifies XML and named resource presence, not WPF types or rendering. When design resources change, inform the operator, then run `python3 tools/ux-contract.py --refresh`; it leaves these manual rules untouched. `npx uxproof@0.3.1 init --no-skills` rejected WPF as non-React and wrote nothing; its browser/React review tools are not WPF validation.
