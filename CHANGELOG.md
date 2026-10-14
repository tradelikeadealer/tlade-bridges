# Changelog

There is one published release, tagged `latest`, and it is always the current build.
This file is its history: what changed, and what is known to be open.

Entries are added when a build is published. A bridge or indicator with no entry
has not changed since the one below it.

---

## NinjaTrader 8, ATAS, MotiveWave — TLADe GEX Levels

### 3.5.3 — 14 October 2026 (ATAS only)

**Fixed — the Quantum Field Ladder on ATAS drew nothing at all.**
Since 12 October, 18:01 ET the data string carries `R:`, the index-to-ETF ratio. The Ladder
was the one indicator never taught to read it: after stripping `S:` it tested the remainder
for `L:`, found `R:` there instead, and left the level list empty. No error, no warning, an
empty chart. Measured on the published build: 0 levels on the live string, 39 on the same
string with the `R:` segment removed. Key holders lost it on Monday evening; without a key
the delayed feed had not yet reached a date that carries `R:`, which is why some installs
still worked.

**Fixed — SPX, SPY, NDX and QQQ on the Ladder.**
This closes what 3.5.2 listed as known open. SPX and NDX now subtract the futures-cash
spread — the Ladder was drawing ES and NQ strikes on a cash chart, a whole spread off — and
SPY and QQQ divide by the measured ratio from `R:` instead of the 10 and 40 written into the
code. On 14 October that was 7.5 points of SPY and 26 of QQQ. When no spread is known the
strikes pass through untouched: the spread setting now defaults to 0 instead of 24, because
with a built-in default that guard could never fire and a string without `S:` shifted every
level by an invented number.

**New — a magnet is a verdict that travels, not something the indicator guesses.**
A level where call and put gamma balance is a magnet and is drawn in cyan; a directional one
is a wall and stays magenta. Until now the Ladder called only Zero Gamma a magnet and painted
everything else magenta, which left a Quantum Field with nothing quantum about it. The engine
decides and the answer rides in the profile section as `B:<strikes>,0,0`, three fields like
`T:`. It goes there because the platforms require the string to start with `S:`, and because
every parser skips a profile row whose first field is not a number — a sixth field on the
level row would have landed inside Quantower's magnitude, which splits with a count of five,
and silently zeroed every level.

**New — Zero Gamma is a white line, always drawn.**
It is the pivot of the field, not one level among many, so it is not behind any switch. The
colour is a setting for anyone working on a light background. On ATAS it used to be the grey
`#9ca3af`, the same grey as the vol bands and the structure levels.

**New — the reference levels are lines, and they are off by default.**
Max Pain, Expected Move, vol bands, Delta Flip and Charm Magnet are not field: with the new
"Show system levels as lines" switch on, they draw as lines with the same colours and styles
the GEX Dashboard uses. `EMR` is dropped entirely — it is the WIDTH of the RTH expected move,
not a price, and as a level it put a line at 66 on an ES chart and 360 on an NQ one. "Max
levels" now counts walls, which Max Pain and the vol bands used to eat.

**Also — the log finally says which build is running.**
Both ATAS indicators wrote `CTOR v3.0` while the release was 3.5.2, so a log told us nothing
about what the user actually had installed. It now writes the release version, and the
Ladder's parse line reports the magnet count: `PARSED levels=39 magnets=1`.

**Packaging — the ATAS bundle is assembled from a named list.**
It used to be the whole working folder, zipped. That shipped the real DLL buried under
`bin/Release/net10.0-windows`, a second DLL with the identical filename under `mac/`, the
as-delivered contributor sources in `original/`, a zip inside the zip — and no instructions,
because `INSTALL.txt` had never been a tracked file. People installed the Mac file or a
pre-3.5.0 source and got a silent blank chart. The zip now has `TLAdeBridgeATAS.dll` and
`INSTALL.txt` at the top, with `mac/`, `python-bridge/` and `source/` beside them.

**Known open.**
The TradingView Quantum Field Ladder still divides by a hardcoded 10 and 40: its own data
string comes from `generateQuantumDataString`, which carries no `R:`, so the fix above does
not reach it. It also receives Zero Gamma with `nodeType=0`, so it paints it as a wall and
the top-N sort by intensity drops it first.

### 3.5.3 — ATAS X (Avalonia, Windows and macOS)

