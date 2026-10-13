# Changelog

There is one published release, tagged `latest`, and it is always the current build.
This file is its history: what changed, and what is known to be open.

Entries are added when a build is published. A bridge or indicator with no entry
has not changed since the one below it.

---

## NinjaTrader 8, ATAS, MotiveWave — TLADe GEX Levels

### 3.5.2 — 13 October 2026

**Fixed — SPY levels were 2.6 points off.**
On a SPY chart the three indicators converted the index price to the ETF by dividing by
10 — a number written into the code. SPX and SPY do not trade at exactly ten to one: on
13 October the real ratio was 10.034, which puts every level about 2.6 points below where
it was being drawn, every wall and every band together. QQQ was already using the measured
ratio from the data string; SPY now does the same.

The ratio travels in the data string as `R:`, immediately after `S:`, for both families:
`R:10.034` on an ES string, `R:41.103` on an NQ one. The same field carries both, so a band
of 9.0–12.0 is enforced before it is applied to SPY — it stops an NQ string loaded while
looking at a SPY chart from dividing by 41. Outside that band the old divisor of 10 is used,
so an older data string keeps working exactly as before.

**To get it:** download the build again. The ratio reaches the indicator on its own through
auto-fetch; with a pasted data string, copy it again from the terminal so it carries `R:`.
Nothing else changes, no setting moves.

**Also in the feed — a session marker.**
The data string and the API now open the profile section with one extra row:
`T:<date>,<session>,<time ET>`, which says which publication the levels come from. The
indicators ignore it: it is not a level and not a profile row, and every parser skips a row
whose first field is not a number. It is there to be read by a human comparing a chart
against the terminal, and by anything that archives the string.

**Known open:** the Quantum Field Ladder (TradingView and ATAS) still divides by 10 for SPY
and 40 for QQQ. Its own data string does not carry `R:` yet, so the fix above does not reach
it; its levels on an ETF chart keep the old offset.

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
