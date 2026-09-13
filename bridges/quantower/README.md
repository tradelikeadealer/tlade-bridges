# TLADe Gamma Exposure — Quantower Indicator

**Status: Community — published as delivered, testing with the Telegram community**

Overlay TLADe GEX levels directly on a Quantower chart: Call/Put Walls,
Zero Gamma, Max Pain, Expected Move (Globex and RTH), Vol Bands, Charm
Magnet, plus the GEX profile histogram. Same data contract as the
TradingView indicator (`S:|L:|P:` string); in Live mode the indicator
fetches the string itself from the TLADe cloud endpoint.

## Community Contribution

This indicator is a community contribution by **Dogan Cile**, written
from scratch for Quantower. Many thanks for the work.

It is published here **as delivered by the author** under the MIT
licence, with his copyright in the header (the header is the only change
to the source). Dogan has authorised the TLADe team and the community to
fork and maintain it — fixes, optimisations and add-ons — so issues and
pull requests are welcome here.

## Download

- Prebuilt DLL + source: [GammaExposure-Quantower.zip](https://github.com/tradelikeadealer/tlade-bridges/releases/latest/download/GammaExposure-Quantower.zip)
- Source only: [`GammaExposure.cs`](GammaExposure.cs)

## Requirements

- Quantower (Windows). The prebuilt `GammaExposure.dll` was compiled by
  the author against the Quantower API available in August 2026; if
  Quantower refuses to load it after an update, recompile from source
  (see below).
- An ES / MES or NQ / MNQ chart. Live mode maps ES→SPX and NQ→NDX and
  fetches the matching TLADe data; other symbols need
  **Underlying symbol** set to ES or NQ by hand.
- A TLADe API key for the premium feed. Without a key the indicator uses
  the free (delayed) cloud endpoint.

⚠️ **Windows Smart App Control / SmartScreen.** A community DLL is not
code-signed, so a Windows 11 machine with Smart App Control **On** will
block it silently (the indicator simply does not appear in the list).
Check *Windows Security → App & browser control → Smart App Control
settings*. Note that Smart App Control can only be turned off, not back
on, without reinstalling Windows — your call.

## Install

1. Close Quantower.
2. Copy `GammaExposure.dll` into
   `<Quantower folder>\Settings\Scripts\Indicators\GammaExposure\`
   (create the `GammaExposure` folder; the default Quantower folder is
   `C:\Quantower`).
3. Start Quantower, open an ES or NQ chart and add the indicator
   **Gamma Exposure** from the indicators list.
4. In the indicator settings set **Live = on**. The API URL is already
   set to the TLADe endpoint; paste your key into **TLADe API key** if
   you have one.

Manual mode: leave **Live = off** and paste the data string from the
TLADe terminal into **TLADe manual data**.

## Settings (main ones)

| Setting | What it does |
|---|---|
| Live / manual data / API key | Data source, see above |
| Refresh mode | `GlobalSixSessions` (default: refetch at the six TLADe session times), `Interval`, `ManualOnly` |
| Top N wall levels / per side / Level selection | How many Call/Put Walls to draw, strongest or nearest to price |
| Limit to price radius | Hide levels far from the current price |
| Zero gamma / Exposure in markers | Zero Gamma line, GEX magnitude in the labels |
| Show GEX profile / Alignment / Bar scale / colours | The right-edge profile histogram |
| Status dot / Refresh button / Placement | Feed status indicator and a manual refresh control on the chart |

## What it does not do (yet)

Compared with the canonical TLADe indicator set (TradingView 3.5.0) this
version draws the **feed levels and the profile only**. Not implemented:
local price structure (PDH/PDL/PWH/PWL from chart bars), breakout
structure (BOS), session AVWAPs, session boxes, confluence zones,
wall-flip marking (two consecutive 5m closes through a wall), level
cross alerts, and the >1H timeframe gate. Contributions welcome.

## Build from source

Create a C# class library project targeting the .NET version of your
Quantower install, reference `TradingPlatform.BusinessLayer.dll` from
`<Quantower folder>\TradingPlatform\<version>\bin\`, add
`GammaExposure.cs`, build, and copy the resulting DLL as in *Install*.

## Data contract

The indicator consumes the TLADe data string:

```
S:<spread>|L:<price>,<code>,<label>,<tooltip>,<score>;...|P:<strike>,<call gex>,<put gex>;...
```

Level codes: `CW` `PW` (walls), `ZG`, `MP`, `EH` `EL` (EM Globex),
`EHR` `ELR` (EM RTH), `VH` `VL` (Vol Bands), `CM` (Charm Magnet). Prices
in the string are in index space (SPX/NDX); the indicator adds `S:` to
place them on the futures chart.
