# NinjaTrader

Custom NinjaTrader indicators plus an MCP server for Claude Code integration.

## Indicators

- [**Candle Panel**](Indicators/CandlePanel) — fixed on-screen panel showing last N 5m/15m/1H candles regardless of chart timeframe, plus optional support/resistance lines.
- [**Delta Footprint**](Indicators/DeltaFootprint) — per-price-level buy/sell delta shown as colored numbers on each bar (footprint style). Requires Tick Replay + bid/ask data.
- [**Important Lines**](Indicators/ImportantLines) — combined indicator plotting VWAPs, previous-day/week levels, initial balance, opening range, session highs/lows, fixed price lines, and EMAs on one chart.
- [**Order Panel**](Indicators/OrderPanel) — custom order entry panel indicator.

## MCP Server

# NinjaTrader MCP Server

MCP (Model Context Protocol) server for NinjaTrader trading platform. Provides Claude Code with tools to read positions, place orders, access market data, and interact with NinjaScript indicators.

## Features

- **Two Connection Modes**:
  - **Local**: Connect to NinjaTrader Desktop running on your machine
  - **Cloud**: Use NinjaTrader Ecosystem API

- **Account Management**: View accounts, balances, margin
- **Position Tracking**: Monitor open positions
- **Order Management**: Place, modify, and cancel orders
- **Market Data**: Get quotes, historical bars, market depth
- **NinjaScript Access**: Read indicator values, chart state (local mode only)
- **Health Check**: Verify connection status

## Prerequisites

- Node.js 18+
- NinjaTrader 8 (for local mode)
- NinjaTrader account (for cloud mode)

## Setup

### 1. Clone and Install

```bash
git clone https://github.com/YOUR_USERNAME/ninjatrader-mcp.git ~/ninjatrader-mcp
cd ~/ninjatrader-mcp
npm install
```

### 2. Configure Environment

```bash
cp .env.example .env
```

Edit `.env` based on your connection mode:

**Local Mode** (Desktop running):
```env
NINJATRADER_MODE=local
NINJATRADER_LOCAL_URL=http://localhost:7890
```

**Cloud Mode** (Ecosystem API):
```env
NINJATRADER_MODE=cloud
NINJATRADER_API_KEY=your_api_key_here
```

### 3. For Local Mode - Install NinjaTrader Add-On

You need a NinjaScript Add-On running on NinjaTrader Desktop that exposes an HTTP server. See [NinjaTrader Add-On Development](https://developer.ninjatrader.com/) for details.

### 4. Test Connection

```bash
node src/server.js --test
```

### 5. Add to Claude Code

Add to `~/.claude/.mcp.json`:

```json
{
  "mcpServers": {
    "ninjatrader": {
      "command": "node",
      "args": ["/Users/YOUR_USERNAME/ninjatrader-mcp/src/server.js"]
    }
  }
}
```

Replace `YOUR_USERNAME` with your actual username.

### 6. Restart Claude Code

```bash
# Restart Claude Code, then ask:
# "Use nt_health_check to verify NinjaTrader is connected"
```

## Available Tools

### Account (Both Modes)
- `nt_accounts` - List all trading accounts
- `nt_account_info` - Get account details
- `nt_account_balance` - Get balance and margin

### Positions (Both Modes)
- `nt_positions` - List all open positions
- `nt_position` - Get specific position

### Orders (Both Modes)
- `nt_orders` - List orders
- `nt_place_order` - Place new order
- `nt_modify_order` - Modify existing order
- `nt_cancel_order` - Cancel specific order
- `nt_cancel_all_orders` - Cancel all orders

### Market Data (Both Modes)
- `nt_quote` - Get current quote
- `nt_historical_bars` - Get historical OHLCV data
- `nt_market_depth` - Get order book
- `nt_search_instruments` - Search instruments

### NinjaScript (Local Mode Only)
- `nt_indicator_values` - Read custom indicator values from chart
- `nt_chart_state` - Get current chart symbol/timeframe
- `nt_execute_strategy` - Trigger a NinjaScript strategy

### Utility
- `nt_health_check` - Verify connection

## Usage Examples

### Check Your Positions
```
Use nt_positions to show my current futures positions
```

### Place an Order
```
Use nt_place_order to buy 1 ES contract at market
```

### Get Historical Data
```
Use nt_historical_bars to get 100 bars of 5-minute ES data
```

### Read an Indicator (Local Mode)
```
What's the current RSI value on my ES chart?
Use nt_indicator_values with symbol "ES" and indicatorName "RSI"
```

### Get Chart State (Local Mode)
```
What's currently displayed on my chart?
Use nt_chart_state
```

## Connection Modes Explained

### Local Mode

Connects directly to NinjaTrader Desktop running on your machine via a local HTTP server (default port 7890).

**Pros:**
- Access to NinjaScript indicators
- Real-time chart data
- Execute strategies

**Requirements:**
- NinjaTrader 8 running
- NinjaScript Add-On installed and running
- Local server accessible at localhost:7890

### Cloud Mode

Uses NinjaTrader's Ecosystem API for cloud-based access.

**Pros:**
- No local software required
- Access from anywhere
- Works with multiple devices

**Requirements:**
- NinjaTrader Ecosystem account
- API key from NinjaTrader developer portal

## Troubleshooting

### Local Mode

#### "Connection refused"
- Ensure NinjaTrader Desktop is running
- Verify the Add-On server is started in NinjaTrader
- Check `NINJATRADER_LOCAL_URL` in `.env`

#### "Indicator values not available"
- This feature is only available in local mode
- Verify NinjaTrader Desktop is running with the chart open

### Cloud Mode

#### "Authentication failed"
- Verify your API key is correct
- Check API key has necessary permissions

#### "No accounts found"
- Verify your NinjaTrader account is linked to the Ecosystem
- Check account is active

## Architecture

```
Claude Code  ←→  MCP Server (stdio)  ←→  Tradovate API
                              ↓
                        Local Mode:
                        NinjaTrader Desktop (port 7890)
                        
                        Cloud Mode:
                        NinjaTrader Ecosystem API
```

## Security Notes

- Never commit your `.env` file
- Keep API keys secure
- Local mode exposes localhost:7890 (only local access)

## Comparison: Tradovate vs NinjaTrader MCP

| Feature | Tradovate | NinjaTrader |
|---------|-----------|-------------|
| Official API | ✅ Yes | ✅ Yes |
| Local Desktop Access | ❌ No | ✅ Yes |
| Indicator Access | ❌ No | ✅ Yes |
| Strategy Execution | Via API | ✅ Yes |
| Chart Control | ❌ No | ✅ Yes |
| WebSocket | ✅ Yes | ✅ Yes |
| Market Data | ✅ Yes | ✅ Yes |
| Order Management | ✅ Yes | ✅ Yes |

## License

MIT
