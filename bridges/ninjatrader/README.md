# TLADe Bridge — NinjaTrader 8

Connect NinjaTrader 8 to TLADe for real-time ES/NQ data. Feed-agnostic — works with **Rithmic, CQG, and Kinetick** through NT8.

## What You Get

- **Real-time spot ticks** — every NT8 tick forwarded sub-second
- **Live candles** — the in-progress 5-min bar pushed ~1×/sec so the
  terminal chart paints live, not only at 5-minute closes
- **500-bar backfill** on indicator mount, so chart history is there
  immediately instead of accumulating tick-by-tick
- **Any feed** — Rithmic, CQG, Kinetick, Simulated, IB through NT8 —
  if NinjaTrader can see it, TLADe gets it
- **Zero config** — NT8 indicator auto-pushes, Python receiver auto-serves

## Architecture

```
Your Feed (Rithmic/CQG/Kinetick/IB)
  └── NinjaTrader 8
        └── TLAdeBridge.cs (NT8 indicator, pushes ticks + bars via HTTP POST)
              └── tlade_bridge_nt8.py (Python receiver on port 5000)
                    └── TLADe Terminal (auto-detects on localhost:5000)
```

The receiver speaks the standard [Bridge Protocol](../../protocol/BRIDGE_SPEC.md) on port 5000, exposing `/health` and `/ib_data` to the terminal and `/push_spot` + `/push_bar` for the NT8 indicator.

## Requirements

- NinjaTrader 8 with a connected data feed
- Python 3.8+
- TLADe subscription

## Setup

### 1. Install the NT8 indicator

Copy `TLAdeBridge.cs` to your NinjaTrader indicators folder:
```
Documents\NinjaTrader 8\bin\Custom\Indicators\
```

Restart NinjaTrader or compile custom indicators (right-click in NinjaScript Editor > Compile).

### 2. Add indicator to chart

Open an ES or NQ chart in NT8, add the `TLAdeBridge` indicator.

### 3. Run the receiver

`tlade_bridge_nt8.py` is the file you downloaded with this bridge — it stays
wherever you saved it (usually your **Downloads** folder). Python only looks in
the folder the command prompt is currently in, so open the prompt **in that
folder**, not in your user directory:

1. Open File Explorer and go to the folder containing `tlade_bridge_nt8.py`
2. Click the address bar at the top, type `cmd`, press Enter
3. A command prompt opens already pointing at that folder

Then run:

```bash
pip install flask flask-cors
python tlade_bridge_nt8.py
```

Leave that window open while you trade — closing it stops the bridge.

> **`No such file or directory`?** You are in the wrong folder. The prompt shows
> which one you are in (e.g. `C:\Users\yourname>`); it must be the folder that
> holds the script. Running as administrator does not help — it is not a
> permissions problem.

### 4. Open TLADe

The terminal auto-detects the bridge on localhost.

## Updating

When a new version ships, follow [`UPDATING.md`](./UPDATING.md) — replacing
the `.cs` file alone is not enough, you also need to flush the NinjaScript
Editor buffer, recompile, and re-attach the indicator on the chart.

## FAQ

### Why does the indicator appear to only work on one chart when I add it to two?

By design — the TLAdeBridge indicator is a *spot-tick publisher*, not a chart
visualiser. It forwards each tick from NinjaTrader to the local bridge on
port 5000. Adding it to a second chart doesn't open a second data channel:
both instances post to the same `/push_spot` endpoint, so the receiver keeps
only the latest tick and the second instance looks idle by comparison.

You only need the indicator loaded on **one chart per ticker** (one for ES,
one for NQ), and that chart **must be a 5-minute chart**: the indicator pushes
the bars of the chart it sits on, and the terminal expects 5-minute bars
(Bridge Protocol §ib_data). On a 1-minute or tick chart the terminal reads the
bars at the wrong scale — session engines drift and the chart re-fits on every
update. Trade on any other timeframe in other chart windows; the multi-timeframe
view inside the terminal (5m / 15m / 30m / H1 / H4) is built from the 5-minute feed.

Start order matters: run the receiver first, then load (or reload) the indicator.
The 500-bar history is pushed when the indicator loads; restarting the receiver
afterwards discards it, and the terminal starts with only the bars since.

### The bridge runs, but TLADe never switches to live data (Chrome)

If the receiver is running and `http://localhost:5000/health` responds, but the
terminal never picks up the live feed, the blocker is your browser — not the
bridge. Chrome is progressively rolling out a security policy called **Local
Network Access**, which blocks HTTPS websites from reaching services on your
own machine (`localhost`).

**Fix (Chrome):** open `chrome://flags/#local-network-access-check`, set
**Local Network Access Checks** to **Disabled**, then restart Chrome
completely. The flag is version-dependent (it may be renamed or removed after
a Chrome update — suspect this first if the bridge stops connecting right
after updating) and disables the check browser-wide.

**Alternative:** Firefox does not enforce this policy the same way and
connects to the bridge out of the box.

## Status

**Beta** — validated with NT8 Simulated Feed. Full data path: NT8 pushes real-time spot ticks, closed 5-min bars, the in-progress live bar (~1×/sec), and a 500-bar backfill on indicator mount — so the terminal chart runs entirely on your NT8 feed. Tested with live data feeds pending (needs funded AMP account with Rithmic or CQG).

## Contributing

If you have a live NT8 connection with Rithmic or CQG, we'd appreciate testing reports. Open an issue with your results.
