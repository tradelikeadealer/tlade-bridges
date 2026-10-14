#!/usr/bin/env python3
"""
TLADe Bridge — NinjaTrader 8 receiver.

Receives ticks AND closed 5-minute bars from the TLAdeBridge.cs indicator
running inside NT8 and exposes them to the TLADe terminal via the standard
Bridge Protocol (see ../../protocol/BRIDGE_SPEC.md).

Endpoints:
  POST /push_spot          — inbound: NT8 indicator pushes each tick here
  POST /push_bar           — inbound: NT8 indicator pushes each closed bar
  GET  /health             — terminal liveness probe
  GET  /ib_data?ticker=    — terminal pulls latest tick (live_price) +
                              5-minute bar history (chart_data)

Run:
  pip install flask flask-cors
  python tlade_bridge_nt8.py

Bars start empty and accumulate as NT8 pushes them. On indicator mount,
TLAdeBridge.cs performs a backfill of the most recent ~500 closed bars
so the terminal has chart history immediately.
"""

import os
from datetime import datetime
from threading import Lock

from flask import Flask, jsonify, request
from flask_cors import CORS

# The release number of the whole NT8 bridge, the same string TLAdeBridge.cs
# prints and sends. One product, one number: a receiver on 1.2.0 next to an
# indicator on 3.5.4 is one more thing to have to explain.
VERSION = "3.5.4"
PORT = int(os.environ.get('BRIDGE_PORT', '5000'))  # override for local testing without clashing with another local bridge
HEARTBEAT_WINDOW_S = 15  # tick is "fresh" if received within this window
MAX_BARS = 4032          # ~2 weeks of 5-min bars (Bridge Protocol §ib_data recommendation)

app = Flask(__name__)
CORS(app, resources={r"/*": {"origins": "*"}})

# Chrome Private Network Access (PNA) — required so public HTTPS origins
# like tradelikeadealer.com can fetch from this localhost server. Without
# this header Chrome blocks the request with:
#   "Permission was denied for this request to access the `loopback`
#    address space."
@app.after_request
def _allow_private_network(resp):
    resp.headers['Access-Control-Allow-Private-Network'] = 'true'
    return resp

_lock = Lock()
_ticks = {}                       # { "ES": {"price", "ts", "received_at"}, ... }
_bars  = {"ES": [], "NQ": []}     # accumulator of closed 5-min bars per ticker
_refused = {}                     # per-ticker count of refused ticks (see _impostor)


# Inbound allow-list: the only instruments whose RAW CHART PRICE may be stored.
# This is the receiving half of NormalizeTicker() in TLAdeBridge.cs, which is the
# canonical copy of the rule - the two cannot import each other (C# and Python),
# so this copy names its original (Rule #12).
#
# It used to be `"NQ" if "NQ" in s else "ES"`, i.e. ES as the catch-all for every
# string on earth. That is the same defect that made an MNQ chart publish the
# Nasdaq price under the ticker "ES" (Federico, 14 October 2026): a SPY tick at
# 785 or a Gold tick at 2600 would have been stored as the S&P just as happily.
_INBOUND = {"ES": "ES", "MES": "ES", "NQ": "NQ", "MNQ": "NQ"}

# Read-side aliases. Selecting WHICH bucket to read is harmless, so the terminal
# may ask with the index or ETF name; only writing a cash price into a futures
# bucket is dangerous, and that is what _INBOUND forbids.
_OUTBOUND = dict(_INBOUND, SPX="ES", SPY="ES", **{"ES=F": "ES"})
_OUTBOUND.update({"NDX": "NQ", "QQQ": "NQ", "NQ=F": "NQ"})

# ── One ticker, one instrument ───────────────────────────────────────────────
# A tick that cannot belong to the same instrument as the ticker's live
# reference is refused instead of stored. This is what a stale indicator build
# does: before 14 October TLAdeBridge published an MNQ chart under "ES", so "ES"
# alternated between 7850 and 30879 one second apart, and every spot-anchored
# drawing in the terminal (radar window, above/below split) was re-sliced on
# each tick. Refusing the impostor keeps the feed usable even when the indicator
# loaded on the chart is old - which the user cannot always tell.
#
# Whichever instrument arrives first owns the ticker for the session; the other
# is refused and NAMED in the log, so the cause is readable instead of guessed.
# ES does not move 25% in a session, and two different indices are about 4x
# apart, so the threshold separates them without hardcoding any price band.
MAX_DEVIATION   = 0.25
REFERENCE_TTL_S = 300   # after this much silence, the next tick sets a new reference


