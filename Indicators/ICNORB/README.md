# ICNORB indicator

Standalone Opening-Range-Breakout indicator, extracted from ICNImportantLines' ORB feature set. Draws NY/Asia/Europe opening-range high & low lines plus NY/London pre-open boxes. History of past sessions' boxes kept (bounded to 250 days) and drawn alongside the live one.

<img width="734" height="507" alt="Screenshot_3" src="https://github.com/user-attachments/assets/9ab9ca12-3dda-4dd6-8ef4-57a9a8703367" />

## Lines available

- **NY / Asia / Europe ORB** — high and low made in the first N minutes after each session's open (NYStartTime, AsiaStartTime, EuropeStartTime). Line runs from the ORB window's start bar; extends to the live edge while still forming, freezes at session end once the window closes. Each session independently toggleable, colored, sized.
- **NY / London Pre-Open Box** — high/low of the N minutes *before* that session's open, offset by +/- Points to form a box. Box keeps extending through the session itself (its End) until that session closes, then stops. Handles windows that wrap past midnight (e.g. pre-open starting before 00:00 NY).

Each session's line/box is archived into a bounded history list on `Bars.IsFirstBarOfSession`, so prior days' ORBs/boxes stay visible on the chart instead of only showing the current day.

## Controls

Bar-top menu (`ICN_ORB`) exposes a Show/Hide All toggle plus a per-line toggle, each colored to match its brush. All lines, colors, minutes, and points are also configurable via the indicator's parameter dialog, grouped by category (Sessions, NY ORB, Asia ORB, Europe ORB, NY Pre-Open Box, London Pre-Open Box).
