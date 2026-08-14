# Tick Stream Server

NinjaTrader AddOn. Relays live tick data over a raw TCP socket so external processes (Python, Node, etc.) can consume market data without polling files.

## What it does

- Subscribes to ONE instrument's live MarketData feed (trade/bid/ask updates).
- Listens on `127.0.0.1:5555`. Any client that connects gets newline-delimited JSON, one object per line, as ticks arrive.
- No files, no polling — pure push over TCP.

## Configuring

Control Center menu bar gets a **"Tick Stream..."** entry. Opens a small window to:

- Set the instrument symbol (e.g. `ES 12-25`) — changing it live re-subscribes without restarting NinjaTrader.
- Enable/disable the live stream. Disabling drops the MarketData subscription entirely (no CPU/network cost) — useful if you only want historical pulls.

Settings persist to `Documents\NinjaTrader 8\NT8Bridge\tickstream_symbol.txt` and `tickstream_enabled.txt`.

## Live tick line shape

```json
{"ts":"2026-08-14T10:00:00.000Z","symbol":"NQ 06-26","type":"trade","price":19500.25,"volume":2}
```

`type` is `trade`, `bid`, or `ask`. Live tick lines carry no `"kind"` field (back-compat) — treat any line without `"kind"` as a live tick.

## Historical bars — two ways

**1. Push** — from the settings window, pick a date range + bar type/value, click "Send History". Broadcasts to every connected client.

**2. Pull** — a connected client sends one request line:

```json
{"cmd":"history","symbol":"NQ 06-26","from":"2026-08-01T00:00:00Z","to":"2026-08-05T00:00:00Z","barType":"Minute","barValue":1}
```

`symbol` is optional (defaults to the addon's configured symbol). Reply goes to that client only.

Both paths emit the same shapes:

```json
{"kind":"bar","symbol":"NQ 06-26","barType":"Minute","barValue":1,"t":"2026-08-01T00:01:00.000Z","o":19500.0,"h":19501.0,"l":19499.5,"c":19500.75,"v":120}
{"kind":"historyEnd","symbol":"NQ 06-26","barType":"Minute","barValue":1,"count":1440}
```

or on failure:

```json
{"kind":"historyError","message":"instrument not found: XX 12-25"}
```

## Install

Copy `TickStreamServer.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\`, then compile (F5) in the NinjaScript editor.

## Notes

- Multiple clients can connect concurrently; each gets the same live broadcast, and pulled history goes only to the requester.
- Port is fixed at `5555`, loopback only (`127.0.0.1`).
