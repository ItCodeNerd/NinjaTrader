# ICNZones

Automates hand-drawn horizontal "manual box" levels. Four independent detectors feed one shared zone list; overlapping zones from different detectors optionally merge into a single confluence zone whose opacity scales with how many detectors agree.

<img width="693" height="742" alt="image" src="https://github.com/user-attachments/assets/fab22b17-a973-4e32-864b-224b3f56a51a" />

## Detectors

- **Displacement candle anatomy** — candle whose body (or full range) exceeds `Displacement ATR Multiple` x ATR(`ATR Period`) counts as displacement. Emits thin bands: wick high, wick low, body top, body bottom, body CE (midpoint) and range CE.
- **Fair value gaps (3-candle imbalance)** — bullish gap when `Low[0] > High[2]`, bearish when `High[0] < Low[2]`. Optionally dropped once price trades back through them (`Remove Filled Gaps`).
- **Equal highs/lows (repeat touch)** — swing pivots (`Swing Strength` bars each side) clustered within `Equal Tolerance` ticks; a cluster publishes once it has `Min Touches`. Sharp reaction highs/lows that never got a second touch can still be marked via `Show Single-Touch Swing Extremes`.
- **Higher timeframe OHLC** — Open/High/Low/Close of the last closed HTF candle (`HTF Period Type`/`HTF Period Value`), each drawn as a thin band.

## Confluence

When `Merge Confluence` is on, overlapping zones from different detectors merge into one band carrying the union of sources and the highest touch count. Merged (confluence) zones use their own palette slot, denser fill as more detectors agree, and a `(Nx)` suffix on the label.

## Menu

A chart top-bar menu ("ICN_Zones") toggles each detector, all detectors at once, and labels — filters apply instantly without waiting for a new bar. Menu entries are tinted with each zone's color so the menu doubles as a legend.

## Rendering

Zones render in `OnRender` (SharpDX) rather than as drawing objects, so bands span the full canvas width cheaply and redraw instantly on toggle. `Extend Left` controls whether a zone spans the whole chart or starts at its origin bar.

## Palette

Deliberately excludes red/green so a zone never reads as candle color: blue = displacement, aqua = FVG, violet = equal highs/lows, slate = HTF, magenta = confluence.
