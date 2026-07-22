# Order Panel

`ICNOrderPanel.cs` — custom NinjaTrader order entry panel indicator (WPF-based). Docks a small floating panel near the top-right of the chart window with TP/SL/qty controls and a draggable bracket preview.

<img width="231" height="290" alt="image" src="https://github.com/user-attachments/assets/94fda38a-4834-48c9-a97a-ca7495482c1a" />

<img width="892" height="329" alt="image" src="https://github.com/user-attachments/assets/84a703f8-aed2-46a8-99a0-94148012f76f" />

## Install

Copy `ICNOrderPanel.cs` to `Documents/NinjaTrader 8/bin/Custom/Indicators/`, then compile (F5) in NinjaScript Editor.

## Buttons

- **Units: Ticks / $** — toggles whether TP/SL are entered and displayed in ticks or dollars.
- **TP Ticks / SL Ticks / Qty** — +/- spinners and editable text fields for take-profit distance, stop-loss distance, and order quantity. Only editable while idle (no active setup); locked once tracking/placed.
- **MARK BUY / MARK SELL** — starts tracking a long or short setup at the current market price. Draws live entry/TP/SL lines on the chart that follow price until placed. Disabled once a setup exists.
- **PLACE ORDER** — locks in the current entry/TP/SL prices and submits a market entry order plus an OCO-linked limit (TP) and stop-market (SL) bracket. Enabled only while tracking. Button relabels to "ORDER SENT" once locked.
- **CLOSE TRADE** — visible only after an order is placed. Cancels any pending TP/SL orders and submits a market order to flatten the position, then resets the panel to idle.
- **CANCEL** — enabled only while tracking (before placing). Drops the tracked setup and resets to idle without submitting anything. Once an order is placed, use CLOSE TRADE instead — cancelling then would leave a naked position.

## Behavior

- **States**: Idle → Tracking (after MARK BUY/SELL) → Locked (after PLACE ORDER) → back to Idle (via CANCEL, CLOSE TRADE, or auto-reset).
- **Dragging TP/SL**: while tracking, the TP and SL lines on the chart can be dragged with the mouse to adjust their price directly; the tick/dollar values in the panel update accordingly.
- **Auto-reset**: if the position goes flat (TP or SL filled, or closed elsewhere) or the entry order is rejected, the panel automatically resets to idle instead of getting stuck showing "ORDER PLACED".
- **Account**: panel submits orders to the account currently selected in Chart Trader; account name is shown at the bottom of the panel.
