# TradingView — TLADe Pine Indicators

**Status: Ready · Pine v6 · TLADe-built**

Three Pine v6 indicators that overlay TLADe levels, GEX profile,
breakout structure, session AVWAPs, confluence zones, and the Quantum
Field Ladder directly on your TradingView chart.

## What's in this folder

| File | What it does |
|---|---|
| [`gex_es_spx_spy.pine`](gex_es_spx_spy.pine) | GEX Levels + BOS + Session AVWAP + Confluence — ES futures + SPX index + SPY ETF (ticker switch in the indicator settings). Source for the published TradingView script. |
| [`gex_nq_ndx_qqq.pine`](gex_nq_ndx_qqq.pine) | Same as above for the Nasdaq family: NQ futures + NDX index + QQQ ETF. |
| [`quantum_field_ladder.pine`](quantum_field_ladder.pine) | Quantum Field Ladder — gravitational view of dealer positioning rendered as a horizontal ladder of nodes around price. |

## Published versions

If you just want to *use* the indicators (no contribution), add them
directly from TradingView — no setup, no compile, just paste the live
GEX data string in the indicator settings:

- GEX ES/SPX/SPY: https://www.tradingview.com/script/FnH0YWPK/
- GEX NQ/NDX/QQQ: https://www.tradingview.com/script/Ry5ZZ6Y2/
- Quantum Field Ladder: https://it.tradingview.com/script/n0YSr7so/

The sources in this folder are kept in sync with the published
scripts (single source of truth) so external contributors can review
the exact code that's running on TradingView, propose changes, or
fork them for their own use.

## How the GEX data feed works

Each indicator parses a single text string the trader pastes from the
TLADe terminal ("Copy GEX Data" button), formatted as:

```
S:<spread>|L:<levels>|P:<profile>
```

- **`S:`** — futures-vs-cash spread (ES-SPX or NQ-NDX). The
  indicator uses it to convert published ES/NQ strikes back to SPX/NDX
  or SPY/QQQ when those tickers are selected. **Not hardcoded** — if
  the prefix is missing the convert function falls back to identity
  (ES strikes shown unchanged). No magic numbers.
- **`L:`** — list of structural levels: walls (`CW`/`PW`), system
  markers (`ZG`, `MP`, `EH`, `EL`, `VH`, `VL`), and PA structure
  (`PDH`/`PDL`/`PWH`/`PWL`) with their tooltips and magnitudes.
- **`EMR`** (inside `L:`) — the odd one out: its first field is a
  **width in points, not a price**. It is the Expected Move range of the
  RTH session, and the indicator anchors it itself at the open of the
  09:30 ET bar on your chart. The publisher cannot do it: it builds the
  data ten minutes before its own session clock, so at that moment the
  09:30 open does not exist yet. On a chart with no bar starting exactly
  at 09:30 — 1h, 4h, daily — the band is not drawn at all, rather than
  drawn on an approximate open.
- **`P:`** — per-strike GEX profile values, drawn as a histogram on
  the right side of the chart.

Same contract used by every other TLADe indicator integration
(NinjaTrader 8, ATAS, MotiveWave). If you build a bridge or
indicator for another platform it can plug into the same string.

## Contributing

If you want to propose a change to one of the Pine sources — a
new visualisation, a UI input, a confluence-zone variant, a
breakout-structure refinement — the workflow is:

1. Fork this repo.
2. Edit the `.pine` source in `bridges/tradingview/`.
3. Test it on TradingView's Pine editor (paste your edited source
   into a fresh chart, verify it compiles + renders cleanly).
4. Open a PR with a short description of what changed and why.
   Screenshot before/after is appreciated for visual changes.

Style rules to keep PRs mergeable:

- **Pine v6 only.** `//@version=6` at the top, no v5/v4 syntax.
- **No hardcoded constants where data should drive.** Spreads,
  thresholds, anchors must come from the input string or from a
  user-editable input — never magic numbers in the source.
- **Performance.** Pine is forgiving but a 500+ levels chart can
  still slow down. Keep loops bounded and avoid recomputing on
  every bar what can be cached.
- **Comments in English.** Code stays in English even when the
  contributor is non-English-speaking — TradingView's audience is
  global.

For larger ideas (e.g. an entirely new indicator concept) open an
issue first so we can align before you invest the time.

## Build / test (local Pine workflow)

There's no build step — Pine compiles inside TradingView. The
typical contributor workflow is:

1. Open a TradingView chart.
2. Click **Pine Editor** at the bottom.
3. Open the script you want to edit (or paste from this folder).
4. Make your changes.
5. Click **Save** → **Add to chart** to test.
6. When ready, copy the edited source back into your forked
   `bridges/tradingview/*.pine` and open the PR.

## Questions?

📧 support@tradelikeadealer.com
