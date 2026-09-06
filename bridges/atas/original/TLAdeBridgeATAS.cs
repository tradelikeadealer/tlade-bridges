// ============================================================
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Bridge contribution
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe Bridge — ATAS Edition  v2.4.0
//  C# indicator for ATAS (Advanced Time & Sales)
//
//  Architecture:
//    Rithmic / CQG feed
//      -> ATAS
//           -> TLAdeBridgeATAS.cs   (indicator, HTTP POST push)
//                -> tlade_bridge_atas.py   (Python receiver, port 5000)
//                     -> TLADe Terminal    (auto-detects localhost:5000)
//
//  Push endpoints (ATAS -> receiver):
//    POST /push_spot    -> live tick
//    POST /push_bar     -> closed bar (OHLCV + delta + bid/ask volume)
//    POST /push_daily   -> daily bar (last bar of the previous trading day)
//
//  Install:
//    1. Build:   dotnet build TLAdeBridgeATAS.csproj -c Release
//                (the DLL is auto-copied to Documents\ATAS\Scripts\)
//    2. ATAS:    Settings -> Extensions -> Reload
//    3. Add the indicator to an ES or NQ chart
//    4. Start the receiver: double-click start.bat (or `python tlade_bridge_atas.py`)
//    5. TLADe Terminal auto-detects the bridge on localhost:5000.
//
//  v2.4.0: Channel<> for bars — single worker thread, no thread-pool explosion
//    - _postedBarTimes: every bar sent exactly once (key = candle.Time.Ticks)
//    - _barSem: at most 3 concurrent POSTs to Flask
//    - removed the broken time/index filter
// ============================================================

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ATAS.Indicators;

namespace ATAS.Indicators.Technical
{
    [DisplayName("TLADe Bridge")]
    [Description("Trimite date live (ticks + bare + delta) catre TLADe Terminal pe localhost:5000")]
    public class TLAdeBridgeATAS : Indicator
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS", "tlade_bridge.log");

        // Channel with capacity 600: a single worker sends bars sequentially
        // Previne thread pool explosion la flood de bare istorice
        private readonly Channel<(string ticker, int bar, CandleData cd)> _barChannel
            = Channel.CreateBounded<(string, int, CandleData)>(new BoundedChannelOptions(600)
              {
                  FullMode    = BoundedChannelFullMode.DropOldest,
                  SingleReader = true,
                  SingleWriter = false
              });

        // Bar deduplication: every bar sent exactly once (key = candle.Time.Ticks)
        private readonly ConcurrentDictionary<long, byte> _postedBarTimes
            = new ConcurrentDictionary<long, byte>();

        // State intern
        private double   _lastPostedPrice    = -1;
        private DateTime _lastSpotTime       = DateTime.MinValue;
        private int      _lastPostedDailyBar = -1;
        private DateTime _lastBarDate        = DateTime.MinValue.Date;
        private bool     _liveMode           = false;

        [Display(Name = "Port receiver", GroupName = "TLADe", Order = 1)]
        [Range(1024, 65535)]
        public int Port { get; set; } = 5000;

        [Display(Name = "Interval minim intre tickuri (sec)", GroupName = "TLADe", Order = 2)]
        [Range(1, 30)]
        public int SpotThrottleSeconds { get; set; } = 2;

        [Display(Name = "Trimite bare intraday (off = NT8-like, doar SPOT)", GroupName = "TLADe", Order = 3)]
        public bool SendIntraBars { get; set; } = false;

        [Display(Name = "Push daily bar at session rollover", GroupName = "TLADe", Order = 4)]
        public bool SendDailyBars { get; set; } = false;

        public TLAdeBridgeATAS() : base(true)
        {
            DataSeries[0].IsHidden = true;
            // Start the worker that sends bars sequentially
            Task.Run(BarWorker);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // ATAS v8: bar = bar being processed; CurrentBar = total count; live bar = CurrentBar - 1
            var isLastBar = bar == CurrentBar - 1;

            // INIT: first time we reach the live bar
            if (!_liveMode && isLastBar)
            {
                var candle0 = GetCandle(bar);
                _liveMode   = true;
                Log($"INIT v2.6.0 bar={bar} CurrentBar={CurrentBar} price={candle0?.Close} instrument={DataProvider.InstrumentInfo.Instrument} port={Port}");
            }

            // 1) SPOT — throttled to SpotThrottleSeconds, sent from the live bar
            if (_liveMode && isLastBar)
            {
                var now    = DateTime.UtcNow;
                var candle = GetCandle(bar);
                var valueD = (double)value;
                var closeD = candle != null ? (double)candle.Close : 0.0;
                // value = tick real-time (actualizat per tick); candle.Close poate fi stale
                var price  = valueD > 0 ? valueD : closeD;

                if ((now - _lastSpotTime).TotalSeconds >= SpotThrottleSeconds)
                {
                    Log($"SPOT_DBG val={valueD} close={closeD} price={price} last={_lastPostedPrice}");
                    if (price > 0)
                    {
                        _lastPostedPrice = price;
                        _lastSpotTime    = now;
                        var ticker = NormalizeTicker(DataProvider.InstrumentInfo.Instrument);
                        Task.Run(() => PostSpot(ticker, price, now));
                    }
                }
            }

            // 2) BARA INCHISA — doar daca SendIntraBars=true (default false = NT8-mode)
            if (_liveMode && SendIntraBars && !isLastBar)
            {
                var candle = GetCandle(bar);
                if (candle == null) return;

                // TryAdd returns false if the bar has already been sent
                if (!_postedBarTimes.TryAdd(candle.Time.Ticks, 0)) return;

                // New-day detection -> send the D1 bar of the previous day
                if (SendDailyBars
                    && _lastBarDate != DateTime.MinValue.Date
                    && candle.Time.Date != _lastBarDate
                    && bar > 0
                    && (bar - 1) != _lastPostedDailyBar)
                {
                    _lastPostedDailyBar = bar - 1;
                    var prevCandle = GetCandle(bar - 1);
                    if (prevCandle != null)
                    {
                        var dailyCd = BuildCandleData(prevCandle);
                        var t       = NormalizeTicker(DataProvider.InstrumentInfo.Instrument);
                        Task.Run(() => PostDailyBar(t, bar - 1, dailyCd));
                    }
                }

                _lastBarDate = candle.Time.Date;

                var cd     = BuildCandleData(candle);
                var ticker = NormalizeTicker(DataProvider.InstrumentInfo.Instrument);
                _barChannel.Writer.TryWrite((ticker, bar, cd));
            }
        }

