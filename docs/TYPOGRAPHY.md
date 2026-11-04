# EUC Lens 0.5.1 — typography

One shared system. Fonts and sizes are resources in `src/EucLens.App/Themes/Foundations.xaml`; the styles in `Controls.xaml` apply them; no view, style or custom control sets its own font family or a literal font size (a unit test enforces it).

## Fonts

| Resource | Stack | Used for |
|---|---|---|
| `BodyFont` | `Segoe UI Variable Text, Segoe UI` | Everything: the window, controls, tooltips, drawn text of the custom controls |
| `DisplayFont` | `Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI` | Page titles, the brand, key diagnostic values |
| `IconFont` | `Segoe MDL2 Assets` | Navigation glyphs only |

WPF uses the first family of a list that is installed: Segoe UI Variable on Windows 11, Segoe UI on Windows 10 and older. No font file is bundled or redistributed, and ClearType rendering is the platform's. A Popup or a tooltip does not inherit the window's font, so the tooltip style carries it explicitly. The Windows smoke test logs the face that really resolved (`Typography: body stack … resolves to …; Segoe UI Variable installed: …`) and fails when it is neither.

## Scale (DIPs)

| Role | Token | Size | Weight |
|---|---|---|---|
| Page title | `HeadingFontSize` | 24 | Semibold, `DisplayFont` |
| Section heading, state | `SectionFontSize`, `StateFontSize` | 18 | Semibold |
| Key diagnostic value | `MetricFontSize` | 24 | Semibold, `DisplayFont`, tabular figures |
| Key value in a row, "when" | `ValueFontSize` | 16 | Semibold, tabular figures |
| Finding title | `FindingTitleFontSize` | 15 | Semibold |
| Body text, buttons, inputs, navigation | `BodyFontSize`, `ReadingFontSize` | 14 (line height 20 for paragraphs) | Regular |
| Secondary labels, metadata, filters | `MetaFontSize`, `LabelFontSize` | 13 | Regular |
| Tooltips | `TooltipFontSize` | 13 | Regular |
| Chart labels | `ChartData` | 12 (trend charts 13) | Regular |
| Text drawn by the timeline controls | | 12–13 | Regular |

Numbers that are measurements, percentages, durations and times use tabular figures wherever they are shown as values, so columns line up.

## Colour and contrast

Every text colour of both themes reaches WCAG AA (4.5:1) on every surface it is drawn on (`TypographyTests` computes it from the theme files): text, muted text, accent, success, warning and critical on the window, surface, elevated and accent-surface colours. Severity is never colour alone: each level has its own icon shape and its word.

## Keyboard focus

Every interactive style draws a visible focus indicator that is a border or ring, not only a fill: buttons, text buttons, text boxes, expanders, list lines, segmented filters, column headers, navigation items, combo boxes (accent ring) and their items (accent border while highlighted). Tab order is the reading order (no view sets a `TabIndex`).

## Validation and limits

* Unit tests (`TypographyTests`): font stacks and no bundled font; no literal family or size anywhere; size targets and their order; semibold titles and tabular figures; minimum size of drawn text; focus indicators; no `TabIndex`; accessible names on buttons, text boxes and combo boxes; WCAG AA for 42 theme and surface pairs.
* Windows smoke test: the resolved font, the token values at runtime, and on every page, size (1366×768 to 2560×1440), scale (100, 125, 150 %) and theme, that no visible text is below 13 DIPs, clipped, truncated without a tooltip or accessible name, cut vertically, overlapping a sibling or outside the window, and that no page scrolls sideways. The scale cases simulate the available space; they do not change the Windows DPI.
* **Not honoured: Windows "Text size" (Settings → Accessibility).** WPF applications ignore it; display scaling (DPI) is honoured. A real DPI check on a scaled display is a manual acceptance step.
* Native Windows UI (the tray menu, message boxes, file dialogs) keeps the system's typography.
