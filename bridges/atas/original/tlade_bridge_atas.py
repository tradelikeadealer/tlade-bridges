#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Bridge contribution
# Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
"""
TLADe Bridge — ATAS Edition  v1.2.0

Python receiver for the TLAdeBridgeATAS.cs indicator running inside ATAS.
Speaks the standard TLADe Bridge protocol on port 5000, exposing /health
and /ib_data to the TLADe Terminal.

Endpoints:
  POST /push_spot    <- ATAS indicator pushes every tick
  POST /push_bar     <- ATAS indicator pushes the closed 5m bar
  POST /push_daily   <- ATAS indicator pushes the daily bar
  POST /push_levels  <- external source pushes GEX levels (Call Wall, Put Wall, etc.)
  GET  /health       -> TLADe Terminal liveness probe
  GET  /ib_data      -> TLADe Terminal pulls live data (with real chart_data)
  GET  /get_levels   -> ATAS-side TLAdeLevels indicator pulls stored GEX levels

Run:
  pip install flask flask-cors
  python tlade_bridge_atas.py

/push_levels payload format:
  POST /push_levels
  {
    "ticker": "ES",
    "levels": [
      {"price": 5300.00, "type": "call_wall_strong", "label": "5300 CW Call Wall [S]"},
      {"price": 5250.00, "type": "call_wall",         "label": "5250 CW Call Wall"},
      {"price": 5200.00, "type": "put_wall",          "label": "5200 PW Put Wall"},
      {"price": 5180.00, "type": "max_pain",          "label": "5180 Max Pain"},
      {"price": 5270.00, "type": "em_high",           "label": "5270 EM High"}
    ]
  }

Accepted level types:
  call_wall_strong, call_wall, put_wall_strong, put_wall,
  max_pain, em_high, em_low, vwap_session, zg_level,
  vol_band_up, vol_band_down
"""

from datetime import datetime, timezone
from threading import Lock

from flask import Flask, jsonify, request
from flask_cors import CORS

VERSION = "1.2.0"
PORT    = 5000
HEARTBEAT_WINDOW_S = 15   # a tick is considered "fresh" if seen within the last N seconds
MAX_BARS           = 500  # how many 5m bars we keep in memory per ticker

app = Flask(__name__)
CORS(app, resources={r"/*": {"origins": "*"}})

_lock  = Lock()

# ── Per-ticker state ──────────────────────────────────────────────────────────
# {
#   "ES": {
#     "spot":  {"price": float, "ts": str, "received_at": datetime},
#     "bars":  [ {time, open, high, low, close, volume, ask_volume, bid_volume,
#                 delta, max_delta, min_delta}, ... ],   # max MAX_BARS
#     "daily": [ {time, open, high, low, close, volume, delta}, ... ]
#   }
# }
_state = {}

# Per-ticker GEX levels: {"ES": {"levels": [...], "ts": "..."}, ...}
_levels = {}

TICKERS = ["ES", "NQ"]

def _get_state(ticker: str) -> dict:
    if ticker not in _state:
        _state[ticker] = {"spot": None, "bars": [], "daily": []}
    return _state[ticker]


def _get_levels(ticker: str) -> dict:
    if ticker not in _levels:
        _levels[ticker] = {"levels": [], "ts": None}
    return _levels[ticker]


def _normalize(raw: str) -> str:
    s = (raw or "").upper()
    return "NQ" if ("NQ" in s or "NDX" in s or "MNQ" in s) else "ES"


def _is_fresh(received_at) -> bool:
    if received_at is None:
        return False
    return (datetime.now(timezone.utc) - received_at).total_seconds() < HEARTBEAT_WINDOW_S


# ─────────────────────────────────────────────────────────────────────────────
#  INBOUND — the ATAS indicator pushes data here
# ─────────────────────────────────────────────────────────────────────────────

@app.route("/push_spot", methods=["POST"])
def push_spot():
    """Live tick from the ATAS indicator (1:1 compatible with the NT8 bridge)."""
    data = request.get_json(silent=True)
    if not data or "ticker" not in data or "spot" not in data:
        return jsonify({"error": "invalid payload — expected {ticker, spot, ts}"}), 400
    try:
        price = float(data["spot"])
    except (TypeError, ValueError):
        return jsonify({"error": "spot must be numeric"}), 400

    ticker = _normalize(str(data["ticker"]))
    now    = datetime.now(timezone.utc)

    with _lock:
        st = _get_state(ticker)
        st["spot"] = {
            "price":       price,
            "ts":          data.get("ts") or now.isoformat(),
            "received_at": now,
            "provider":    data.get("provider", "unknown"),
        }

    print(f"[SPOT]  {ticker} -> {price}  ({data.get('provider','?')})")
    return jsonify({"ok": True, "ticker": ticker})