**ATAS X is built from our own sources now, and it is in the CI.**
`TLADe-ATASX.zip` had been uploaded by hand and sat on the 2 September build for six weeks.
Since 12 October the Quantum Field Ladder inside it was drawing nothing, like the classic one,
and nobody could see that the download itself was stale. It is now built on every push, from
the same three source files as the Windows build, with one compile-time constant:

| | ATAS Platform (classic) | ATAS X |
|---|---|---|
| Target | `net10.0-windows` + WPF | `net10.0`, no WPF |
| DLL | `TLAdeBridgeATAS.dll` | `TLAdeBridgeATASX.dll` |
| Colours in settings | `System.Windows.Media.Color` | `System.Drawing.Color` |

Only one thing in the indicators was actually tied to WPF: `AddAlert`, whose two colour
parameters are `System.Windows.Media.Color` in the classic assemblies, a signature that cannot
be expressed on `net10.0`. On ATAS X it is called through reflection — the platform's own
signature is read at runtime and the colours built to match — so the DLL can be produced
without any ATAS X assembly on the build machine. If no signature matches, the alert is logged
and skipped: a missed alert is not worth a dead indicator.

**What ATAS X loses in this build: worldline AVWAP.** It was in the 2 September DLL and not in
this repository — it lived only in the contributor's branch and never came back. The commit
that published that build says of it: "Not verified on macOS worldlines — nobody has said they
tested it there." ATAS X is now at feature parity with Windows, from one set of sources, which
is the only arrangement that does not drift again. Re-adding worldlines is a separate job.

**Verified:** builds clean, carries all three indicator classes, and its assembly references
have the same shape as the hand-built DLL it replaces — ATAS.Indicators and OFT.Rendering, and
no `PresentationCore`. **Not verified in ATAS X itself:** nobody has loaded this file into the
platform. It ships on that basis, deliberately, because the Ladder has been blank since Monday.

**The classic bundle no longer carries a `mac/` folder.** It held a `.dll` with the identical
filename to the Windows one, three directories down, and people copied that one and got an
empty chart with no error. macOS has its own download and its own install folder; the
INSTALL.txt in the classic bundle now says so in the first lines.

---

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

## NinjaTrader 8 — TLADe local data bridge

This is `TLAdeBridge.cs`, the indicator that feeds your NinjaTrader prices *into* the
terminal. It is not the GEX Levels indicator and is not needed to see levels on a chart.

### 14 October 2026

**Fixed — a chart of the micro Nasdaq published its price as the S&P.**
The instrument was mapped with `Name.StartsWith("NQ") ? "NQ" : "ES"`, so ES was the
catch-all for every instrument that exists. `MasterInstrument.Name` for the micro Nasdaq
is `MNQ`, and `"MNQ".StartsWith("NQ")` is false: an MNQ chart sent the Nasdaq price under
the ticker `ES`. With the bridge also loaded on an ES chart — the documented setup, one
chart per ticker — the terminal received 7850 and 30879 one second apart on a single
ticker. The spot oscillated between two indices, and because the radar window and the
above/below split are derived from the spot on every render, the whole drawing was
re-sliced several times a second. It read as a rendering fault and was not one. Gold, YM,
RTY, any symbol at all did the same thing, with nothing anywhere to say so.

The map is now an exact allow-list — `ES`, `MES`, `NQ`, `MNQ` — and an instrument outside
it publishes **nothing**, with one line in the Output window naming the symbol refused.

Equality, not `StartsWith`, and deliberately narrower than the GEX Levels indicator, which
also accepts SPX/SPY/NDX/QQQ. Two reasons, both measured rather than assumed. NinjaTrader's
own instrument database on a stock install holds six real instruments whose name begins
with `ES` — `ESP35`, `ESPORTS`, `ESRX`, `ESS`, `ESTC3`, `ESV` — every one of which a prefix
test would have published as the S&P; and of 1875 master instruments, none has a space in
its name, so the bare root is what the property always returns and equality cannot miss.
The GEX Levels indicator may take a cash symbol because it *draws*, and owns the spread and
the ETF ratio needed to put a cash level on a futures axis. This bridge sends the raw chart
price: a SPY chart trades near 785 and would arrive labelled `ES`, wrong by a factor of ten,
and QQQ by forty-one. The two must not be "aligned".

Reported by a subscriber who had watched his chart flicker for a day, and who had done
nothing wrong: the README told him to use one chart per ticker, and never said that the
micro contract was not one of them. It says so now.

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