        // Single worker that consumes the channel sequentially — no thread-pool explosion
        private async Task BarWorker()
        {
            await foreach (var (ticker, bar, cd) in _barChannel.Reader.ReadAllAsync())
                await PostBar(ticker, bar, cd);
        }

        public override void Dispose()
        {
            _barChannel.Writer.Complete();
            base.Dispose();
        }

        private static string NormalizeTicker(string symbol)
        {
            var s = (symbol ?? "").ToUpperInvariant();
            if (s.Contains("NQ") || s.Contains("NDX") || s.Contains("MNQ"))
                return "NQ";
            return "ES";
        }

        private static CandleData BuildCandleData(IndicatorCandle candle)
        {
            return new CandleData
            {
                Open      = (double)candle.Open,
                High      = (double)candle.High,
                Low       = (double)candle.Low,
                Close     = (double)candle.Close,
                Volume    = (double)candle.Volume,
                AskVolume = (double)candle.Ask,
                BidVolume = (double)candle.Bid,
                Delta     = (double)candle.Delta,
                MaxDelta  = (double)candle.MaxDelta,
                MinDelta  = (double)candle.MinDelta,
                Time      = candle.Time
            };
        }

        private async Task PostSpot(string ticker, double price, DateTime ts)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var payload = "{" +
                $"\"ticker\":\"{ticker}\"," +
                $"\"spot\":{price.ToString(ci)}," +
                $"\"ts\":\"{ts:O}\"," +
                $"\"provider\":\"atas\"" +
                "}";
            await Post($"http://localhost:{Port}/push_spot", payload,
                       $"SPOT {ticker} {price.ToString(ci)}");
        }

        private async Task PostBar(string ticker, int bar, CandleData cd)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var payload = "{" +
                $"\"ticker\":\"{ticker}\"," +
                $"\"bar_index\":{bar}," +
                $"\"time\":\"{cd.Time:O}\"," +
                $"\"open\":{cd.Open.ToString(ci)}," +
                $"\"high\":{cd.High.ToString(ci)}," +
                $"\"low\":{cd.Low.ToString(ci)}," +
                $"\"close\":{cd.Close.ToString(ci)}," +
                $"\"volume\":{cd.Volume.ToString(ci)}," +
                $"\"ask_volume\":{cd.AskVolume.ToString(ci)}," +
                $"\"bid_volume\":{cd.BidVolume.ToString(ci)}," +
                $"\"delta\":{cd.Delta.ToString(ci)}," +
                $"\"max_delta\":{cd.MaxDelta.ToString(ci)}," +
                $"\"min_delta\":{cd.MinDelta.ToString(ci)}," +
                $"\"provider\":\"atas\"" +
                "}";
            await Post($"http://localhost:{Port}/push_bar", payload,
                       $"BAR {ticker} {cd.Time:HH:mm} C={cd.Close.ToString(ci)} D={cd.Delta:+0;-0}");
        }

        private async Task PostDailyBar(string ticker, int bar, CandleData cd)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var payload = "{" +
                $"\"ticker\":\"{ticker}\"," +
                $"\"bar_index\":{bar}," +
                $"\"time\":\"{cd.Time:yyyy-MM-dd}\"," +
                $"\"open\":{cd.Open.ToString(ci)}," +
                $"\"high\":{cd.High.ToString(ci)}," +
                $"\"low\":{cd.Low.ToString(ci)}," +
                $"\"close\":{cd.Close.ToString(ci)}," +
                $"\"volume\":{cd.Volume.ToString(ci)}," +
                $"\"ask_volume\":{cd.AskVolume.ToString(ci)}," +
                $"\"bid_volume\":{cd.BidVolume.ToString(ci)}," +
                $"\"delta\":{cd.Delta.ToString(ci)}," +
                $"\"provider\":\"atas\"" +
                "}";
            await Post($"http://localhost:{Port}/push_daily", payload,
                       $"DAILY {ticker} {cd.Time:yyyy-MM-dd} C={cd.Close.ToString(ci)}");
        }

        private async Task Post(string url, string json, string label)
        {
            try
            {
                var content  = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(url, content);
                Log($"{label} -> {(response.IsSuccessStatusCode ? "OK" : $"FAIL {(int)response.StatusCode}")}");
            }
            catch (TaskCanceledException)
            {
                Log($"TIMEOUT: {label}");
            }
            catch (Exception ex)
            {
                Log($"ERR ({label}): {ex.Message}");
            }
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [TLAdeBridge] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        private struct CandleData
        {
            public double   Open, High, Low, Close;
            public double   Volume, AskVolume, BidVolume;
            public double   Delta, MaxDelta, MinDelta;
            public DateTime Time;
        }
    }
}
