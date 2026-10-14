# Updating the NT8 Bridge

When a new version of `TLAdeBridge.cs` ships, NinjaTrader does **not** pick it
up automatically. Three steps are needed — skipping any of them leaves the
old version running.

## 1. Replace the file

Download the latest [`TLADe-NT8-Bridge.zip`][zip], extract it, and copy the
new `TLAdeBridge.cs` over your existing one:

```
Documents\NinjaTrader 8\bin\Custom\Indicators\TLAdeBridge.cs
```

Overwrite the old file.

## 2. Force NinjaScript Editor to reload from disk

If you had NinjaScript Editor open before the update, the previous version
of the file is still cached in its in-memory buffer — `F5` would just
recompile the stale copy.

- Close the `TLAdeBridge.cs` tab inside the editor (Ctrl+W or click the X
  on the tab).
- Re-open it: in the left tree under **Indicators** → double-click
  `TLAdeBridge`. The buffer is now fresh from disk.
- Press **F5**. You should see `Compile successful — 0 errors` at the
  bottom of the editor.

If the compile fails, paste the error output to support — don't proceed
with a stale binary loaded on the chart.

## 3. Remove + re-add the indicator on the chart

NinjaTrader keeps the previously-compiled indicator instance running on
your chart until you explicitly detach it. F5 alone does not swap it.

- On your 5-min ES or NQ chart: right-click the `TLAdeBridge` indicator
  in the legend → **Remove**.
- Right-click the chart → **Indicators…** → select `TLAdeBridge` → **Add**
  → **OK**.

On re-attach, the backfill of the most recent ~500 closed bars replays
toward the receiver, and the live-bar push starts immediately.

## 3b. Check the version — this is how you know it worked

Open the Output Window (**View → New → NinjaScript Output**). On re-attach the
indicator announces itself:

```
[TLAdeBridge] v3.5.4 loaded on ES
```

If that line does not appear, or shows an older version, the update did not
take and the previously compiled build is still running — go back to step 2.
NinjaTrader compiles every script you own into one assembly, so an error in
any *other* indicator makes the whole compile fail and silently leaves the
old binary in place. The compile result at the bottom of the editor is the
place to look.

The receiver says the same thing from its side: `http://localhost:5000/health`
reports `"indicator_version"` per ticker, and `null` there means a build older
than 3.5.4.

## 4. (Optional) Hard refresh the terminal browser

To clear any cached candle source state in the browser:
**Ctrl+Shift+R** (Windows / Linux) or **Cmd+Shift+R** (macOS).

After that the LWChart should paint sub-second, not only at 5-minute
closes.

## Troubleshooting

If chart candles still don't update live after the three steps above,
open NinjaScript's **Output Window** (View → New → NinjaScript Output)
and look for lines starting with `[TLAdeBridge]`. Send a screenshot to
support — that log tells us exactly what the indicator is doing.

Common causes:
- The Python receiver (`tlade_bridge_nt8.py`) was not restarted and an
  older instance is still bound to port 5000. Stop it (Ctrl+C in its
  terminal) and re-run `python tlade_bridge_nt8.py`.
- The chart was on a non-front-month contract (e.g. an expired ES roll).
  Switch to the active front contract.
- The terminal browser has a stale `tlade_source_choice` in
  `localStorage` set to `'yahoo-always'` — open DevTools (F12) →
  Application → Local Storage → remove that key, then reload.

[zip]: https://github.com/tradelikeadealer/tlade-bridges/releases/latest/download/TLADe-NT8-Bridge.zip
