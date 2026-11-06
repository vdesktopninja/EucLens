# Documentation screenshots

The main README shows four screenshots of the real application. They are **not added yet**: they must come from a real EUC Lens v0.5.1 running in a real session, and the automated test harness cannot supply them (its screenshots carry a *SYNTHETIC TEST DATA* banner). This page says what is needed.

## Files to add

| File | Page | What it should show |
|---|---|---|
| `dashboard.png` | Dashboard | A live Horizon session: health, protocol *Horizon Blast*, RTT and FPS trends, CPU and memory. |
| `diagnostics.png` | Diagnostics | Connection, Graphics and VDI Guest health with at least one finding selected and its evidence visible. |
| `session-recorder.png` | Session Recorder | A recording in progress or just finished, the latency chart with data, and a few saved recordings with one selected. |
| `session-history.png` | Session History | An analyzed recording with synchronized charts, the performance timeline and grouped findings. |

Use the same theme for all four (light is easier to read on GitHub), a window of about 1600×1000 pixels at 100% display scaling, and PNG.

## Before you publish a screenshot

- **No synthetic data.** The header banner must read *REAL SESSION*, never *SYNTHETIC TEST DATA*.
- **No personal or infrastructure details.** Check the whole image, including the title bar, the taskbar, the window title and every table, for: user and computer names, host names, IP addresses, session IDs, recording IDs and file paths that include a user name, domain or company names, and the Horizon pool or farm name. Recordings of a test desktop are best. Crop or blur anything that remains.
- **Honest content.** Do not stage values. The screenshot should show what the application showed.
- **Size.** Optimize the PNG (for example with `oxipng -o4 --strip safe` or `pngquant`); keep each file below about 400 KB.

## Enabling them in the README

1. Add the four PNGs to this folder.
2. In `README.md`, replace the HTML comment headed *SCREENSHOTS* with the gallery it contains (the alt text is already written).
3. Check that the images render on GitHub, in light and dark mode.
