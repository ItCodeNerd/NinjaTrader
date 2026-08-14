# NinjaTrader

Custom NinjaTrader indicators.

## Indicators

- [**Candle Panel**](Indicators/CandlePanel) — fixed on-screen panel showing last N 5m/15m/1H candles regardless of chart timeframe, plus optional support/resistance lines.
- [**Delta Footprint**](Indicators/DeltaFootprint) — per-price-level buy/sell delta shown as colored numbers on each bar (footprint style). Requires Tick Replay + bid/ask data.
- [**Fixed Lines**](Indicators/FixedLines) — lightweight overlay: fixed-interval price grid, session/day-high/day-low VWAPs, and 9/50/200 EMA, plus session opens and high/low levels.
- [**ICNORB**](Indicators/ICNORB) — standalone Opening-Range-Breakout indicator: NY/Asia/Europe ORB high/low lines plus NY/London pre-open boxes, with session history kept on chart.
- [**Important Lines**](Indicators/ImportantLines) — combined indicator plotting VWAPs, previous-day/week levels, initial balance, opening range, session highs/lows, fixed price lines, and EMAs on one chart.
- [**Order Panel**](Indicators/OrderPanel) — custom order entry panel indicator with mark buy/sell buttons and locked/enabled input state feedback.

## AddOns

- [**Tick Stream Server**](Indicators/TickStreamer) — relays live tick data over TCP (newline-delimited JSON), with push/pull historical bar requests, configurable from a Control Center menu entry.

## License

MIT