def _normalize_ticker(raw: str, inbound: bool = False):
    """Map a ticker to ES or NQ. Returns None for anything else - None means REFUSE."""
    table = _INBOUND if inbound else _OUTBOUND
    return table.get((raw or "").upper().strip())


def _impostor(ticker: str, price: float):
    """The stored reference for `ticker` if `price` cannot be the same instrument.

    Returns None when the price is acceptable. Caller holds the lock.
    """
    ref  = None
    prev = _ticks.get(ticker)
    if prev and (datetime.utcnow() - prev["received_at"]).total_seconds() <= REFERENCE_TTL_S:
        ref = prev["price"]
    elif _bars.get(ticker):
        # No fresh tick: the bars already stored under this ticker are the
        # reference. A backfill burst can land before the first spot tick.
        ref = _bars[ticker][-1].get("close")
    if ref and abs(price - ref) / ref > MAX_DEVIATION:
        return ref
    return None


def _report_refusal(ticker: str, price: float, ref: float, kind: str) -> None:
    """Say it once per ticker, then every 100th - OnEachTick means this can pour in."""
    n = _refused.get(ticker, 0) + 1
    _refused[ticker] = n
    if n == 1 or n % 100 == 0:
        print(f"[REFUSED] {kind} {price} does not belong to {ticker} (live reference {ref}) "
              f"- refused {n}x. Two different instruments are publishing as {ticker}: "
              f"the TLAdeBridge indicator on one of your charts is an old build that "
              f"labels every instrument as ES. Update it, or remove it from that chart.")


def _is_fresh(received_at: datetime) -> bool:
    return (datetime.utcnow() - received_at).total_seconds() < HEARTBEAT_WINDOW_S


@app.route("/push_spot", methods=["POST"])
def push_spot():
    """Inbound endpoint — NT8 indicator posts each tick here."""
    data = request.get_json(silent=True)
    if not data or "ticker" not in data or "spot" not in data:
        return jsonify({"error": "invalid payload, expected {ticker, spot, ts}"}), 400
    try:
        price = float(data["spot"])
    except (TypeError, ValueError):
        return jsonify({"error": "spot must be numeric"}), 400

    ticker = _normalize_ticker(str(data["ticker"]), inbound=True)
    if ticker is None:
        print(f"[REFUSED] {data['ticker']!r} is not an instrument this bridge can publish - "
              f"only ES, MES, NQ and MNQ are quoted in the futures scale.")
        return jsonify({"error": f"unsupported ticker {data['ticker']}"}), 400

    with _lock:
        ref = _impostor(ticker, price)
        if ref is not None:
            _report_refusal(ticker, price, ref, "tick")
            return jsonify({"error": "price does not belong to this ticker",
                            "ticker": ticker, "reference": ref}), 409
        _ticks[ticker] = {
            "price": price,
            "ts": data.get("ts") or datetime.utcnow().isoformat(),
            "received_at": datetime.utcnow(),
            # Reported by the indicator since TLAdeBridge 3.5.3. Absent means an
            # older build, and that is exactly what /health says out loud.
            "indicator_version": str(data.get("v") or "") or None,
        }
    print(f"[TICK] {ticker} -> {price}")
    return jsonify({"ok": True, "ticker": ticker})


@app.route("/push_bar", methods=["POST"])
def push_bar():
    """Inbound endpoint — NT8 indicator posts each closed bar here.

    Dedupes by `bar_index` so the indicator can safely re-post a bar
    (e.g. during the brief overlap when OnBarUpdate fires multiple times
    around bar close). Most-recent value wins for that index.
    """
    data = request.get_json(silent=True)
    if not data or "ticker" not in data:
        return jsonify({"error": "invalid payload, expected {ticker, time, ohlcv, bar_index}"}), 400

    ticker = _normalize_ticker(str(data["ticker"]), inbound=True)
    if ticker is None:
        print(f"[REFUSED] bar for {data['ticker']!r} - not an instrument this bridge can publish.")
        return jsonify({"error": f"unsupported ticker {data['ticker']}"}), 400
    try:
        bar = {
            "time":   int(data.get("time")),
            "open":   float(data.get("open")),
            "high":   float(data.get("high")),
            "low":    float(data.get("low")),
            "close":  float(data.get("close")),
            "volume": int(data.get("volume", 0)),
        }
    except (TypeError, ValueError) as e:
        return jsonify({"error": f"invalid bar fields: {e}"}), 400

    bar_index = data.get("bar_index")
    if bar_index is not None:
        try:
            bar_index = int(bar_index)
        except (TypeError, ValueError):
            bar_index = None

    with _lock:
        ref = _impostor(ticker, bar["close"])
        if ref is not None:
            _report_refusal(ticker, bar["close"], ref, "bar")
            return jsonify({"error": "bar does not belong to this ticker",
                            "ticker": ticker, "reference": ref}), 409
        bars = _bars[ticker]
        if bar_index is not None and bars and bars[-1].get("bar_index") == bar_index:
            # same bar re-posted → overwrite latest
            bars[-1] = {**bar, "bar_index": bar_index}
        else:
            bars.append({**bar, "bar_index": bar_index})
            if len(bars) > MAX_BARS:
                _bars[ticker] = bars[-MAX_BARS:]
        stored = len(_bars[ticker])

    print(f"[BAR]  {ticker} idx={bar_index} t={bar['time']} C={bar['close']} V={bar['volume']} (stored={stored})")
    return jsonify({"ok": True, "ticker": ticker, "bars_stored": stored})


