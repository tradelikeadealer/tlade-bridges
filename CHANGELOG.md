# Changelog

There is one published release, tagged `latest`, and it is always the current build.
This file is its history: what changed, and what is known to be open.

Entries are added when a build is published. A bridge or indicator with no entry
has not changed since the one below it.

---

## NinjaTrader 8 — TLADe GEX Levels indicator

### 3.5.1 — 7 October 2026

**Fixed — levels missing on SPY and QQQ charts.**
With ticker auto-detect on, a SPY chart was mapped to ES and a QQQ chart to NQ, so the
levels arrived in futures scale and were drawn hundreds of points away from a cash chart.
They were not absent, they were off-screen — which looked exactly like the indicator doing
nothing. The data family (which feed the levels come from) and the display scale (what the
chart is priced in) are now two separate decisions, and a chart whose scale cannot be
derived draws nothing at all rather than drawing numbers in the wrong scale.

The rule this follows is the one the whole platform follows: levels are either correct or
absent, never silently wrong.

**Fixed — the download was three weeks stale.**
`TLADeGexDashboardNT.zip` is the file the Indicators page in the terminal links to, and it
is the only place to get this indicator. It used to be uploaded by hand, and had not been
rebuilt since 13 September, so fixes published in between never reached anyone through the
normal path. It is now built by CI on every commit, from the source in this repository.

**Known open:** nothing reported against this build at the time of writing.

### 3.5.0 — 13 September 2026

The build this one replaces. Feature set aligned to the TradingView ES indicator: same
level names, colours and line styles, the same wall-flip rule (two consecutive 5-minute
closes beyond the strike), the same breakout rule (closed bars, opposite-bar invalidation),
confluence zones as a percentage of the expected move, and level-cross alerts.
