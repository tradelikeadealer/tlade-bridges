# TLADe Bridge ATAS

> **Credits.** ATAS-side end-to-end integration contributed by **[Mihai Ostafe](mailto:mihaiostafe@gmail.com)** — three indicators, Python receiver, autostart workflow, this README. Reviewed, tested and published by the TLADe team.
> Released under the **MIT License** — see SPDX headers in each source file.

ATAS-side integration with the TLADe / TradeLikeaDealer ecosystem.
One DLL (`TLAdeBridgeATAS.dll`) ships three indicators:

1. **TLADe Bridge** — pushes live price / bar / delta from ATAS to a local Python bridge that the TLADe Terminal listens to.
2. **TLADe GEX Dashboard** — pulls Gamma Exposure walls and system levels directly from the TLADe Cloud and draws them as horizontal lines on the chart.
3. **TLADe Quantum Field Ladder** — draws Zero-Gamma and Call / Put-Wall levels as gradient force-field bands.

All three live in the single all-in-one `TLAdeBridgeATAS.dll`, built from the `TLAdeBridgeATAS.csproj` in this directory.

## Platforms

This directory is the **Windows official build** — currently at `TLAdeBridgeATAS.cs` v2.4.4 / `tlade_bridge_atas.py` v1.3.0. It is the stable line published on top of Mihai Ostafe's v2.4.0 baseline, with all his credits preserved. The build is what Windows users have been running in production and is **not modified** when a Mac-specific issue is patched.

> **Mac users:** a separate, community-maintained Mac port lives in [`mac/`](./mac/). It is a derivative work — different `.csproj`, different DLL, with its own patch history (Krzysztof's Mac port + Cornel's defensive OHLC guards). The Windows build above is not affected by what happens in `mac/`.

## Patch notes

### Dashboard v3.1.1 — Session AVWAP locally computed (Asia / EU / US / Prev Day)
Four anchored-VWAP polylines now drawn directly by the indicator from the chart's own bars — same formula as the TLADe TradingView Pine, no round-trip through the cloud. Source = `hlc3 * volume` cumulative, reset at each session anchor in ET:
- **Asia** (amber) — anchored at 18:00 ET (= futures day start)
- **EU** (blue) — anchored at 02:00 ET
- **US** (green) — anchored at 09:30 ET RTH open
- **Prev Day US** (dim green) — the previous trading day's US AVWAP, promoted at the futures-day rollover and held for the rest of the day

New settings group **"5. Session AVWAP"**: individual on/off toggle for each polyline, line width, label visibility, and a "Show Historical AVWAP (prev days)" switch (off by default = only the current futures day is drawn, keeping the chart clean).

Live-tick safety: when ATAS re-invokes `OnCalculate` on the current bar tick-by-tick, the indicator snapshots the accumulators at the bar's first computation and rolls them back on subsequent calls so `hlc3*vol` is counted exactly once per bar — not once per tick.

> **VP (POC/VAH/VAL)** is still kept out of the indicator on purpose. ATAS's native `VolumeProfile` study computes it from local bars cleanly; stack it alongside the TLADe Dashboard for that view.

### Dashboard v3.1.0 — Tick-independent scheduler + Breakout / Charm Magnet
Two changes on the GEX Dashboard indicator:

**1. Sync fix (= "I have to remove and re-add the indicator to get an update")**

v3.0 drove the 6 daily auto-fetches from inside `OnCalculate`. That callback only fires when a tick arrives, so:
- On cash-index charts (SPX/NDX), zero ticks outside RTH → all 3 pre-market slots (ASIA 18:05, EU 02:05, PRE 08:05 ET) were silently skipped.
- On futures charts (ES/NQ), Globex quiet hours can have ticks ≥ 6 min apart, easily wider than the legacy 5-minute "did we just pass a slot?" window.

v3.1 introduces a real `System.Threading.CancellationTokenSource`-backed scheduler started in `OnInitialize` and torn down in `OnDispose`. It:
- runs **independently of OnCalculate** (= zero dependency on tick rate)
- fires **exactly 7 fetches per trading day**: 1 at mount + 6 absolute ET slots (02:05 EU, 08:05 PRE, 09:35 RTH, 10:35 OR, 13:05 CLOSE, 18:05 ASIA) — *until 3.5; see 3.6.0 below*
- correctly picks the *next ascending* slot regardless of the time the indicator is added (the v3.1.0-pre subtle bug — slot array iteration order — was caught in testing and fixed before release).

