#region Using declarations
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.Data;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TLAdeBridge : Indicator
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private double lastPostedPrice = 0;
        private DateTime lastPostTime  = DateTime.MinValue;
        private DateTime lastLiveBarPostTime = DateTime.MinValue;
        private int lastPostedBarIndex = -1;
        private bool backfillDone      = false;
        // backfillDone flips immediately at the start of the backfill block;
        // backfillFinished flips only after PostBackfillPayloads has drained
        // every queued payload. Used to suppress the live bar push while the
        // backfill burst is still in flight (avoids saturating the local
        // socket pool against the Flask receiver).
        private volatile bool backfillFinished = false;
        // Latches once so the "unsupported instrument" line is printed a single
        // time instead of on every tick of a chart that will never publish.
        private bool unsupportedReported = false;

        // How many closed bars to push on startup to seed the bridge with
        // history. The terminal expects "at least 2 weeks of 5-minute bars"
        // per Bridge Protocol §ib_data — 500 covers ~2 trading days, which
        // is enough for AVWAP/VP on the active session. The cap also keeps
        // the startup burst bounded.
        private const int BACKFILL_BARS = 500;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "TLADe local data bridge — pushes ES/NQ ticks and closed bars to localhost:5000";
                Name        = "TLAdeBridge";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
                IsSuspendedWhileInactive = false;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            string ticker = NormalizeTicker();

            // Unsupported instrument: publish nothing rather than publish it as ES.
            // Said once per instance, not per tick - OnBarUpdate runs on every tick.
            if (ticker == null)
            {
                if (!unsupportedReported)
                {
                    unsupportedReported = true;
                    Print($"[TLAdeBridge] {Instrument.MasterInstrument.Name} is not a supported instrument - "
                        + "nothing will be sent. Load this indicator on an ES, MES, NQ or MNQ chart.");
                }
                return;
            }

            // ── 1. Backfill on first call after data loaded ─────────────────
            // Push the most recent closed bars so the terminal has chart
            // history immediately. Bars (Time/Open/Close/...) are ONLY safe
            // to read from the NinjaScript thread, so snapshot all values
            // synchronously here, then spawn async HTTP posts off-thread.
            if (!backfillDone && CurrentBar > 0)
            {
                backfillDone = true;
                int count = Math.Min(BACKFILL_BARS, CurrentBar);
                var snapshots = new System.Collections.Generic.List<string>(count);
                for (int i = count; i >= 1; i--)
                {
                    int barIndex = CurrentBar - i;
                    if (barIndex < 0) continue;
                    long unixTime = ((DateTimeOffset)Time[i]).ToUnixTimeSeconds();
                    string payload =
                        "{" +
                        $"\"ticker\":\"{ticker}\"," +
                        $"\"time\":{unixTime}," +
                        $"\"open\":{Open[i].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"high\":{High[i].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"low\":{Low[i].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"close\":{Close[i].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"volume\":{(long)Volume[i]}," +
                        $"\"bar_index\":{barIndex}" +
                        "}";
                    snapshots.Add(payload);
                }
                Task.Run(() => PostBackfillPayloads(ticker, snapshots));
            }

            // ── 2. On the first tick of a new bar, post the just-closed bar ─
            // IsFirstTickOfBar == true means we're at the open of bar[0];
            // the bar that just closed is bar[1]. Snapshot the bar values
            // SYNC here (NinjaScript thread); the HTTP POST runs async.
            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                int closedBarIndex = CurrentBar - 1;
                if (closedBarIndex != lastPostedBarIndex)
                {
                    lastPostedBarIndex = closedBarIndex;
                    long unixTime = ((DateTimeOffset)Time[1]).ToUnixTimeSeconds();
                    string payload =
                        "{" +
                        $"\"ticker\":\"{ticker}\"," +
                        $"\"time\":{unixTime}," +
                        $"\"open\":{Open[1].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"high\":{High[1].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"low\":{Low[1].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"close\":{Close[1].ToString(CultureInfo.InvariantCulture)}," +
                        $"\"volume\":{(long)Volume[1]}," +
                        $"\"bar_index\":{closedBarIndex}" +
                        "}";
                    Task.Run(() => PostBarPayload(ticker, payload, closedBarIndex));
                }
            }

            // ── 3. Live bar push — bar[0] is the in-progress current 5m bar.
            // Replicates IB's `keepUpToDate=True` on reqHistoricalData: keeps the
            // last bar fresh on the receiver so /ib_data returns a live close,
            // not a stale 5-min-old one. Throttled ~1s so we don't flood the
            // local socket on every tick. Suppressed while the backfill burst
            // is still draining so we don't double up on the Flask receiver.
            // The Python receiver dedupes by bar_index, so the same slot keeps
            // getting overwritten until this bar closes (when block 2 above
            // posts its final state under the previous bar_index).
            DateTime nowLive = DateTime.Now;
            if (backfillFinished && CurrentBar >= 0 && (nowLive - lastLiveBarPostTime).TotalSeconds >= 1)
            {
                lastLiveBarPostTime = nowLive;
                long liveUnixTime = ((DateTimeOffset)Time[0]).ToUnixTimeSeconds();
                string livePayload =
                    "{" +
                    $"\"ticker\":\"{ticker}\"," +
                    $"\"time\":{liveUnixTime}," +
                    $"\"open\":{Open[0].ToString(CultureInfo.InvariantCulture)}," +
                    $"\"high\":{High[0].ToString(CultureInfo.InvariantCulture)}," +
                    $"\"low\":{Low[0].ToString(CultureInfo.InvariantCulture)}," +
                    $"\"close\":{Close[0].ToString(CultureInfo.InvariantCulture)}," +
                    $"\"volume\":{(long)Volume[0]}," +
                    $"\"bar_index\":{CurrentBar}" +
                    "}";
                Task.Run(() => PostBarPayload(ticker, livePayload, CurrentBar));
            }

            // ── 4. Spot tick (unchanged) ────────────────────────────────────
            double price = Close[0];
            DateTime now = DateTime.Now;

            Print($"[TLAdeBridge] OnBarUpdate fired — price={price} lastPrice={lastPostedPrice} timeDiff={(now - lastPostTime).TotalSeconds:F1}s");

            if ((now - lastPostTime).TotalSeconds < 2) return;
            if (price == lastPostedPrice) return;

            lastPostedPrice = price;
            lastPostTime    = now;

            Print($"[TLAdeBridge] Posting {ticker} {price}...");
            Task.Run(() => PostSpot(ticker, price, now));
        }

        /// <summary>
        /// The chart's instrument, mapped to the only two tickers the bridge can
        /// publish. Returns null for anything else, and null means PUBLISH NOTHING.
        ///
        /// It used to be `sym.StartsWith("NQ") ? "NQ" : "ES"`, which made ES the
        /// catch-all for every instrument on earth. MasterInstrument.Name for the
        /// micro Nasdaq is "MNQ", and "MNQ".StartsWith("NQ") is FALSE - so an MNQ
        /// chart published the Nasdaq price under the ticker "ES". With the bridge
        /// also loaded on an ES chart, the terminal received 7850 and 30879 one
        /// second apart on the same ticker: the spot oscillated between two indices
        /// and every spot-anchored drawing (radar window, above/below split) was
        /// re-sliced on each tick. A subscriber watched his chart flicker for a day.
        /// Gold, YM, RTY, any symbol at all did the same thing, silently.
        /// (Federico, 14 October 2026)
        ///
        /// Deliberately NARROWER than the dashboard's mapping in
        /// TLADeGexDashboardNT.cs, which also accepts SPX/SPY/NDX/QQQ. Do not
        /// "align" the two: the dashboard DRAWS, and owns the spread and the ETF
        /// ratio needed to place a cash level on a futures axis. This bridge SENDS
        /// THE RAW CHART PRICE. A SPY chart trades near 785 and would arrive
        /// labelled "ES" - wrong by a factor of ten; QQQ by forty-one. Only
        /// instruments already quoted in the futures scale may pass.
        ///
        /// Exact equality, not StartsWith: MasterInstrument.Name is the root symbol
        /// ("ES", not "ES 12-26"), so equality is precise, and StartsWith("ES")
        /// would also swallow ESTX50.
        /// </summary>
        private string NormalizeTicker()
        {
            string sym = (Instrument.MasterInstrument.Name ?? "").ToUpperInvariant();
            if (sym == "NQ" || sym == "MNQ") return "NQ";
            if (sym == "ES" || sym == "MES") return "ES";
            return null;
        }

        private async Task PostSpot(string ticker, double price, DateTime ts)
        {
            try
            {
                string payload = $"{{\"ticker\":\"{ticker}\",\"spot\":{price.ToString(CultureInfo.InvariantCulture)},\"ts\":\"{ts:O}\"}}";
                var content    = new StringContent(payload, Encoding.UTF8, "application/json");
                var response   = await httpClient.PostAsync("http://localhost:5000/push_spot", content);
                Print($"[TLAdeBridge] {ticker} {price} → {(response.IsSuccessStatusCode ? "OK" : "FAIL")}");
            }
            catch (Exception ex)
            {
                Print($"[TLAdeBridge] POST error: {ex.Message}");
            }
        }

        private async Task PostBarPayload(string ticker, string payload, int barIndex)
        {
            try
            {
                var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync("http://localhost:5000/push_bar", content);
                Print($"[TLAdeBridge] BAR {ticker} idx={barIndex} → {(response.IsSuccessStatusCode ? "OK" : "FAIL")}");
            }
            catch (Exception ex)
            {
                Print($"[TLAdeBridge] BAR POST error: {ex.Message}");
            }
        }

        private async Task PostBackfillPayloads(string ticker, System.Collections.Generic.List<string> payloads)
        {
            int sent = 0;
            foreach (var payload in payloads)
            {
                try
                {
                    var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync("http://localhost:5000/push_bar", content);
                    if (response.IsSuccessStatusCode) sent++;
                }
                catch (Exception ex)
                {
                    Print($"[TLAdeBridge] BACKFILL POST error: {ex.Message}");
                }
                // Delay between bars so we don't saturate the local socket
                // pool — 25ms keeps the burst inside what a single-threaded
                // Flask receiver can drain even with concurrent live-bar push.
                await Task.Delay(25);
            }
            backfillFinished = true;
            Print($"[TLAdeBridge] Backfill complete: {sent}/{payloads.Count} bars for {ticker}");
        }
    }
}
