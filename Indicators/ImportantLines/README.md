# Important Lines indicator

Combined indicator plotting session VWAP, previous-day levels, fixed number lines, and up to 4 configurable EMAs on one chart. Session VWAP tracks Asia/London/NY sessions with custom start/end times (ET, wraps overnight if needed). Previous-day levels mark prior day's high/low/close for reference. Fixed number lines let you pin arbitrary price levels. EMAs (periods, colors, show/hide) fully configurable per line. Aims to replace several separate indicators with one, keeping chart clean.

## Lines available

- **EMA Levels** — up to 4 EMAs, each with own period, color, show/hide toggle.
- **VWAP** — multiple VWAP variants: full session, Asia/Europe/NY sessions, day-high/day-low anchored, weekly, rolling 24h, and previous-day NY/Session endpoints (dashed reference). Optional ±1/±2 standard-deviation bands. Session times configurable (ET, wraps overnight).
- **Previous Week** — prior week high/low/close levels.
- **Current Month** — current calendar month's high/low, live-updating as the month progresses, drawn from the month's first bar to the current bar.
- **Last 4H Candle** — high/low of the last completed 4-hour candle, projected across the still-forming candle. Buckets anchored to the 18:00 ET Globex session open (18:00/22:00/02:00/06:00/10:00/14:00 ET), matching a real 4H chart on the instrument rather than a midnight-anchored clock grid.
- **Prev Day – Value Area** — POC/value-area high-low from prior session. Value area method selectable (VolumeProfile, Uniform, LinearWeighted, CloseWeighted, TPO), value-area % (default 70), and bucket size in ticks.
- **Prev Day – Previous Day** — Previous Day High/Low (PDH/PDL) and Previous Value Area High/Low (PVAH/PVAL), color and line width per line.
- **Prev Day – Today** — today's running equivalents of the above levels, updated live.
- **Prev Day – Visibility** — per-line show/hide toggles for all previous-day levels.
- **Initial Balance** — first-hour (configurable) high/low range lines.
- **Opening Range** — configurable opening-range high/low lines with own timing.
- **Session High/Low** — running high/low of current session.
- **Globex Open** — Globex session open price line.
- **Midnight Open** — midnight open price line.
- **Session Open Lines** — session open price markers.
- **Fixed Lines** — manually pinned price levels, not tied to any session or calculation.

<img width="1140" height="903" alt="image" src="https://github.com/user-attachments/assets/29b1cb9e-ce54-4721-b0a7-455bbef2b27e" />

<img width="336" height="421" alt="image" src="https://github.com/user-attachments/assets/918e914e-8c70-4248-ba4e-7db00d3351e5" />

<img width="638" height="482" alt="image" src="https://github.com/user-attachments/assets/b9bfe865-58aa-41f5-aaaa-9e83c4175194" />

<img width="634" height="594" alt="image" src="https://github.com/user-attachments/assets/410d9cc0-0b8e-46b4-a989-aed40ac35716" />

<img width="624" height="437" alt="image" src="https://github.com/user-attachments/assets/ae424869-d2d7-4750-a2f9-65b11c25c179" />

<img width="630" height="149" alt="image" src="https://github.com/user-attachments/assets/b06f6849-744a-4e88-b1d8-42282f8a7723" />

<img width="642" height="704" alt="image" src="https://github.com/user-attachments/assets/a6c83f27-3f6a-402c-bd44-50e4d66b7e85" />

<img width="643" height="682" alt="image" src="https://github.com/user-attachments/assets/48a999c3-d389-403e-85d9-90ee840cf496" />

<img width="630" height="539" alt="image" src="https://github.com/user-attachments/assets/df469215-0630-4d7d-87dd-3d1f7663e972" />

<img width="641" height="573" alt="image" src="https://github.com/user-attachments/assets/255a17de-1bd5-4bdc-9a49-0b1a0626aead" />

<img width="636" height="647" alt="image" src="https://github.com/user-attachments/assets/63447601-b445-4d76-9d44-756fd21189f7" />

<img width="636" height="420" alt="image" src="https://github.com/user-attachments/assets/e309beae-d653-4094-af63-cb681a21c082" />
