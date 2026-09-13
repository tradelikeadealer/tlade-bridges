# CHANGELOG — MotiveWave Indicator

## 2026-09-13 — Release 3.5.0: the canonical set

`TLADeGexDashboard.java` brought to the same feature set as the TradingView
indicator 3.5.0 and frozen there (bug fixes only from now on):

- One name per level (Call Wall, Put Wall, Zero Gamma, Max Pain, EM High/Low
  Globex and RTH, Vol High/Low, Charm Magnet, Delta Flip); TradingView palette.
- Wall flip on two consecutive 5-minute closes through the wall: colour by
  nature, width minus one, `↺` marker.
- Breakout structure on closed bars (M / W / D / H4 / H1), invalidated by an
  opposite bar of the same timeframe closing through it; keep N per timeframe.
- Confluence zones sized as a percentage of the Expected Move (default 7%).
- Session AVWAPs with labels, session boxes (Asia / Europe / Pre / US RTH).
- Level-cross signals (walls, system levels, breakouts) via MotiveWave signals.
- Silent above 1H unless "Draw on timeframes above 1H" is on.
- Defaults aligned with TradingView: walls, system levels, Delta Flip, profile,
  AVWAP on; structure, Charm, boxes, confluence, breakouts, alerts off.

Previous jar (v1.3.4) kept on the GitHub release `indicators-pre-3.5`.
## 2026-07-02 — Fetch error surfacing + delayed workspace-open kick (v1.3.4)

Two changes on `TLADeGexDashboard.java`, both diagnostic and both
triggered by a case where `v1.3.3` on macOS Java 25 rendered the
banner as `LIVE · fetching… · 0 levels / updated — ET` indefinitely,
with no way to tell if the problem was a wrong API key, a network
issue, or a stuck fetch thread.

### 1. Delayed initial fetch on workspace-open

`v1.3.3` kicked `startFetch(true)` synchronously from the first
`calculateValues` invocation, so the HTTPS handshake happened inline
with MW's first render pass. On macOS Java 25 that path appeared
to leave the fetch thread hung in the transport layer with
`fetchInFlight = true` and no completion ever reaching the
`finally`. `onLoad` (the drop-fresh path) never showed the problem.

v1.3.4 keeps the `initialFetchKicked` guard but schedules the fetch
`1.5 s` after the first paint via the study's own
`ScheduledExecutorService`, so the fetch thread starts after MW has
completed its first render pass. `onLoad` stays synchronous.

### 2. Fetch error surfaced in the status banner

Two silent-fail paths in `startFetch` used to leave the banner
frozen on `updated — ET` with `0 levels`:

- HTTP response received but body did not contain the `L:` payload
  marker (e.g. 401 invalid key, 403 forbidden, 429 rate-limited,
  maintenance JSON) — the success branch was gated on
  `data.contains("L:")` with no `else`.
- `catch (Exception ignore)` swallowed SSL / DNS / socket errors.

Both now write to a new `lastFetchError` field, and `buildStatus`
renders that string in place of the "updated … ET" line until the
next successful fetch clears it. Examples the user will see:

- `ERR: HTTP 401 · {"error":"invalid api key"}`
- `ERR: HTTP 429 · Rate limit exceeded`
- `ERR: net: SSLHandshakeException · PKIX path building failed…`
- `updated 10:32 ET`  ← after success

`System.err.println` also fires on both paths, so users who open the
MotiveWave console see the same info in logs.

Single jar still covers macOS + Windows + Linux (classfile major 69).

## 2026-06-30 — macOS compatibility — bytecode downgraded to Java 25 (v1.3.2)

`dist/TLADeGexDashboard.jar` recompiled with `--release 25` so it loads
on **MotiveWave 7.0.22 onwards on macOS**, which ships a Java 25 runtime
(downgraded from Java 26 in 7.0.22 because of macOS crashes — see
MotiveWave 7.0.27 release notes, 2026-06-29). The prior v1.3.1 jar was
class file version 70 (Java 26), so the macOS JVM rejected it with
`UnsupportedClassVersionError` and users reported "the indicator
doesn't load at all".

No source changes. The TLADeGexDashboard source already targets only
Java 25-compatible APIs; the v1.3.1 jar was Java 26 simply because that
was the JDK on the build machine. Cross-compile via
`javac --release 25 -classpath mwave_sdk.jar -d build TLADeGexDashboard.java`
produces classfile major 69, which loads on both MW macOS (Java 25)
and MW Windows / Linux (Java 26, forward-compatible).

Single jar still covers all three OSes — no need to ship Mac/Win
separately.

## 2026-06-14 — Status banner redesign + v1.3 build fix (v1.3.1)

Two changes on `TLADeGexDashboard.java`.

### 1. Build fix — v1.3 never compiled, so the shipped jar was stale