`tlade_gex.log` shows the scheduler lifecycle (`Scheduler: mount fetch`, `Scheduler: catch-up fetch (held … expected …)`).

**3.6.0 (22 October 2026):** the fixed slots are gone. Once a minute the scheduler compares the publication the levels come from (the `T:` row of the data string) with the one that should exist at the current ET time, and fetches when the held one is older — 5 minutes apart for three tries, then every 15. The status badge names the publication (`levels: 2026-10-21 OPENING_RANGE 10:30 ET`) and turns red with `STALE: expected …` when behind. The Quantum Field Ladder now uses the same scheduler (until 3.5 it fetched from `OnCalculate`, so a quiet chart missed its slots). Shared rule: `TLAdePublication.cs`.

**2. Two new level families in the payload**

The cloud `indicatorData` endpoint was extended to emit the two on-chain-derived levels the terminal renders that the dashboard didn't have:

- **`CM` — Charm Magnet** (violet line). The strike where charm flow magnetises price, surfaced from the volatility cascade. Matches the "Magnete Charm" panel in the terminal's Market Pulse. Present in the payload when the snapshot's `magnete_charm.tipo` is `Magnet` / `Pressure` / `Neutral` (skipped when `N/D`).
- **`BL` / `BS` — Breakout Areas** (emerald / rose lines). Recent breakout zones detected by the PA engine. `BL` = long breakout, `BS` = short breakout.

Two new toggles in the **3. Visibility** group: `Show Breakout Areas (BL/BS)` and `Show Charm Magnet (CM)`.

> **Volume Profile and Session AVWAP** were intentionally **NOT** added to the payload. ATAS already ships native `VolumeProfile` and `VWAP` studies that compute these from the chart's own bars — stack them alongside the TLADe Dashboard on the same chart for the same view the TLADe terminal renders. Same pattern as Pine on TradingView.

### Bridge v2.4.1 — Closed-bar timestamp fix
The Bridge Protocol §chart_data expects `time` as Unix seconds (integer); the previous version was sending ISO strings, which the terminal parsed as NaN and collapsed every closed 5m bar to a degenerate O=H=L=C tick on the chart. `PostBar()` now emits `time` as `((DateTimeOffset)cd.Time).ToUnixTimeSeconds()`.

### Dashboard / Quantum v3.0.1 — Strike normalization fix
The cloud `indicatorData` endpoint already returns strikes in the futures price space (ES for the SPX family, NQ for the NDX family). v3.0.0 was double-applying the spread (`rawStrike - EffectiveEsSpread`), causing levels to appear ~19 points below the TLADe Terminal on ES. v3.0.1 uses the strikes as-is. If you ever see a constant offset between ATAS and the terminal on a future Mother-side change, the indicator still exposes the `Manual spread override` setting (use a tiny positive value like `0.001` to bypass conversion entirely).

---

## Architecture

```
Rithmic / CQG market data
        │
        ▼
   ATAS Platform
        │
        │  (chart indicators load from %APPDATA%\ATAS\Indicators\TLAdeBridgeATAS.dll)
        │
        ├── TLADe Bridge indicator  ─── HTTP POST ──▶  tlade_bridge_atas.py (Flask, localhost:5000)
        │                                                       │
        │                                                       ▼
        │                                               TLADe Terminal (auto-detects bridge)
        │
        ├── TLADe GEX Dashboard     ───────HTTPS────▶  https://europe-west1-omggex.cloudfunctions.net/indicatorData
        │
        └── TLADe Quantum Field Ladder ────HTTPS────▶  same cloud endpoint
```

The local Python bridge (`tlade_bridge_atas.py`) is needed only for the **Bridge** indicator's terminal integration. The two visual indicators (GEX Dashboard, Quantum Field Ladder) fetch directly from the cloud and do not depend on the bridge.

---

## Prerequisites

- ATAS Platform installed at `C:\Program Files (x86)\ATAS Platform\`
- .NET 10 SDK (the project targets `net10.0-windows`)
- Python 3.8+ on PATH (only if you use the Bridge indicator)
- A TLADe API key (free or paid). Free mode also works but data is delayed by 3 days.

---

## Build & Install

From the project directory:

```powershell
dotnet build TLAdeBridgeATAS.csproj -c Release
```

The build target copies the DLL to `%USERPROFILE%\Documents\ATAS\Scripts\` automatically, but ATAS loads indicators from a different folder. **Always copy the DLL to the load folder manually:**

```powershell
Copy-Item "$env:USERPROFILE\Documents\ATAS\Scripts\TLAdeBridgeATAS.dll" `
          "$env:APPDATA\ATAS\Indicators\TLAdeBridgeATAS.dll" -Force
```

