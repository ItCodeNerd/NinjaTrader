# Fixed lines indicator

Lightweight overlay: fixed-interval price grid, session/day-high/day-low anchored VWAPs, and 9/50/200 EMA. Plots only, no custom bar rendering, kept lean for performance.

<img width="734" height="539" alt="Screenshot_2" src="https://github.com/user-attachments/assets/f59b038d-ad8c-4517-9cf8-c135accc2515" />
<img width="303" height="505" alt="Screenshot_1" src="https://github.com/user-attachments/assets/72e0bf4b-ae3b-4292-930b-355298420a5a" />


## Lines available

- **Fixed Lines** — horizontal grid at a fixed price step (default 100), spanning a configurable number of levels above/below current price. Bounds recompute only when price nears the edge of the current range or step changes, avoiding per-bar redraw.
- **Session VWAP** — volume-weighted average price accumulated from first bar of session.
- **Day-High / Day-Low VWAP** — two auxiliary VWAPs that reset their accumulation each time a new session high (or low) is made, tracking volume-weighted price since the last extreme.
- **EMA 9 / 50 / 200** — standard exponential moving averages, each independently toggleable with configurable period.
- **Midnight / Globex / NY Open** — horizontal ray from the opening price of midnight (NY time), Globex session open (18:00 NY, falls back to first bar of session if Trading Hours template excludes it), and configurable NY session start time.
- **Asia / London High-Low** — high and low made during configurable session windows (default Asia 20:00–03:00, London 03:00–09:30 NY time), ray drawn from the bar where the extreme occurred.
- **Previous Day High/Low (PDHL)** — prior session's high and low, anchored at start of current session.
- **Previous Week High/Low (PWHL)** — prior week's high and low. Week boundary computed from a fixed Sunday epoch (Sun–Sat buckets), avoiding day-of-week special-casing.
- **5m / 1H / 4H Open** — open price of the current intrabar period (5-minute, 1-hour, 4-hour NY-time buckets), independent of the chart's own timeframe. Bucket flips automatically at each period boundary.

All lines run from their anchor bar to the right edge of the chart. Labels (optional) show the line name at the current price level, right-aligned by default.

## Controls

Bar-top menu (`ICN_FixedLines`) exposes a Show/Hide All toggle plus a per-line toggle, each colored to match its line/plot. All lines and colors are also configurable via the indicator's parameter dialog, grouped by category (Fixed Lines, VWAP, EMA, Session Opens, etc).
