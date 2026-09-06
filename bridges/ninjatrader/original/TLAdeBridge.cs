#region Using declarations
using System;
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

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "TLADe local data bridge — pusha tick ES/NQ a localhost:8765";
                Name        = "TLAdeBridge";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
                IsSuspendedWhileInactive = false;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            double price = Close[0];
            DateTime now = DateTime.Now;

            Print($"[TLAdeBridge] OnBarUpdate fired — price={price} lastPrice={lastPostedPrice} timeDiff={(now - lastPostTime).TotalSeconds:F1}s");

            if ((now - lastPostTime).TotalSeconds < 2) return;
            if (price == lastPostedPrice) return;

            lastPostedPrice = price;
            lastPostTime    = now;

            // Ticker mapping: NQ → NDX, tutto il resto (ES) → SPX
            string sym    = Instrument.MasterInstrument.Name;
            string ticker = sym.StartsWith("NQ") ? "NDX" : "SPX";

            Print($"[TLAdeBridge] Posting {ticker} {price}...");
            Task.Run(() => PostSpot(ticker, price, now));
        }

        private async Task PostSpot(string ticker, double price, DateTime ts)
        {
            try
            {
                string payload = $"{{\"ticker\":\"{ticker}\",\"spot\":{price.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"ts\":\"{ts:O}\"}}";
                var content    = new StringContent(payload, Encoding.UTF8, "application/json");
                var response   = await httpClient.PostAsync("http://localhost:8765/push_spot", content);
                Print($"[TLAdeBridge] {ticker} {price} → {(response.IsSuccessStatusCode ? "OK" : "FAIL")}");
            }
            catch (Exception ex)
            {
                Print($"[TLAdeBridge] Errore POST: {ex.Message}");
            }
        }
    }
}
