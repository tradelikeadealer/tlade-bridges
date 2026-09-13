# TLADe Bridges — Third-Party Indicators and Add-ons

**Bring your own data feed and your own charting platform to
[TLADe](https://tradelikeadealer.com)** — the GEX analytics terminal
for futures and options traders.

This repo holds the integrations that run **outside** the TLADe
terminal — on TradingView, NinjaTrader 8, ATAS, MotiveWave and
broker/feed APIs. For indicators that run **inside** the TLADe
terminal itself (drawn natively on its LWChart canvas), see
[`../native/`](native/).

## How It Works

```
Your Broker/Platform  ──>  Bridge (runs locally)  ──>  TLADe Terminal
   (TWS, NT8, etc.)        (Python/C#/any lang)        (auto-detects on localhost)
```

1. You run a bridge on your machine — a small local server that reads from your data feed
2. The TLADe terminal automatically detects it on `localhost:5000`
3. You get real-time data with zero latency and tick-accurate volume

**No data leaves your machine.** The bridge runs 100% locally.

## Available Integrations

The integrations below come in two flavours, kept side by side for
transparency: those **built by the TLADe team** (canonical
reference implementations) and those **contributed by the community
and patched by TLADe** (original source preserved, our patches
documented in each subfolder's `CHANGELOG`).

### TLADe-built

Canonical integrations developed and maintained directly by the
TLADe team.

| Integration | Status | Feed / Surface | Language |
|---|---|---|---|
| [TradingView Pine](bridges/) (in repo terminal/TV-Indicators/) | **Ready** | Pine v6 indicators for ES/SPX/SPY and NQ/NDX/QQQ — published on TradingView | Pine |
| [Interactive Brokers](bridges/ib/) | **Ready** | TWS / IB Gateway | Python |
| [Rithmic](bridges/rithmic/) | **Ready** | R\|Protocol direct (Apex, TopstepTrader, Bulenox, Earn2Trade + 12 other prop firms) | Python |
| [Rithmic — Bulenox fix](https://github.com/tradelikeadealer/tlade-bridges/releases/tag/rithmic-bulenox-fix) | **Variant** | Same bridge, logs into the market-data and history plants only. For Bulenox and any Rithmic login that answers `rpCode 13 permission denied` on the order plant. | Python |

### Community-contributed (TLADe-patched)

Built originally by a community contributor; reviewed line-by-line,
patched by the TLADe team where needed, and published with the
original source preserved next to ours. Each integration's
`original/` folder holds the contributor's untouched code with full
credit; the root-level source is the TLADe-patched build. See
`CHANGELOG.md` in each subfolder for the patch list.

| Integration | Status | Surface | Contributor |
|---|---|---|---|
| [NinjaTrader 8](bridges/ninjatrader/) | **Beta** | NT8 chart indicator + local receiver (Rithmic, CQG, Kinetick via NT8) | Kris (C# + Python) |
| [ATAS](bridges/atas/) | **Ready** | ATAS Platform — Bridge + GEX Dashboard + Quantum Field Ladder (Rithmic / CQG via ATAS data) | Mihai (C# + Python) |
| [MotiveWave](bridges/motivawe/) | **Ready (cross-OS, MW Java 25+)** | TLADe levels overlay on MotiveWave charts. Single jar for macOS, Windows and Linux. | Herat Acharya (Java) |
| [Quantower](bridges/quantower/) | **Community test** | Quantower chart indicator — feed levels + GEX profile, Live fetch from the TLADe endpoint. Published as delivered by the author (no `original/` split). | Dogan Cile (C#) |
| [CQG](bridges/cqg/) | Wanted | CQG API direct | — |

### Independent versions

Anyone can build their own version of any indicator here and publish it under
their own name — the MIT licence allows it and no permission from us is needed.
Those versions are **not reviewed by TLADe**: we don't test them, we don't keep
them in sync with the data format, and we can't support them. Use them at your
own discretion, and take questions to whoever wrote them.

If you publish one and would like it listed below so users can find it, open an
issue with the link and a couple of lines on what's different. We'll add the row —
listing is not an endorsement.

| Version | Platform | Author | Notes |
|---|---|---|---|
| _(none listed yet)_ | | | |

## Build Your Own Bridge

Any program that implements the [Bridge Protocol](protocol/BRIDGE_SPEC.md) is compatible with TLADe. The protocol is 4 HTTP endpoints — you can write a bridge in any language.

See [`templates/bridge_template.py`](templates/bridge_template.py) for a minimal skeleton.

## Contributing

There are three ways to contribute, and you can pick whichever suits you.

**Improve an existing integration.** Fork, make your changes, open a pull
request. We read them; if a change makes the integration better for everyone,
it goes into the TLADe-patched build and you are credited in the table above.

**Publish your own version.** You don't need our permission — MIT lets you fork,
rename and publish under your own name, and support it yourself. Open an issue
if you want it listed under *Independent versions* so users can find it.

**Build a bridge for a feed nobody covers yet.**

1. Read the [Bridge Protocol Spec](protocol/BRIDGE_SPEC.md)
2. Use the [template](templates/bridge_template.py) as a starting point
3. Test with your TLADe terminal (it auto-detects `localhost:5000/health`)
4. Open a PR

### How your work is treated

If you publish an integration here, it stays **yours**. We list it, we point
users to it, and we say who wrote it — but we don't take it over.

If at some point we want to modify it — because the data format changed, or a
fix benefits every user — **we ask you first**. If you agree, the patched build
moves into the TLADe-maintained set: we take on the data feed and keep it in
sync, and you stay credited as its author, with your original preserved in the
integration's `original/` folder. If you'd rather keep it independent, that's
fine too — nothing changes.

### What TLADe guarantees

For the integrations in the two tables at the top — TLADe-built and
TLADe-patched — we guarantee **the data feed behind them**: the levels they
receive are the same levels the terminal shows, and we keep them in sync when
the payload format changes. What we do not cover is how a platform we don't
trade on renders them: chart behaviour, drawing quirks and platform-specific
bugs stay with the code's author, who is credited in the table.

For everything under *Independent versions*, we guarantee nothing — we haven't
looked at it.

## Requirements

- A TLADe subscription ([tradelikeadealer.com](https://tradelikeadealer.com))
- A local data feed (your broker account + platform)
- Python 3.8+ (for Python bridges) or .NET (for NT8 bridges)

## License

MIT — use, modify, and distribute freely.
