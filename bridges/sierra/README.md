# TLADe for Sierra Chart — beta

Two ACSIL studies for [Sierra Chart](https://www.sierrachart.com):

| Study | Who | What it draws | Download |
|---|---|---|---|
| **TLADe GEX Dashboard** (this folder) | Built and maintained by TLADe | The TLADe indicator, as on ATAS, NinjaTrader and MotiveWave: GEX walls, Zero Gamma, Max Pain, Expected Move, Vol Bands, Delta Flip, Charm Magnet, GEX profile, plus session AVWAPs, Worldlines H1, session boxes, breakout levels, previous day/week high/low and confluence zones from your own bars | `TLADe-SierraChart.zip` on the [latest release](https://github.com/tradelikeadealer/tlade-bridges/releases/latest) |
| **TLADe Quantum Field Ladder** ([`alex/`](alex/)) | Written by Alex, community contribution | The levels as magnet/wall fields | `TLADe-SierraChart-QuantumLadder-Alex.zip` on the same release |

Each user authenticates with **their own TLADe API key**. Never share a Chartbook that holds it.

## Install

1. Close Sierra Chart.
2. Copy `TLADe_GEX_Dashboard_64.dll` into your Sierra Chart `Data` folder (for example
   `C:\SierraChart\Data`), then start Sierra Chart.

   Or build it yourself: copy `TLADe_GEX_Dashboard.cpp` into `ACS_Source`, then
   **Analysis → Build Custom Studies DLL → File → Select Files → Build → Remote Build**.
3. Open an **intraday** ES or NQ chart, 1 to 60 minutes (the terminal uses 5). Above one hour the
   study draws nothing unless *Draw on timeframes above 1H* is on.
4. **Analysis → Studies → Add Custom Study → TLADe GEX Dashboard**.
5. Paste your API key in the first input. Without a key the study runs in FREE mode, with data
   three days old.
6. On a cash or ETF chart set **Ticker** to SPX, SPY, NDX or QQQ: the study takes the spread off
   (`S:`) and divides by the ratio (`R:`) of the data string. *auto* reads ES or NQ from the symbol.

The status in the top-left corner says which publication the levels come from, and **STALE** when a
newer one should exist. The study checks once a minute and fetches only when a newer publication is
due. During replay it does not fetch.

## What is the same as the other platforms

It is a port of the ATAS edition 3.6.1 (`bridges/atas/TLAdeGexDashboardATAS.cs`), the Worldlines
come from the TradingView indicator: same level names, colours, line styles, wall-flip rule (two
5-minute closes), breakout rule (closed bars, opposite-bar invalidation, keep the last N), session
AVWAP rule (Asia 18:00, EU 03:00, US 09:30 ET, all re-anchored at the 18:00 ET rollover), session
boxes, confluence zones, profile bars, themes and alerts. The canonical rule for every calculation
is the TLADe terminal.

Sierra-specific:

- Bar times are converted from the chart's time zone to New York, so the sessions are right
  whatever time zone the chart uses.
- A flipped wall reads "flipped" instead of the ↺ sign, which Sierra's text drawing cannot render.
- The input values are not printed next to the study name on the chart: they include the API key,
  and every screenshot would publish it.
- The Message Log is written to without ever opening its window.

## Beta

New on 28 October 2026 and tested on one installation. The levels it receives are the levels the
terminal shows; how a Sierra Chart setup renders them is what the beta is for. Send a screenshot if
something looks wrong.

## Credits

The WinHTTP fetch on a background thread, the API key wiped from memory after use, the New York clock
from the Windows time zone database, the HTTP 401/403/429 handling and the fetch status in the corner
come from Alex's study in [`alex/`](alex/). Thanks, Alex.