@app.route("/health", methods=["GET"])
def health():
    """Terminal liveness probe (Bridge Protocol §health).

    `lite_mode` flips to false once we have any stored bars — that's
    how the terminal knows chart_data on /ib_data is real, not empty.
    """
    with _lock:
        any_fresh = any(_is_fresh(t["received_at"]) for t in _ticks.values())
        live_tickers = [k for k, t in _ticks.items() if _is_fresh(t["received_at"])]
        has_bars = any(len(b) > 0 for b in _bars.values())
        # Which TLAdeBridge build is actually feeding us, as reported by the
        # indicator itself. None means a build older than 3.5.3, which did not
        # say. The terminal shows this so nobody has to ask the user.
        indicator = {k: t.get("indicator_version") for k, t in _ticks.items()
                     if _is_fresh(t["received_at"])}
        refused = dict(_refused)
    return jsonify({
        "status": "ok",
        "tlade_bridge": True,
        "ib_connected": any_fresh,
        "provider": "ninjatrader",
        "lite_mode": not has_bars,
        "version": VERSION,
        "live_tickers": live_tickers,
        "indicator_version": indicator,
        "refused_ticks": refused,
    })


@app.route("/ib_data", methods=["GET"])
def ib_data():
    """Real-time spot + 5-minute bar history for the requested ticker.

    Bars come from /push_bar accumulator; live_price from /push_spot.
    Returns 404 only when BOTH the tick is stale AND no bars are stored.
    """
    requested = request.args.get("ticker", "ES")
    ticker = _normalize_ticker(requested)
    if ticker is None:
        return jsonify({"error": f"unknown ticker {requested}"}), 400
    with _lock:
        t = _ticks.get(ticker)
        fresh = bool(t and _is_fresh(t["received_at"]))
        price = t["price"] if fresh else None
        ts = t["ts"] if fresh else None
        # Dedup by time + sort defensively. NT8 BackfillBars and OnBarUpdate's
        # first-tick PostBar can race so the live bar overlaps a backfill bar
        # at the same time. Last-write-wins on duplicates; charts require
        # strictly ascending + unique time.
        unique_bars = {}
        for b in _bars[ticker]:
            t_key = b.get("time", 0)
            unique_bars[t_key] = b
        bars = sorted(unique_bars.values(), key=lambda b: b.get("time", 0))

    if not fresh and not bars:
        return jsonify({"error": f"no data for {ticker}"}), 404

    chart_data = {
        "time":   [b["time"]   for b in bars],
        "open":   [b["open"]   for b in bars],
        "high":   [b["high"]   for b in bars],
        "low":    [b["low"]    for b in bars],
        "close":  [b["close"]  for b in bars],
        "volume": [b["volume"] for b in bars],
    }

    return jsonify({
        "chart_data":  chart_data,
        "live_price":  price,
        "last_price":  price,
        "instrument":  ticker,
        "ts":          ts,
    })


if __name__ == "__main__":
    print(f"[TLADe NT8 Bridge] v{VERSION} listening on http://localhost:{PORT}")
    print("[TLADe NT8 Bridge] waiting for ticks + bars from TLAdeBridge.cs (NT8 indicator)...")
    print(f"[TLADe NT8 Bridge] bar storage cap: {MAX_BARS} per ticker")
    # threaded=True so the receiver can accept concurrent POSTs from the NT8
    # indicator (backfill burst + live bar push + spot ticks can hit the
    # same socket simultaneously; without threading the late requests fail
    # with "An error occurred while sending the request" on the C# side).
    app.run(host="127.0.0.1", port=PORT, debug=False, threaded=True)
