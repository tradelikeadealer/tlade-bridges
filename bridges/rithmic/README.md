# TLADe Bridge — Rithmic (R|Protocol)

**Status: Ready**

Direct connection to Rithmic R|Protocol API for real-time CME futures data. Works with all Rithmic-powered brokers and prop firms.

## Choose your operating system

- 🪟 **[Windows setup guide](README_Windows.md)** (recommended — Rithmic is primarily a Windows ecosystem)
- 🍎 **[macOS setup guide](README_macOS.md)**

---

## What you get

- **Real-time ticks** — 5min bars streamed live from Rithmic
- **2 weeks of history** — backfill on startup
- **Real volume** — tick-accurate volume on every bar (VP/AVWAP from actual trades)
- **All Rithmic brokers** — one bridge covers every Rithmic-powered platform

## Supported Systems

Apex, TopstepTrader, Bulenox, Earn2Trade, 10XFutures, 4PropTrader, DayTraders.com, LegendsTrading, LucidTrading, MES Capital, PropShopTrader, TradeFundrr, Tradeify, ThriveTrading, Rithmic 01, Rithmic Paper Trading.

If your broker connects through Rithmic, this bridge works.

## Requirements

- A Rithmic account (any of the systems above)
- Python 3.8+
- A TLADe subscription ([tradelikeadealer.com](https://tradelikeadealer.com))

## ⚠️ Important: One Market Data Session Only

Rithmic allows **only one Market Data session at a time** per account. Before running the bridge, close any other application using your Rithmic market data:

- RTrader Pro
- NinjaTrader (if connected via Rithmic)
- Any other Rithmic-connected platform

If you see authentication errors, this is almost always the cause.

## Bulenox (and "permission denied" on connect)

Some Rithmic entitlements — Bulenox accounts in particular — refuse the login on the order plant with `rpCode 13, permission denied`, and the default bridge stops there. The bridge never uses the order plant, so there is a variant that logs into the market-data and history plants only:

**[Rithmic bridge — Bulenox fix](https://github.com/tradelikeadealer/tlade-bridges/releases/tag/rithmic-bulenox-fix)** — same `start.bat`, same setup, same `.rithmic_config`. Use it if you see `permission denied` in the first lines of the log; if the general bridge works for you, keep it.

Two other errors that look like account problems but are not:

- `You must specify valid SYSTEM_NAME in the credentials: ['Rithmic Test']` — wrong **region**. Run `start.bat`, answer `n` to "Use these settings", pick the other region.
- `heartbeat_interval` on `NoneType` — the line after a failed login; fix the login error above it.

## Alternative

If you use Rithmic through NinjaTrader 8, the [NinjaTrader bridge](../ninjatrader/) also works — it's feed-agnostic and supports Rithmic, CQG, and Kinetick through NT8.

## Questions?

📧 support@tradelikeadealer.com