@app.route("/push_bar", methods=["POST"])
def push_bar():
    """Closed 5m bar with full order-flow fields."""
    data = request.get_json(silent=True)
    if not data or "ticker" not in data:
        return jsonify({"error": "invalid payload"}), 400

    ticker = _normalize(str(data["ticker"]))

    bar = {
        "time":       data.get("time"),
        "open":       data.get("open"),
        "high":       data.get("high"),
        "low":        data.get("low"),
        "close":      data.get("close"),
        "volume":     data.get("volume"),
        "ask_volume": data.get("ask_volume"),
        "bid_volume": data.get("bid_volume"),
        "delta":      data.get("delta"),
        "max_delta":  data.get("max_delta"),
        "min_delta":  data.get("min_delta"),
    }

    with _lock:
        st = _get_state(ticker)
        # avoid duplicates (same bar_index)
        idx = data.get("bar_index")
        if idx is not None and st["bars"] and st["bars"][-1].get("bar_index") == idx:
            st["bars"][-1] = {**bar, "bar_index": idx}
        else:
            st["bars"].append({**bar, "bar_index": idx})
            if len(st["bars"]) > MAX_BARS:
                st["bars"] = st["bars"][-MAX_BARS:]

    delta_str = f"D={bar['delta']:+.0f}" if bar.get("delta") is not None else ""
    print(f"[BAR]   {ticker} {bar.get('time','?')}  C={bar.get('close')}  Vol={bar.get('volume')}  {delta_str}")
    return jsonify({"ok": True, "ticker": ticker, "bars_stored": len(_state[ticker]["bars"])})


@app.route("/push_daily", methods=["POST"])
def push_daily():
    """Daily (D1) bar from ATAS."""
    data = request.get_json(silent=True)
    if not data or "ticker" not in data:
        return jsonify({"error": "invalid payload"}), 400

    ticker = _normalize(str(data["ticker"]))
    bar    = {k: data.get(k) for k in ("time","open","high","low","close","volume","ask_volume","bid_volume","delta")}

    with _lock:
        st = _get_state(ticker)
        # replace today's bar if already present
        if st["daily"] and st["daily"][-1].get("time","")[:10] == (bar.get("time") or "")[:10]:
            st["daily"][-1] = bar
        else:
            st["daily"].append(bar)
            if len(st["daily"]) > 365:
                st["daily"] = st["daily"][-365:]

    print(f"[DAILY] {ticker} {bar.get('time','?')[:10]}  C={bar.get('close')}  Vol={bar.get('volume')}")
    return jsonify({"ok": True, "ticker": ticker})


# ─────────────────────────────────────────────────────────────────────────────
#  OUTBOUND — the TLADe Terminal pulls data from here
# ─────────────────────────────────────────────────────────────────────────────

@app.route("/health", methods=["GET"])
def health():
    """Liveness probe (Bridge Protocol §health)."""
    with _lock:
        live = [k for k, v in _state.items() if v["spot"] and _is_fresh(v["spot"]["received_at"])]

    return jsonify({
        "status":       "ok",
        "tlade_bridge": True,
        "ib_connected": len(live) > 0,
        "provider":     "atas",
        "lite_mode":    False,       # we have real chart_data, not lite
        "version":      VERSION,
        "live_tickers": live,
        "port":         PORT,
    })


@app.route("/ib_data", methods=["GET"])
def ib_data():
    """
    Full payload for the terminal (Bridge Protocol §ib_data).
    Unlike the NT8 bridge, we return a real chart_data block with historical
    5m bars + delta, which allows TLADe to render the chart from ATAS data.
    """
    ticker = _normalize(request.args.get("ticker", "ES"))

    with _lock:
        st = _get_state(ticker)
        spot = st.get("spot")
        fresh = bool(spot and _is_fresh(spot["received_at"]))

        if not fresh:
            return jsonify({"error": f"no recent tick for {ticker}"}), 404

        bars   = list(st["bars"])
        daily  = list(st["daily"])
        price  = spot["price"]
        ts     = spot["ts"]

    # ── Serialize 5m bars into chart_data format ───────────────────────────
    def extract(key, lst):
        return [b.get(key) for b in lst]

    chart_data = {
        "time":       extract("time",   bars),
        "open":       extract("open",   bars),
        "high":       extract("high",   bars),
        "low":        extract("low",    bars),
        "close":      extract("close",  bars),
        "volume":     extract("volume", bars),
        # extra fields vs the NT8 bridge:
        "ask_volume": extract("ask_volume", bars),
        "bid_volume": extract("bid_volume", bars),
        "delta":      extract("delta",      bars),
        "max_delta":  extract("max_delta",  bars),
        "min_delta":  extract("min_delta",  bars),
    }

    daily_data = {
        "time":   extract("time",   daily),
        "open":   extract("open",   daily),
        "high":   extract("high",   daily),
        "low":    extract("low",    daily),
        "close":  extract("close",  daily),
        "volume": extract("volume", daily),
        "delta":  extract("delta",  daily),
    }

    return jsonify({
        "chart_data":  chart_data,   # 5m bars with order flow
        "daily_data":  daily_data,   # D1 bars
        "live_price":  price,
        "last_price":  price,
        "instrument":  ticker,
        "ts":          ts,
        "provider":    "atas",
        "bars_count":  len(bars),
        "daily_count": len(daily),
    })


