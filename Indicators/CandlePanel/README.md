# Candle panel indicator

Shows last N 5-minute, 15-minute and 1-hour candles, independent of chart's own timeframe. Lets you monitor higher timeframes without switching charts or adding extra panes. Renders directly on price panel via SharpDX for low overhead.

## Layouts

Two switchable layouts (`Layout` property, or "Switch to ... layout" in the top menu):

- **RealScaleFuture** (default) — mini-candles drawn straight onto the chart's own real price scale, past the last real bar. Each mini-candle sits at its true price level, and the current-price tick lines up with NT8's own live price line automatically.
- **FixedBox** — self-contained box pinned to a screen corner (position/size configurable), each timeframe group scaled to its own high/low range independently. Always visible regardless of scroll/zoom.

Per group (5m/15m/1H), each column shows: a countdown to that timeframe's bar close, the candles themselves, a volume-weighted direction score (e.g. `+12.3K`/`-4.5M`), and (RealScaleFuture) a tick mark for the live price.

An overall **bias label** (BULLISH/BEARISH/MIXED) is a majority vote across the three groups' volume-weighted scores.

## Top menu

Chart tab gets an "ICN_CandlePanel" menu entry with: show/hide panel, switch layout, cycle panel corner, per-column show/hide (5m/15m/1H), and toggles for bias label / time remaining / current price line. Menu only shows on the chart tab this indicator is on.

## Configuring

Panel position, size, and number of candles per timeframe are configurable via properties (candle pitch, future gap, margins, panel width/height/corner, colors). Number of candles is capped at 20 per timeframe. Note: support/resistance line drawing from recent swing high/low is not part of the current version.

<img width="360" height="415" alt="image" src="https://github.com/user-attachments/assets/656c0de9-3b4d-414c-9b91-14c702514728" />

<img width="545" height="331" alt="image" src="https://github.com/user-attachments/assets/e0d59769-94d4-4331-a0f1-6c291bfd2578" />

<img width="324" height="272" alt="image" src="https://github.com/user-attachments/assets/ef2c0d11-b72d-494e-b983-77481480ec90" />

<img width="320" height="580" alt="image" src="https://github.com/user-attachments/assets/956abc20-830e-487b-aa53-ea8e1daf02ae" />
