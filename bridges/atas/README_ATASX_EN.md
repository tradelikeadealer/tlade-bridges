# TLADe indicators for ATAS X (Windows and macOS)

A separate build from the classic one. Same sources, one compile-time
constant (`ATASX`).

| | ATAS Platform (classic, WPF) | ATAS X (Avalonia) |
|---|---|---|
| Project | `TLAdeBridgeATAS.csproj` | `TLAdeBridgeATASX.csproj` |
| Target | `net10.0-windows` + WPF | `net10.0`, no WPF |
| DLL | `TLAdeBridgeATAS.dll` | `TLAdeBridgeATASX.dll` |
| Colours in settings | `System.Windows.Media.Color` | `System.Drawing.Color` |
| Runs on | Windows | Windows **and** macOS — the same file |

Both carry all the indicators: **TLADe Bridge**, **TLADe GEX Dashboard**,
**TLADe Quantum Field Ladder**, and in this build **worldline-AVWAP**.

## Before you install

**ATAS X build 8.0.15.642 or newer.** Older builds will not load the
indicator (vendor notice, 13 September 2026).

**Windows: Smart App Control blocks unsigned indicator DLLs.** This is the
most common reason an install appears to do nothing at all. If the indicator
never shows up in the list, look there first.

## Install

1. Close ATAS X completely (`OFT.PlatformX.exe`). The platform locks the file
   while it is open.
2. Copy `TLAdeBridgeATASX.dll` into `%APPDATA%\ATAS X\Indicators\`
   (create the folder if it does not exist).
3. Reopen ATAS X and re-attach the indicators to your chart.

No keys or URLs are compiled into the DLL. `ApiKey` and `N8nWebhookUrl`
default to empty, and the key is read at runtime from `tlade_apikey.txt`.

## Session anchors

Every anchor goes through the same conversion to **New York time**
(Eastern, DST-aware). No other timezone appears in the code.

| line | anchor (ET) |
|---|---|
| Asia | 18:00 — the futures day open |
| EU | 03:00 — London open, in NY time |
| US | 09:30 — RTH open |
| PD (previous US) | 09:30 ET of the previous day |
| Daily *(worldline-AVWAP only)* | 18:00 |
| Weekly | Sunday 18:00 |

`worldline-AVWAP` additionally draws **Daily** and **Weekly**. Daily shares the
Asia anchor (18:00 ET), so those two lines overlap by design.

## What changed in this build (2 September 2026)

**GEX Dashboard — EU anchor moved from `02:00 ET` to `03:00 ET`.** The ATAS X
port had kept the old value inherited from the Pine script (Frankfurt
pre-market); the classic ATAS build was already on 03:00. The extra hour of
pre-London volume dragged the average toward the Asian session price.

**worldline-AVWAP:**

- EU anchor moved from `08:00 London local` to `03:00 ET`. The same line for
  most of the year, but now identical through the two DST offset windows
  (mid-March and late October) where ET is the reference.
- **PD** changed from *frozen* to *sloped*: at the Asia rollover it initialises
  with the totals of the US session that just closed, then keeps accumulating.
  It stays an AVWAP anchored at yesterday's 09:30 ET, not a flat step. This is
  the rule both dashboards already used.
- EU and US are suppressed when the anchor falls on a Saturday or Sunday. At
  the Sunday reopen they would otherwise duplicate the Asia line from 18:00.
- Weekly is no longer drawn when the loaded history does not reach the week
  open, which used to produce a partial VWAP carrying a weekly label. The
  reason is written to `tlade_gex.log`.
- Guard added for bars with an invalid timestamp (`DateTime.MinValue`).
- Dead code removed: the London timezone helpers (`_tzLondon`, `ResolveTz`,
  `ToLondon`).

## Credit and support

The ATAS indicators are a community contribution by **Mihai**, written natively
for ATAS. TLADe does tuning and maintenance on them. They are not part of the
officially supported set, so please report anything you find — it reaches him,
and it is how the build improves.