The v1.3 commit called `computeAvwapForSeries(series)` from inside
`buildDrawModel(double spot, DataContext ctx)`, where `series` is not in
scope (it lives in `calculateValues`). `javac` failed, so the published
`dist/` jar and the GitHub release asset were still the pre-v1.3 build —
no scheduler, no Charm Magnet, no Session AVWAP for any user. Fixed by
deriving the series from the context: `computeAvwapForSeries(ctx.getDataSeries())`.
The jar now compiles and carries all v1.3 features.

### 2. Status banner redesign — readable, user-facing

The top-left box was an 11px monospaced developer dump (`dataChars`,
`parsed`/`visible` counts, `levelSpan`) in light blue on a semi-transparent
background — unreadable over a busy chart. Replaced with a 14/13px
sans-serif panel, opaque rounded background, amber accent, antialiased,
showing user-relevant info only: `TLADe GEX · {ticker}` / `{LIVE|DELAYED} ·
{N} levels` / `updated {HH:mm} ET`. The last-fetch ET timestamp is new, so
users can see at a glance whether the auto-refresh fired at each session.
The banner is positionable via a **Status Banner Position** setting
(Top/Bottom × Left/Right); default Bottom Left so it clears the MotiveWave
indicator list (top-left) and the right-edge GEX profile.

## 2026-06-12 — Tick-independent scheduler + Charm Magnet / Breakout + Session AVWAP (v1.3)

Three changes on `TLADeGexDashboard.java`. All pure Java, no native code,
cross-OS preserved (same jar still loads on macOS / Windows / Linux).

### 1. Sync fix — auto-refresh no longer dependent on chart ticks

v1.2 drove the 6 daily auto-fetches by calling `maybeScheduleFetch()`
from inside `calculateValues` and `onBarUpdate`. Both are tick-driven:
on cash-index charts outside RTH and on futures Globex quiet hours the
chart receives zero ticks for the entire pre-market window, so all 3
pre-RTH slots (ASIA 18:05, EU 02:05, PRE 08:05 ET) were silently
skipped. Users worked around it by toggling the study off and on.

v1.3 introduces a real `ScheduledExecutorService` daemon-thread
scheduler started in `onLoad` and chained per-slot. Fires exactly 7
fetches per trading day (1 mount + 6 absolute ET slots), correctly
picks the *next ascending* slot at any time of day. `FETCH_MINUTES_ET`
sorted ascending; the unsorted-array bug that took the first slot
greater than `nowMins` instead of the minimum (= jumping straight to
18:05 ASIA from 04:00 ET, skipping all 4 same-day slots) caught in the
ATAS v3.1 port and fixed here too.

The legacy `maybeScheduleFetch()` is kept as dead code; the new path
no longer relies on `OnCalculate`/`OnBarUpdate` to drive fetches.

### 2. Two new level families parsed from the extended cloud payload

The cloud `indicatorData` endpoint was extended to emit two on-chain-
derived levels the terminal renders:

- **CM** — Charm Magnet (violet line). Strike where charm flow
  magnetises price, surfaced from the volatility cascade. Matches
  the terminal's Market Pulse panel "Magnete Charm".
- **BL / BS** — Breakout Areas (emerald / rose lines). Long / short
  breakout zones detected by the PA engine.

Two new toggles in the Visibility settings group: `Show Breakout
Areas (BL/BS)` and `Show Charm Magnet (CM)`.

### 3. Session AVWAP — local compute, mirrors the Pine indicator

Four anchored-VWAP polylines drawn directly by the indicator from the
chart's own bars — same formula as our TradingView Pine, no extra
round-trip through the cloud. Source = `hlc3 * volume` cumulative,
reset at each session anchor in ET:

- **Asia** (amber) — anchored at 18:00 ET (= futures day start)
- **EU** (blue) — anchored at 02:00 ET
- **US** (green) — anchored at 09:30 ET RTH open
- **Prev Day US** (dim green) — yesterday's US AVWAP, promoted at the
  futures-day rollover and held for the rest of the day

New settings group **"Session AVWAP"**: individual on/off per polyline,
line width (1–4), label visibility, "Show Historical AVWAP (prev days)"
switch (off by default = only the current futures day is drawn, keeps
the chart clean).

Same approach as the ATAS v3.1.1 port: cross-OS Java only, uses
`DataSeries.getStartTime(i)` + `DrawContext.translateTime(long)` /
`translateValue(double)` from the SDK — no platform-specific code.

## 2026-06-09 — Cross-OS build (v1.2)

Rebuilt against the MotiveWave macOS SDK (`mwave_sdk.jar` class file
version 69, Java 25) so the single `dist/TLADeGexDashboard.jar` now
loads on macOS, Windows and Linux — anywhere MotiveWave ships its
Java 25-or-newer runtime. Source unchanged from v1.1.

**Why:** the previous build was compiled against the Windows SDK
(class file v70 / Java 26). MotiveWave on macOS still bundles a Java
25 runtime, so the Java 26 bytecode was silently skipped at extension
load time — no warning, no entry under Studies. macOS users were
effectively locked out.

