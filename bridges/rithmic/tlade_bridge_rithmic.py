#!/usr/bin/env python3
"""
TLADe Bridge — Rithmic (Direct R|Protocol)
Connects to Rithmic via async_rithmic, aggregates ticks into 5min bars,
serves real-time candles on localhost for TLADe terminal auto-detection.

Usage:
  pip install flask flask-cors async_rithmic
  python tlade_bridge_rithmic.py

Requires: Rithmic account (Apex, TopstepTrader, Rithmic Paper Trading, etc.)
"""

import os, math, time, threading, asyncio, socket, urllib.request, json
from datetime import datetime, timedelta, timezone
from collections import defaultdict
from flask import Flask, jsonify, request
from flask_cors import CORS

# ── Config ──
RITHMIC_USER = os.environ.get('RITHMIC_USER', '')
RITHMIC_PASS = os.environ.get('RITHMIC_PASS', '')
RITHMIC_SYSTEM = os.environ.get('RITHMIC_SYSTEM', 'Apex')
RITHMIC_GATEWAY = os.environ.get('RITHMIC_GATEWAY', 'wss://rithmic.com:443')
# EU gateway IP — override DNS if ISP breaks resolution
RITHMIC_GATEWAY_IP = os.environ.get('RITHMIC_GATEWAY_IP', '34.254.173.171')
PORT = int(os.environ.get('BRIDGE_PORT', '5000'))
BAR_SECONDS = 300  # 5 minutes

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

# ── DNS Patch (many ISPs break rithmic.com resolution) ──
_original_getaddrinfo = socket.getaddrinfo
def _patched_getaddrinfo(host, port, *args, **kwargs):
    if host == 'rithmic.com' and RITHMIC_GATEWAY_IP:
        host = RITHMIC_GATEWAY_IP
    return _original_getaddrinfo(host, port, *args, **kwargs)
socket.getaddrinfo = _patched_getaddrinfo

# ── State ──
lock = threading.Lock()
connected = False
live_prices = {'ES': None, 'NQ': None}
# Bars: { 'ES': [ {t, o, h, l, c, v}, ... ], 'NQ': [...] }
bars_5min = {'ES': [], 'NQ': []}
# Current building bar
_current_bar = {'ES': None, 'NQ': None}
_daily_cache = {}
_history_cache = {}
_contracts = {'ES': None, 'NQ': None}


