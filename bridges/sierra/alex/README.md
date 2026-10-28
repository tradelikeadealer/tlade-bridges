# TLADe Quantum Field Ladder for Sierra Chart — by Alex

The first Sierra Chart study for TLADe, written by **Alex** and sent to TLADe on 6 October 2026.
It draws the TLADe levels as horizontal magnet/wall fields, with a stronger centre fading toward
the edges. Community contribution: it is Alex's work and keeps his credit. New releases come from him.

Download: `TLADe-SierraChart-QuantumLadder-Alex.zip` on the
[latest release](https://github.com/tradelikeadealer/tlade-bridges/releases/latest).
The full TLADe indicator for Sierra Chart is the study one folder up, [`../`](../).

## Install

1. Close Sierra Chart.
2. Copy `TLADe_quantum_field_ladder_64.dll` into your Sierra Chart `Data` folder, then start Sierra
   Chart. Or copy `TLADe_quantum_field_ladder.cpp` into `ACS_Source` and build it with
   **Analysis → Build Custom Studies DLL → File → Select Files → Build → Remote Build**.
3. **Analysis → Studies → Add Custom Study → TLADe Quantum Field Ladder**.
4. Set *Data Source* to *TLADe API*, paste your own API key, set *Display Ticker* to the chart's
   instrument, and *API Refresh Now* to Yes. The full guide is in the study's **Description** button.

## Changes made by TLADe

With the author's permission (21 October 2026: "You can do it and have full control over the
study"), TLADe applied these corrections; everything else is as Alex wrote it.

- **Index-to-ETF ratio from the data string.** SPY and QQQ levels divide by the ratio published in
  `R:` (about 10.03 for SPX/SPY, 41.1 for NDX/QQQ), used inside a sane band (9–12, 35–48); a string
  without `R:` keeps the original 10 and 40. With 40 a QQQ level sat about 20 points away.
- **Magnets from the data.** A Call or Put Wall is a MAGNET when its strike is in the `B:` row of the
  profile, the engine's own verdict; before, every wall was drawn as a WALL.
- **Manual Quantum string.** `R:` after `S:` is stripped before the levels are read; it used to cost
  the first node, silently.
- **The API key stays off the chart.** Sierra printed the input values next to the study name,
  including the key; that is now off.