# ─────────────────────────────────────────────────────────────────────────────
#  GEX LEVELS — pushed by an external source / pulled by the ATAS indicator
# ─────────────────────────────────────────────────────────────────────────────

@app.route("/push_levels", methods=["POST"])
def push_levels():
    """
    Receive GEX levels from an external source (Python script, browser script,
    TLADe Terminal wrapper, etc.) and store them for the ATAS indicator.

    JSON body:
      {
        "ticker": "ES",
        "levels": [
          {"price": 5300.0, "type": "call_wall_strong", "label": "5300 CW Call Wall [S]"},
          {"price": 5250.0, "type": "call_wall",         "label": "5250 CW Call Wall"},
          ...
        ]
      }
    """
    data = request.get_json(silent=True)
    if not data or "levels" not in data:
        return jsonify({"error": "invalid payload — expected {ticker, levels:[{price,type,label}]}"}), 400

    raw_levels = data.get("levels", [])
    if not isinstance(raw_levels, list):
        return jsonify({"error": "levels must be an array"}), 400

    ticker = _normalize(str(data.get("ticker", "ES")))
    now    = datetime.now(timezone.utc)

    # Validate + normalize
    validated = []
    for item in raw_levels:
        try:
            price = float(item.get("price", 0))
            if price == 0:
                continue
            validated.append({
                "price": round(price, 4),
                "type":  str(item.get("type", "unknown")),
                "label": str(item.get("label", f"{price:.2f}")),
            })
        except (TypeError, ValueError):
            continue

    # Sort descending by price (handy for display)
    validated.sort(key=lambda x: x["price"], reverse=True)

    with _lock:
        lv = _get_levels(ticker)
        lv["levels"] = validated
        lv["ts"]     = now.isoformat()

    print(f"[LEVELS] {ticker} -> {len(validated)} levels received")
    return jsonify({"ok": True, "ticker": ticker, "count": len(validated)})


@app.route("/get_levels", methods=["GET"])
def get_levels():
    """
    Return the stored GEX levels for a ticker.
    Used by the ATAS-side TLAdeLevels.cs indicator.

    Query params:
      ticker=ES|NQ  (default: ES)

    Response:
      {
        "ticker":  "ES",
        "count":   15,
        "ts":      "2026-05-01T20:00:00Z",
        "levels":  [ {"price": 5300.0, "type": "call_wall_strong", "label": "5300 CW Call Wall [S]"}, ... ]
      }
    """
    ticker = _normalize(request.args.get("ticker", "ES"))
    with _lock:
        lv = _get_levels(ticker)
        return jsonify({
            "ticker":  ticker,
            "count":   len(lv["levels"]),
            "ts":      lv["ts"],
            "levels":  list(lv["levels"]),
        })


# ─────────────────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    print(f"TLADe Bridge - ATAS Edition  v{VERSION}")
    print(f"Listening on http://localhost:{PORT}  (auto-detected by TLADe Terminal)")
    print()
    print("  ATAS -> bridge:")
    print(f"    POST /push_spot   (live tick)")
    print(f"    POST /push_bar    (5m bar)")
    print(f"    POST /push_daily  (daily bar)")
    print(f"    POST /push_levels (GEX levels)")
    print()
    print("  Bridge -> TLADe Terminal:")
    print(f"    GET  /health")
    print(f"    GET  /ib_data?ticker=ES")
    print()
    print("  Bridge -> ATAS TLAdeLevels indicator:")
    print(f"    GET  /get_levels?ticker=ES")
    print()
    app.run(host="0.0.0.0", port=PORT, debug=False, threaded=True)