def _bar_start(ts_sec):
    """Round timestamp down to 5-minute boundary."""
    return (ts_sec // BAR_SECONDS) * BAR_SECONDS


def _process_tick(sym, price, volume, ts_sec):
    """Aggregate a tick into the current 5min bar."""
    if price is None or price <= 0:
        return

    bar_ts = _bar_start(ts_sec)

    with lock:
        live_prices[sym] = price
        cur = _current_bar[sym]

        if cur is None or cur['t'] != bar_ts:
            # New bar — push previous if exists
            if cur is not None:
                bars_5min[sym].append(cur)
                # Keep max ~3000 bars (2+ weeks of 5min)
                if len(bars_5min[sym]) > 3000:
                    bars_5min[sym] = bars_5min[sym][-3000:]
            _current_bar[sym] = {
                't': bar_ts, 'o': price, 'h': price, 'l': price, 'c': price, 'v': volume or 0
            }
        else:
            # Update existing bar
            cur['h'] = max(cur['h'], price)
            cur['l'] = min(cur['l'], price)
            cur['c'] = price
            cur['v'] += volume or 0


# ── Active contract SSOT — follow TLADe's roll decision instead of guessing ──
ACTIVE_CONTRACT_URL = os.environ.get('ACTIVE_CONTRACT_URL',
                                     'https://europe-west1-omggex.cloudfunctions.net/activeContract')

def _fetch_active_contracts():
    """{ 'ES':'ESU26', 'NQ':'NQU26' } from the TLADe SSOT, or {} on failure."""
    try:
        with urllib.request.urlopen(ACTIVE_CONTRACT_URL, timeout=8) as r:
            return json.load(r)
    except Exception as e:
        print(f'[Rithmic] active-contract fetch failed ({e}) — using front-month fallback')
        return {}

def _code_to_rithmic(code):
    """'ESU26' -> 'ESU6' (Rithmic single-digit year). None if unparseable."""
    try:
        return code[:3] + code[4]   # 'ESU26' → 'ESU' + '6'
    except Exception:
        return None

# ── Rithmic Connection (async) ──
async def rithmic_loop():
    global connected
    from async_rithmic import RithmicClient
    from async_rithmic.enums import DataType, TimeBarType, SysInfraType

    async def on_tick(data):
        sym_full = data.get('symbol', '')
        # Map ESM6 → ES, NQM6 → NQ
        if sym_full.startswith('ES'):
            sym = 'ES'
        elif sym_full.startswith('NQ'):
            sym = 'NQ'
        else:
            return

        price = data.get('trade_price')
        volume = data.get('trade_size', 0)
        dt_obj = data.get('datetime')
        ts = dt_obj.timestamp() if dt_obj else time.time()
        _process_tick(sym, price, volume, ts)

    while True:
        try:
            print(f'[Rithmic] Connecting to {RITHMIC_GATEWAY} as {RITHMIC_USER} ({RITHMIC_SYSTEM})...')

            client = RithmicClient(
                user=RITHMIC_USER,
                password=RITHMIC_PASS,
                system_name=RITHMIC_SYSTEM,
                app_name="TLADe",
                app_version="1.0",
                url=RITHMIC_GATEWAY,
            )
            client.on_tick += on_tick

            # Market data only: ticker + history. The order and P&L plants are never used by
            # the bridge, and on data-only / paper entitlements they answer 'permission denied'
            # (rpCode 13) — which used to kill a connection that never needed them.
            await asyncio.wait_for(
                client.connect(plants=[SysInfraType.TICKER_PLANT, SysInfraType.HISTORY_PLANT]),
                timeout=30,
            )
            connected = True
            print('[Rithmic] Connected! (market data + history plants only)')

            ticker = client.plants["ticker"]
            history = client.plants["history"]

            # Contract selection: prefer the contract TLADe declares (SSOT), so the
            # bridge streams the SAME contract as the published levels. Fall back to
            # Rithmic's front-month if the SSOT is unreachable.
            ssot = _fetch_active_contracts()  # { 'ES':'ESU26', 'NQ':'NQU26' }
            for sym in ['ES', 'NQ']:
                contract = _code_to_rithmic(ssot.get(sym, '')) if ssot else None
                if contract:
                    print(f'[Rithmic] {sym}: {contract} (SSOT {ssot.get(sym)})')
                else:
                    contract = await ticker.get_front_month_contract(sym, "CME")
                    if contract:
                        print(f'[Rithmic] {sym}: {contract} (front fallback)')
                if contract:
                    _contracts[sym] = contract
                    # Subscribe to tick data
                    await ticker.subscribe_to_market_data(contract, "CME", DataType.LAST_TRADE)
                else:
                    print(f'[Rithmic] {sym}: no contract found')

            print('[Rithmic] ES + NQ streaming')

            # Pre-fetch historical data
            print('[Rithmic] Pre-fetching historical bars...')
            today = datetime.now(timezone.utc)
            for sym in ['ES', 'NQ']:
                contract = _contracts.get(sym)
                if not contract:
                    continue
                # Daily bars: aggregate from 5min history (Rithmic DAILY_BAR returns 0 for futures)
                # Done after history fetch below

                try:
                    # 5min bars (2 weeks) for VP/AVWAP + live chart seed
                    start_hist = today - timedelta(weeks=2)
                    hist_bars = await history.get_historical_time_bars(
                        contract, "CME", start_hist, today,
                        bar_type=TimeBarType.MINUTE_BAR, bar_type_periods=5
                    )
                    if hist_bars:
                        result = []
                        for b in hist_bars:
                            t = int(b.get('marker', 0))
                            if t <= 0:
                                continue
                            result.append({
                                't': t, 'o': b.get('open_price', 0), 'h': b.get('high_price', 0),
                                'l': b.get('low_price', 0), 'c': b.get('close_price', 0),
                                'v': int(b.get('volume', 0))
                            })
                        _history_cache[sym] = {'date': today.strftime('%Y-%m-%d'), 'data': result}
                        print(f'[Rithmic] History pre-fetched: {len(result)} bars for {sym} (2W 5min)')

                        # Seed live bars with history
                        with lock:
                            bars_5min[sym] = result.copy()
                            print(f'[Rithmic] Seeded {len(result)} bars for {sym} live chart')

                        # Aggregate 5min → daily bars for PDH/PDL/PWH/PWL
                        daily_map = defaultdict(lambda: {'o': 0, 'h': -1e9, 'l': 1e9, 'c': 0, 'v': 0, 't': 0})
                        for b in result:
                            day_key = datetime.utcfromtimestamp(b['t']).strftime('%Y-%m-%d')
                            d = daily_map[day_key]
                            if d['o'] == 0:
                                d['o'] = b['o']
                                d['t'] = b['t']
                            d['h'] = max(d['h'], b['h'])
                            d['l'] = min(d['l'], b['l'])
                            d['c'] = b['c']
                            d['v'] += b['v']
                        daily_result = sorted(
                            [{'t': d['t'], 'o': d['o'], 'h': d['h'], 'l': d['l'], 'c': d['c'], 'v': d['v']}
                             for d in daily_map.values() if d['o'] > 0],
                            key=lambda x: x['t']
                        )
                        _daily_cache[sym] = {'date': today.strftime('%Y-%m-%d'), 'data': daily_result}
                        print(f'[Rithmic] Daily aggregated: {len(daily_result)} days for {sym} (from 5min)')
                except Exception as e:
                    print(f'[Rithmic] History pre-fetch failed for {sym}: {e}')

            print('[Rithmic] Pre-fetch complete. Streaming live ticks...')

            # Keep alive + detect bar shrinkage (reconnect data loss)
            initial_es_count = len(bars_5min.get('ES', []))
            while connected:
                await asyncio.sleep(2)
                current_es = len(bars_5min.get('ES', []))
                if initial_es_count > 100 and current_es < 100:
                    print(f'[Rithmic] Bars shrank from {initial_es_count} to {current_es} — reconnecting')
                    break

        except asyncio.TimeoutError:
            print('[Rithmic] Connection timeout')
        except Exception as e:
            print(f'[Rithmic] Error: {e}')
        finally:
            connected = False
            try:
                await client.disconnect()
            except:
                pass
            print('[Rithmic] Reconnecting in 15s...')
            await asyncio.sleep(15)


def start_rithmic_thread():
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    loop.run_until_complete(rithmic_loop())


# ── Flask Endpoints (same protocol as IB bridge) ──

@app.route('/health')
def health():
    has_data = False
    with lock:
        has_data = len(bars_5min['ES']) > 0
    return jsonify({
        'status': 'ok',
        'tlade_bridge': True,
        'ib_connected': connected and has_data,
        'provider': 'rithmic',
        'lite_mode': True,
        'version': '1.0.0',
        'system': RITHMIC_SYSTEM,
    })


@app.route('/ib_data')
def ib_data():
    ticker = request.args.get('ticker', 'ES').upper()
    sym = 'NQ' if 'NQ' in ticker else 'ES'

    with lock:
        all_bars = bars_5min.get(sym, [])
        cur = _current_bar.get(sym)
        price = live_prices.get(sym)

        if not all_bars and cur is None:
            print(f'[Rithmic] /ib_data {sym}: no data yet (404)')
            return jsonify({'error': f'No data for {sym}'}), 404

        # Combine completed bars + current building bar
        combined = list(all_bars)
        if cur:
            combined.append(cur)

        times = [b['t'] for b in combined]
        opens = [b['o'] for b in combined]
        highs = [b['h'] for b in combined]
        lows = [b['l'] for b in combined]
        closes = [b['c'] for b in combined]
        volumes = [b['v'] for b in combined]

        if price is None and closes:
            price = closes[-1]

    print(f'[Rithmic] /ib_data {sym}: {len(combined)} bars, last={price}')
    return jsonify({
        'chart_data': {
            'time': times, 'open': opens, 'high': highs,
            'low': lows, 'close': closes, 'volume': volumes
        },
        'live_price': price,
        'instrument': sym,
    })


@app.route('/ib_daily')
def ib_daily():
    ticker = request.args.get('ticker', 'ES').upper()
    sym = 'NQ' if 'NQ' in ticker else 'ES'
    if sym in _daily_cache and _daily_cache[sym].get('data'):
        print(f'[Rithmic] /ib_daily {sym}: served {len(_daily_cache[sym]["data"])} daily bars (cached)')
        return jsonify({'data': _daily_cache[sym]['data'], 'instrument': sym, 'cached': True})
    print(f'[Rithmic] /ib_daily {sym}: not yet available (503)')
    return jsonify({'error': 'Daily data not yet available'}), 503


@app.route('/ib_history')
def ib_history():
    ticker = request.args.get('ticker', 'ES').upper()
    sym = 'NQ' if 'NQ' in ticker else 'ES'
    if sym in _history_cache and _history_cache[sym].get('data'):
        print(f'[Rithmic] /ib_history {sym}: served {len(_history_cache[sym]["data"])} history bars (cached)')
        return jsonify({'data': _history_cache[sym]['data'], 'instrument': sym, 'cached': True})
    print(f'[Rithmic] /ib_history {sym}: not yet available (503)')
    return jsonify({'error': 'History data not yet available'}), 503


# ── Main ──
if __name__ == '__main__':
    if not RITHMIC_USER or not RITHMIC_PASS:
        print("""
  TLADe Bridge — Rithmic

  Set your credentials:
    set RITHMIC_USER=your-rithmic-id
    set RITHMIC_PASS=your-password
    set RITHMIC_SYSTEM=Apex          (or TopstepTrader, Rithmic Paper Trading, etc.)

  Then run:
    python tlade_bridge_rithmic.py

  Available systems: Apex, TopstepTrader, Bulenox, Earn2Trade, Rithmic Paper Trading, etc.
""")
        exit(1)

    print(f'''
  TLADe Bridge — Rithmic
  User: {RITHMIC_USER}
  System: {RITHMIC_SYSTEM}
  Gateway: {RITHMIC_GATEWAY} → {RITHMIC_GATEWAY_IP}
  Port: localhost:{PORT}

  Open your terminal on tradelikeadealer.com — it will auto-detect.
''')
    threading.Thread(target=start_rithmic_thread, daemon=True).start()
    app.run(host='0.0.0.0', port=PORT, debug=False)