**How:** the macOS `mwave_sdk.jar` exposes the same `Study` /
`DataContext` / `Defaults` / `Figure` / descriptor surface as the
Windows one; only the target bytecode version differs. Compiling
against the macOS SDK with `javac --release 25` produces a single
universal jar — backward-compatible with the Java 26 Windows JVM
(JVMs read older class file versions without issue), forward-
compatible with whatever Java 25 macOS ships.

**Footprint:** identical (18.7 KB jar, same five inner classes).

## 2026-06-09 — TLADe patches (v1.1)

Four small robustness fixes on top of Herat's original `v1.0`. Logic
verified line-by-line against the `TLADeGexDashboardNT.cs` (NinjaTrader
8 reference) — parsing, `convertPrice`, level selection + counting,
threshold gating, protected-strike retention, profile rendering, theme
palette, auto-fetch lifecycle: all unchanged. Only the four points
below were modified.

### 1. 🟠 Race calc/draw — draw lists made swap-safe

**Problem:** `drawLevels` and `drawProfile` were final fields that
`buildDrawModel` mutated in place between fetch completion and the
next `drawCanvas` invocation. The draw thread could observe a list
half-populated under live bars, producing flicker / partial paints
and (rarely) a `ConcurrentModificationException`.

**Fix:** the two fields are now `volatile`, and `buildDrawModel`
constructs the new lists on local references and **publishes them via
a single atomic assignment**. The draw thread either sees the previous
complete list or the new complete list — never an intermediate state.

### 2. 🟠 Settings mutation inside `calculateValues`

**Problem:** the fetched data string and the spread parsed from the
`S:` prefix were being written into the indicator's settings object
during `calculateValues`. That could re-trigger MotiveWave's
`onSettingsUpdated` → `recalculate` cycle, creating a re-entrancy
edge during live bar updates.

**Fix:** introduced two new private fields, `lastData` and
`spreadOverride`, that hold the fetched string + parsed spread without
touching settings. `convertPrice` now reads the effective spread from
`spreadOverride` (or the manual setting if no override). The settings
object is now write-only from the user's UI.

**Behavioural note:** the fetched data string no longer appears in the
"GEX Data" settings textbox (it used to). Functionally identical —
the diagnostic top-left box still shows the right `dataChars` count.
The `S:` prefix is also now a sticky override: as long as new data
arrives with `S:`, the manual "ES-SPX Spread" field in the UI is
ignored. The TLADe export always includes `S:`, so this is the
expected steady-state.

### 3. 🟡 ZG / structure-level colour aligned to NT8 reference

**Problem:** Zero Gamma and structure-level lines were drawn at
`#696969` (DimGray). The NT8 reference uses WPF's `Brushes.DarkGray`
(`#A9A9A9`), one shade lighter. Cross-platform consistency.

**Fix:** colour constant changed to `#A9A9A9`. Pixel-match with NT8.

### 4. 🧹 Deprecation cleanup — `new URL(String)` → `URI.create(...).toURL()`

**Problem:** `new URL(String)` is deprecated since JDK 20. Compiling
on JDK 26 surfaces the warning.

**Fix:** all URL instantiation routed through `URI.create(s).toURL()`.
**Compiles with 0 warnings** on JDK 26.

---

## 2026-06-09 — Original (v1.0) by Herat Acharya

Initial port of `TLADeGexDashboardNT` (NinjaTrader 8) to MotiveWave.

- Same `S:|L:|P:` data contract as the TradingView Pine indicator
- Same six TLADe publish-time auto-fetch slots
- Full level taxonomy: CW / PW / ZG / MP / EH / EL / VH / VL / PDH /
  PDL / PWH / PWL
- API key / free-delayed dual mode
- Profile histogram + chip labels + diagnostic status box

Credit: **Herat Acharya** — community contribution, June 2026.

## 2026-09-13 — release 3.5.0: the canonical set

Rebuilt to the same feature set as the TradingView ES indicator and NinjaTrader 8 (see the
`indicators-pre-3.5` release for the previous jar):

- One name per level ("7466 Put Wall", "7688 Zero Gamma", "7646 EM High Globex"), "↺" on a flipped wall.
- Wall flip on two consecutive 5-minute closes (secondary 5-minute series), colour = nature, thinner line.
- Breakouts M/W/D/4H/1H on closed bars, invalidated by an opposite bar of the same timeframe closing
  through, "Keep last N per timeframe" (2), labels "▲ BO L H1 7650.25".
- Confluence zones sized on the EM range (7% = ±3.5%), Delta Flip, EM High/Low RTH (EHR/ELR),
  TradingView palette and line styles, silent above 1H, level-cross signals (Study → Signals).
- No built-in spread: S: from the data string or identity; R: ratio for QQQ.
- Defaults: Structure, Charm Magnet, Breakouts, Confluence off; Delta Flip, Profile, AVWAP on.