> **Important:** ATAS keeps the DLL locked in memory once any chart loads an indicator from it. You **must close ATAS completely** before overwriting the file, otherwise the copy will fail or, worse, ATAS will hang mid-session. Workflow:
> 1. `dotnet build -c Release` (safe; writes to `bin\Release\`)
> 2. Close ATAS (File → Exit)
> 3. Copy DLL to `%APPDATA%\ATAS\Indicators\`
> 4. Re-open ATAS, add the indicator fresh

---

## API Key Setup

Both visual indicators auto-load the TLADe API key from either:

- `%USERPROFILE%\Documents\ATAS\tlade_apikey.txt`
- `%APPDATA%\ATAS\tlade_apikey.txt`

Create one of those files with just the key inside (single line, no quotes). With a key, you get live data; without, indicators fall back to `mode=free` (3-day delayed).

You can also paste the key directly into each indicator's settings (`1. Source → API Key`), but the file is more convenient if you re-add the indicator often.

---

## Starting the Local Bridge

Required only for the **TLADe Bridge** indicator (price push to TLADe Terminal).

```cmd
start.bat
```

That checks Python / `flask` / `flask-cors`, verifies port 5000 is free, and runs `python tlade_bridge_atas.py`. Leave the window open while trading.

**Auto-start on logon** (already configured): there's a shortcut at
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\TLADe Bridge ATAS.lnk`
pointing to `start.bat`. Delete the shortcut to disable autostart.

---

## Indicator 1 — TLADe Bridge

**Purpose:** Streams ATAS market data (live price, M5 bars, daily bars, delta, bid/ask volume) to the local Python bridge over HTTP, where the TLADe Terminal picks it up.

**Add to chart:** Indicators → search "TLADe Bridge". Add **one** instance per chart only.

**Endpoints used:**

| Endpoint | Direction | Purpose |
|---|---|---|
| `POST /push_spot` | ATAS → bridge | Live tick (throttled ~2s) |
| `POST /push_bar` | ATAS → bridge | Closed M5 bar (OHLCV + delta + bid/ask vol) |
| `POST /push_daily` | ATAS → bridge | Daily bar (D1) at session rollover |
| `GET /health` | terminal → bridge | Liveness |
| `GET /ib_data?ticker=ES` | terminal → bridge | Real-time chart data |

**Ticker normalization:** any symbol containing `NQ` / `NDX` / `MNQ` is mapped to `NQ`; everything else to `ES`.

**Settings of note:**
- *Port* — must be `5000` (TLADe Terminal hardcodes this).
- *Throttle ms* — minimum interval between spot pushes (default 2000ms).

**Log:** `%USERPROFILE%\Documents\ATAS\tlade_bridge.log`

---

## Indicator 2 — TLADe GEX Dashboard

**Purpose:** Draws GEX walls (CW = Call Wall, PW = Put Wall) and system levels (ZG = Zero Gamma, MP = Max Pain, EH/EL = Expected Move High/Low, VH/VL = Volatility Bands, PDH/PDL/PWH/PWL = previous session structure) as horizontal lines on the chart, refreshed automatically.

**Auto-refresh:** one fetch when the indicator is added to the chart, then a check every minute: when the publication the levels come from is older than the one expected at the current ET time (ASIA 18:01 the evening before, EUROPE 03:15, PRE_OPEN 08:00, RTH_OPEN 09:30, OPENING_RANGE 10:30, CLOSE 13:00), it fetches again. The status badge names the publication and says STALE while it is behind.

### Key Settings

**Group "1. Source"**

| Setting | Purpose |
|---|---|
| API Key | Your TLADe key (auto-loaded from file if present) |
| Auto-fetch din TLADe Cloud | Default ON. OFF disables scheduled fetches. |
| Manual data string (override) | Paste a full `S:…\|L:…\|P:…` string to override the cloud. While non-empty, auto-fetch is ignored. Compose the string in Notepad and paste in one go — typing keystroke-by-keystroke wastes CPU and used to wipe levels (defensive parse prevents that now). |

**Group "2. Layout"**

| Setting | Purpose |
|---|---|
| Ticker display | `auto` (detect from chart instrument), `ES`, `SPX`, `SPY`, `NQ`, `NDX`, `QQQ`. Set to `SPX` if you want labels to match what TLADe Terminal shows (raw SPX strikes); set to `ES` to convert SPX strikes to ES futures coordinates. |
| ES↔SPX spread (auto from S:) | Spread read from the `S:` header in the cloud response. Used when ticker is `ES`. |
| NQ↔NDX spread | Default 40. Used when ticker is `NQ`. |
| Manual spread override (0 = auto) | If > 0, this value replaces the auto-detected spread. Use this to dial in an exact match against TLADe Terminal. |
| Snap la tick-ul instrumentului | Default ON. Rounds levels to the chart's tick grid (e.g. 0.25 for ES) so lines land cleanly between price ticks. |

**Group "3. Visibility"**

Toggles for each level family (GEX walls, system, structure), plus labels, axis markers, and the status badge.

**Group "4. Filter"**

| Setting | Purpose |
|---|---|
| Max GEX walls | How many CW/PW lines to draw. `999` = all. Half above spot, half below. The closest wall to spot above and below is always drawn ("protected"). |
| Enable threshold filter | When ON, walls with magnitude under the threshold are hidden. |
| Threshold magnitude (M) | The cutoff in millions of GEX. |

**Group "5. Style"**

| Setting | Purpose |
|---|---|
| Line width GEX | Thickness for walls. |
| Line width System/ZG/MP | Thickness for system levels (usually thicker). |
| Font size label | Label text size. |
| Scale wall lines by magnitude | Default ON. CW/PW lines have variable length anchored to the right edge; line length is proportional to magnitude. System lines remain full width. |
| Wall line max width (% chart) | Upper bound of the scaled wall length. |
| Wall line min width (% chart) | Lower bound — even the smallest wall is at least this long. |
| Label text inherits line color | Default ON. OFF lets you force a single text color (useful when red labels are hard to read on your background). |
| Label text color (when not inheriting) | The override color. |
| Label background opacity (0-255) | Label background alpha. |
| Differentiate label background above/below spot | OFF: background is derived from the line color (dimmed). ON: levels above spot use `LabelBgAbove`, below spot use `LabelBgBelow`. |
| Label background - above spot | Default bright red `#dc2626`. |
| Label background - below spot | Default bright green `#16a34a`. |

**Status badge:** the top-left badge shows `TLADe GEX [LIVE\|FREE] levels=N`. Confirms data mode and level count.

**Log:** `%USERPROFILE%\Documents\ATAS\tlade_gex.log` — every fetch, parse, and OnRender error is recorded.

---

## Indicator 3 — TLADe Quantum Field Ladder

**Purpose:** Same data source as GEX Dashboard, but renders levels as **gradient force-field bands** instead of lines. Cyan bands = ZG (magnet), magenta bands = CW/PW (walls). Band intensity scales with GEX magnitude.

Only ZG / CW / PW are rendered — system levels and structure are omitted (use the GEX Dashboard for those).

### Key Settings

**Group "2. Layout"**

Same conversion controls as GEX Dashboard (Ticker, spreads, snap, manual override).

**Group "3. Render"**

| Setting | Purpose |
|---|---|
| Max levels | How many bands to draw, ranked by distance to spot. |
| Band width (ticks) | Total band thickness in chart ticks. |
| Gradient layers | How many concentric strips form each band (odd 3–11). More = smoother gradient. |
| Center line width | Thickness of the bold center line. |
| Benzi sub lumanari (chart in prim-plan) | Default ON. Bands render on the `Historical` layer (under candles); the center line and label always render on `Final` (over candles). OFF puts everything over candles — Quantum in foreground. |
| Show labels | Show the `<price> MAGNET\|WALL` label per band. |
| Show status badge | The top-left mode/count badge. |
| Font size label | Label text size. |

**Group "4. Colors"**

- `Magnet color (ZG)` — default cyan.
- `Wall color (CW/PW)` — default magenta.
- Label text inheritance, override color, background alpha, and positional above/below background — same semantics as on the GEX Dashboard.

**Log:** `%USERPROFILE%\Documents\ATAS\tlade_quantum.log`

---

## Troubleshooting

**"Indicator added but nothing draws."**
Check the log file for the indicator (`tlade_gex.log` / `tlade_quantum.log`). Look for the most recent `PARSED levels=N` line; if N > 0 the data is fine and the issue is rendering. Verify:
- The chart instrument matches a supported scale (see "Ticker display" setting).
- Threshold filter isn't hiding everything (try OFF).
- `MaxGexLevels` isn't set absurdly low.
- The DLL in `%APPDATA%\ATAS\Indicators\` is actually the latest build (check timestamp).

**"OnRender ERR" or "OnCalculate EX" in the log.**
Read the full exception. Common cause: chart not yet fully initialized (`ChartInfo == null`) — the code already guards against this; a transient error during chart switching is usually harmless.

**"FETCH … HTTP 401/403."**
API key invalid or rate-limited. Verify `Documents\ATAS\tlade_apikey.txt` contents.

**"FETCH … HTTP 200 bytes=0."**
Cloud temporarily empty. Wait for the next scheduled fetch or trigger a refresh by toggling any setting on the indicator.

**Lines visible but at wrong price.**
The `Ticker display` setting is mismatched with your chart instrument. With `auto`, the code looks at `InstrumentInfo.Instrument` and picks NQ/NDX/QQQ when the name contains `NQ`/`NDX`/`MNQ`, otherwise ES family. Override manually if needed.

**Labels match TLADe Terminal but with a small fractional offset (~0.13–0.46).**
The cloud returns integer strikes (e.g. 7383). TLADe Terminal applies a per-strike basis adjustment we can't reproduce exactly without their option-chain data feed. The closest match is `Ticker display = SPX` with the snap option enabled — your values will be within 0.5 of TLADe Terminal.

**ATAS hangs after a DLL replace.**
You almost certainly copied the DLL while ATAS was running. Kill ATAS via Task Manager, then re-copy with ATAS fully closed.

**Bridge running and `/health` responds, but TLADe never switches to live data (Chrome).**
Chrome's **Local Network Access** policy (rolling out progressively) blocks HTTPS websites from reaching `localhost`, so the terminal's requests to the bridge are silently blocked. Fix: open `chrome://flags/#local-network-access-check`, set **Local Network Access Checks** to **Disabled**, restart Chrome completely. The flag is version-dependent (it may be renamed or removed after a Chrome update — suspect this first if the bridge stops connecting right after updating) and disables the check browser-wide. Alternative: Firefox does not enforce this policy the same way and connects out of the box.

---

## File Reference

| File | Role |
|---|---|
| `TLAdeBridgeATAS.cs` | Bridge indicator source (push to local Python). |
| `TLAdeGexDashboardATAS.cs` | GEX Dashboard source. |
| `TLAdeQuantumFieldLadder.cs` | Quantum Field Ladder source. |
| `TLAdeBridgeATAS.csproj` | Single project that builds all three into one DLL. |
| `tlade_bridge_atas.py` | Local Flask receiver (port 5000) and `/get_levels` / `/ib_data` server. |
| `tlade_auto_fetch.py` | Optional helper that pulls GEX into the bridge. |
| `tlade_push_levels.py` | Optional helper to manually push level data into the bridge. |
| `start.bat` | Launches the bridge, checks Python and dependencies. |
| `Other platforms engines/Trading view/` | Pine source and screenshots from the original TradingView indicator (reference only). |
| `Other platforms engines/TLADeGexDashboardNinja Trader/` | NT8 reference port. |
| `tlade-bridges-main/BRIDGE_SPEC.md` | The TLADe bridge HTTP protocol spec. |
| `AI_NOTES.md` | Working notes on ATAS Bridge internals, port choices, threading model, and known bugs. |

---

## Versions

- Bridge indicator: v2.4.x (Channel-based worker, dedup via `ConcurrentDictionary`).
- GEX Dashboard: v3.0.0 (OnRender-based, post-LineSeries rewrite).
- Quantum Field Ladder: v3.0.0 (OnRender-based, multi-layer gradient via `FillRectangle`).

The `v3.0` rewrite of the two visual indicators replaced the `LineSeries.Add()` approach (which silently failed to render) with `EnableCustomDrawing` + `SubscribeToDrawingEvents(DrawingLayouts.Final)` + a real `OnRender` override. Reference pattern: `D:\Automatizare Atas\Cornel\ESStrategy_Dalton.cs`.
