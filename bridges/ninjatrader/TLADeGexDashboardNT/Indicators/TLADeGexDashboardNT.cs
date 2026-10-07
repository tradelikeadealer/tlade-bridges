// TLADe GEX Levels — NinjaTrader 8 — release 3.5.0 (13 September 2026)
// Canonical feature set = the TradingView ES indicator: same names, colours, line styles, wall-flip
// rule (two 5-minute closes), breakout rule (closed bars, opposite-bar invalidation, keep last N),
// confluence zones (% of EM), silent above 1H, level-cross alerts. Layout offsets are NT8's own.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.IO;
using System.Xml.Serialization;
using System.Windows.Media;

using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TLADeGexDashboardNT : Indicator
    {
        private class LevelEntry
        {
            public double EsStrike;
            public string Type;
            public string Label;
            public string Tooltip;
            public double Magnitude;
            // v1.1: local BOS levels are computed from the chart's own bars —
            // already in chart price space, must NOT go through ConvertPrice().
            public bool IsChartSpace;
        }

        private class ProfileEntry
        {
            public double EsStrike;
            public double Value;   // 2nd column
            public double Sign;    // 3rd column (-1 put, +1 call) - used for color only
        }

        private readonly CultureInfo inv = CultureInfo.InvariantCulture;

        private string _prevInput = "";
        private string _prevSettingsSig = "";
        private readonly List<LevelEntry> _levels = new List<LevelEntry>();
        private readonly List<ProfileEntry> _profile = new List<ProfileEntry>();
        private readonly HashSet<string> _tags = new HashSet<string>();

        private Color _posColor;
        private Color _negColor;
        private Brush _posBrush = Brushes.LimeGreen;
        private Brush _negBrush = Brushes.Red;
        private Brush _profCallBrush = Brushes.Red;
        private Brush _profPutBrush = Brushes.LimeGreen;

        private int _prevFontSize = -1;
        private SimpleFont _labelFont;

        private int _lastDrawnBar = -1;
        private bool _isDelayedMode = false;
        private double _ndxQqqRatio = 0.0;   // NDX→QQQ ratio from the data string (R:), NQ family only

        // ── Wall flip, the TradingView rule: two consecutive 5-minute closes beyond the strike flip
        // the wall (and two the other way restore it). State per wall (futures-space strike), reset
        // when the data string changes. Read on the 5-minute series added in State.Configure, so the
        // rule is the same on a 1-minute, 5-minute or hourly chart — and it no longer blinks per tick.
        private readonly Dictionary<double, int> _wallCounter = new Dictionary<double, int>();
        private readonly Dictionary<double, bool> _wallFlipped = new Dictionary<double, bool>();
        private readonly Dictionary<double, string> _wallType = new Dictionary<double, string>();
        private bool _flipDirty = false;

        // Levels currently drawn (chart scale) — what the cross alerts watch
        private struct AlertLevel { public double Price; public string Name; public string Kind; }
        private readonly List<AlertLevel> _alertLevels = new List<AlertLevel>();

        // ── v1.1: Session AVWAP local compute (mirrors the TV Pine indicator) ──
        // 4 plots (Asia / EU / US / Prev Day US) from hlc3*volume cumulative,
        // reset at each session start in ET. Sessions (ET, = Pine):
        //   Asia 18:00–03:00 · EU 03:00–08:00 · Pre 08:00–09:30 · US 09:30–16:00
        // Gap 16:00–18:00 ends the futures day. On each Asia start the US AVWAP
        // is promoted to PD (Pine behaviour, PD keeps accumulating intraday).
        private double _aSumPV, _aSumV, _eSumPV, _eSumV, _uSumPV, _uSumV, _pdSumPV, _pdSumV;
        private bool _asiaActive, _euActive, _usActive, _inCurrentDay;
        private bool _prevInAsia, _prevInEU, _prevInUS;
        // Snapshot taken on the first tick of each bar so live intrabar re-entries
        // (Calculate.OnPriceChange) re-accumulate the forming bar exactly once.
        private double _sASumPV, _sASumV, _sESumPV, _sESumV, _sUSumPV, _sUSumV, _sPdSumPV, _sPdSumV;
        private bool _sAsiaActive, _sEuActive, _sUsActive, _sInCurrentDay;
        private bool _sPrevInAsia, _sPrevInEU, _sPrevInUS;
        private int _avwapLastComputedBar = -1;
        private TimeZoneInfo _etTz;

        // ── v1.1: local multi-TF BOS (H4/H1) — 1:1 port of the ATAS port of
        // terminal clientEngineService.computeAllBOS. PA moved client-side on
        // the TLADe cloud, so BOdl/BOds are not in the published snapshot;
        // Break-of-Structure is reproduced from native chart candles instead.
        // D/W intentionally skipped: the terminal computes them from CASH daily
        // candles (Yahoo ^GSPC/^NDX), aggregating futures chart bars would show
        // discrepant levels (same rationale as the ATAS port).
        private readonly List<LevelEntry> _localBos = new List<LevelEntry>();
        private readonly List<LevelEntry> _localPA = new List<LevelEntry>();
        private int _paLastComputedBar = -1;
        private int _bosLastComputedBar = -1;
        private struct DBar { public double O, H, L, C; }

        // ── Auto-fetch from TLADe API ──
        private static readonly string API_URL = "https://europe-west1-omggex.cloudfunctions.net/indicatorData";
        // 6 fetch times per day (ET): ASIA 18:05, EU 02:05, PRE 08:05, RTH 09:35, OPRANGE 10:35, PWRHOUR 13:05
        private static readonly int[] FETCH_MINUTES_ET = { 1085, 125, 485, 575, 635, 785 };
        private int _lastFetchMinuteET = -1;
        private DateTime _lastFetchTime = DateTime.MinValue;

        private void TryAutoFetch()
        {
            if (!AutoFetchEnabled) return;

            // Get current ET time
            DateTime utcNow = DateTime.UtcNow;
            TimeZoneInfo et;
            try { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { try { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { return; } }
            DateTime etNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, et);
            int etMins = etNow.Hour * 60 + etNow.Minute;

            // Find if we should fetch now (within 5 min window of a scheduled time)
            int matchedSlot = -1;
            for (int i = 0; i < FETCH_MINUTES_ET.Length; i++)
            {
                int diff = etMins - FETCH_MINUTES_ET[i];
                if (diff >= 0 && diff < 5)
                {
                    matchedSlot = FETCH_MINUTES_ET[i];
                    break;
                }
            }

            if (matchedSlot < 0) return;
            if (matchedSlot == _lastFetchMinuteET) return; // already fetched this slot
            if ((DateTime.UtcNow - _lastFetchTime).TotalMinutes < 4) return; // debounce

            _lastFetchMinuteET = matchedSlot;
            _lastFetchTime = DateTime.UtcNow;

            if (string.IsNullOrEmpty(ApiKey)) return; // no key = no auto-fetch (free mode fetched at startup)
            _isDelayedMode = false; // user has key, switch to live

            try
            {
                string ticker = IsNqFamily() ? "NDX" : "SPX";
                string url = $"{API_URL}?ticker={ticker}";
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Timeout = 8000;
                request.UserAgent = "TLADe-NT8/1.0";
                request.Headers.Add("X-API-Key", ApiKey);

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string data = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(data) && data.Contains("L:"))
                    {
                        GexDataInput = data;
                        ParseIfChanged(force: true);
                        _lastDrawnBar = -1; // force redraw
                    }
                }
            }
            catch
            {
                // Silent fail — fallback to manual GexDataInput
            }
        }

        public override string ToString()
        {
            return "TLADe GEX";
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TLADe GEX";
                Description = "TLADe GEX Levels 3.5.0 — GEX levels + profile, Session AVWAP, Session Boxes, Breakout Structure, Confluence Zones (canonical set = the TradingView ES indicator)";
                Calculate = Calculate.OnPriceChange;
                IsOverlay = true;
                DisplayInDataBox = false;
                IsChartOnly = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;

                DisplayTicker = "ES"; // ES / SPX / SPY / NQ / NDX / QQQ
                AutoDetectTicker = true; // override DisplayTicker from chart symbol at runtime
                EsSpxSpread = 0.0;   // 0 = not known: strikes shown as-is until the data string brings S:
                NqNdxSpread = 0.0;

                GexDataInput = "";

                ShowGexLevels = true;
                ShowSystemLevels = true;
                ShowStructureLevels = false;
                ShowCharmMagnet = false;
                ShowDeltaFlip = true;
                LineStyle = "Dotted";
                ShowZeroGamma = false;   // ZG already shows via System Levels; this shows it ALONE when System is off

                MaxGexLevels = 10;     // 999 = All
                ShowOnlyNear = false;
                NearPct = 3.0;

                EnableThreshold = false;
                GexThreshold = 50.0;

                Theme = "Wall Street Classic"; // Wall Street Classic / Boreal / Lady Trader
                BarColorStyle = "Theme Colors"; // Theme Colors / Greyscale / Custom
                CustomBarCallColor = System.Windows.Media.Color.FromRgb(0xef, 0x44, 0x44);
                CustomBarPutColor  = System.Windows.Media.Color.FromRgb(0x22, 0xc5, 0x5e);

                // Layout
                RightOffsetBars  = 30;  // base "future" anchor
                LabelPaddingBars = 8;   // pushes labels/line-anchor left so they don't collide with the profile
                ProfileOffsetBars = 25; // pushes profile further right so it doesn't overwrite labels/lines

                ShowLabels = true;
                LabelFontSize = 9;

                // Line extents are measured FROM THE LABEL anchor (after padding)
                LineLeftBars = 80;
                LineRightBars = 0;
                LevelLineWidth = 1;     // width for GEX/system lines (ZG/MP stay >= 2)

                // Profile (P:)
                ShowProfileBars = true;
                ProfileWidthBars = 70;
                ProfileBarHeightTicks = 8;
                ProfileScaleMax = 10.0;
                AutoScaleProfileMax = false;
                MaxProfileRows = 1500;

                // Requested: puts also draw to the right
                ProfileAllToRight = true;

                ShowStatusText = false;
                AutoFetchEnabled = true;
                ApiKey = "";

                // v1.1 — Breakout / Session AVWAP / Session Boxes (family parity with TV/ATAS/MW)
                ShowBreakoutLevels = false;
                ShowBosM = false;
                ShowBosW = false;
                ShowBosD = true;
                ShowBosH4 = true;
                ShowBosH1 = true;
                BosKeepPerTf = 2;
                ShowAvwapAsia = true;
                ShowAvwapEU = true;
                ShowAvwapUS = true;
                ShowAvwapPD = true;
                ShowHistoricalAvwap = false;
                AvwapLineWidth = 2;
                ShowAboveH1 = false;
                EnableAlerts = false;
                AlertWalls = true;
                AlertSystem = true;
                AlertBos = true;
                ShowConfluence = false;
                ConfluenceMinSize = 3;
                ConfluenceEmPct = 7.0;
                ShowAvwapLabels = true;
                ShowSessionBoxes = false;
                ShowBoxAsia = true;
                ShowBoxEU = true;
                ShowBoxPre = true;
                ShowBoxUS = true;
                ShowHistoricalSessions = false;

                // AVWAP plots — colors match the TV Pine defaults
                AddPlot(new Stroke(new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)), 2), PlotStyle.Line, "AVWAP Asia");
                AddPlot(new Stroke(new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)), 2), PlotStyle.Line, "AVWAP EU");
                AddPlot(new Stroke(new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e)), 2), PlotStyle.Line, "AVWAP US");
                AddPlot(new Stroke(new SolidColorBrush(Color.FromRgb(0x6e, 0xe7, 0xb7)), 2), PlotStyle.Line, "AVWAP US Prev");
            }
            else if (State == State.Configure)
            {
                // 5-minute close stream for the wall-flip rule (BarsInProgress 1)
                AddDataSeries(BarsPeriodType.Minute, 5);
            }
            else if (State == State.DataLoaded)
            {
                // v1.1 — user-selected AVWAP line width
                int w = Math.Max(1, Math.Min(8, AvwapLineWidth));
                for (int i = 0; i < 4 && i < Plots.Length; i++)
                    Plots[i].Width = w;

                UpdateThemeBrushes();
                UpdateLabelFont();
                // Fetch data from API on first load (regardless of schedule)
                bool hasKeyEntry = !string.IsNullOrEmpty(ApiKey);
                bool inputEmpty = string.IsNullOrEmpty(GexDataInput);
                // Always fetch on startup if API key present — GexDataInput may contain stale data
                // from a previous session. Without this, the indicator shows old levels until the
                // next scheduled fetch window (Sam's bug report, April 2026).
                bool shouldFetch = hasKeyEntry || inputEmpty;
                Print($"[TLADe] DataLoaded: autoFetch={AutoFetchEnabled}, hasKey={hasKeyEntry}, inputEmpty={inputEmpty}, willFetch={AutoFetchEnabled && shouldFetch}");
                if (AutoFetchEnabled && shouldFetch)
                {
                    if (hasKeyEntry && !inputEmpty && _isDelayedMode)
                    {
                        GexDataInput = "";
                        _isDelayedMode = false;
                    }
                    _lastFetchMinuteET = -1; // force fetch
                    bool hasKey = hasKeyEntry;
                    try
                    {
                        // Force TLS 1.2 — NT8 .NET 4.8 default may not negotiate HTTPS to Cloud Functions
                        System.Net.ServicePointManager.SecurityProtocol =
                            System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls11 | System.Net.SecurityProtocolType.Tls;

                        string ticker = IsNqFamily() ? "NDX" : "SPX";
                        // With API key → live data. Without → free delayed (3 business days back)
                        string url = hasKey
                            ? $"{API_URL}?ticker={ticker}"
                            : $"{API_URL}?ticker={ticker}&mode=free";
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                        request.Method = "GET";
                        request.Timeout = 8000;
                        request.UserAgent = "TLADe-NT8/1.0";
                        if (hasKey)
                            request.Headers.Add("X-API-Key", ApiKey);
                        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                        {
                            string data = reader.ReadToEnd();
                            Print($"[TLADe] fetch {ticker} ({(hasKey ? "live" : "free")}): HTTP {(int)response.StatusCode}, bytes={data.Length}, hasL={data.Contains("L:")}");
                            if (!string.IsNullOrEmpty(data) && data.Contains("L:"))
                            {
                                GexDataInput = data;
                                if (!hasKey)
                                    _isDelayedMode = true;
                            }
                            else
                            {
                                Print($"[TLADe] body preview: {data.Substring(0, Math.Min(200, data.Length))}");
                            }
                        }
                    }
                    catch (WebException wex)
                    {
                        var http = wex.Response as HttpWebResponse;
                        string body = "";
                        if (http != null)
                        {
                            try { using (var r = new StreamReader(http.GetResponseStream())) body = r.ReadToEnd(); } catch { }
                        }
                        Print($"[TLADe] WebException: {wex.Status} HTTP {(int?)http?.StatusCode} body={body}");
                    }
                    catch (Exception ex)
                    {
                        Print($"[TLADe] Exception: {ex.GetType().Name} {ex.Message}");
                    }
                }
                ParseIfChanged(force: true);
                _prevSettingsSig = BuildSettingsSig();
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                // a new 5-minute bar has opened: the previous one is closed
                if (IsFirstTickOfBar && CurrentBars[1] >= 1)
                    UpdateWallFlips(Closes[1][1]);
                return;
            }
            if (BarsInProgress != 0)
                return;
            if (CurrentBar < 1)
                return;

            // v1.1 — Session AVWAP accumulates on EVERY bar (historical included):
            // the plots need per-bar values, unlike the static level overlays.
            if (TfActive()) ComputeAvwapCurrentBar();

            // Levels/boxes are static overlays — only need the latest bar. Draw on the LAST
            // loaded bar even during Historical, so a freshly-loaded / closed-market (weekend)
            // chart renders immediately instead of waiting for a realtime tick that never comes.
            if (State == State.Historical && CurrentBar < Bars.Count - 1)
                return;

            if (EnableAlerts && State == State.Realtime && IsFirstTickOfBar && CurrentBar >= 2)
                CheckLevelAlerts();

            TryAutoFetch();

            UpdateThemeBrushes();
            UpdateLabelFont();

            bool inputChanged = ParseIfChanged(force: false);

            string sig = BuildSettingsSig();
            bool settingsChanged = !string.Equals(sig, _prevSettingsSig, StringComparison.Ordinal);
            if (settingsChanged)
                _prevSettingsSig = sig;

            if (!inputChanged && !settingsChanged && !_flipDirty && _lastDrawnBar == CurrentBar)
                return;
            _flipDirty = false;

            _lastDrawnBar = CurrentBar;

            if (ShowStatusText)
                DrawStatus();

            // Show delayed mode banner when using free data
            if (_isDelayedMode)
            {
                Draw.TextFixed(this, "TLADeDelayed",
                    "DELAYED DATA (3 days) — Subscribe at tradelikeadealer.com for live levels",
                    TextPosition.TopRight,
                    Brushes.Orange,
                    new SimpleFont("Arial", 10),
                    Brushes.Transparent,
                    Brushes.Transparent,
                    0);
            }

            DrawAll();
        }

        private string BuildSettingsSig()
        {
            return string.Join("|", new[]
            {
                DisplayTicker ?? "",
                EsSpxSpread.ToString("0.########", inv),
                ShowGexLevels.ToString(),
                ShowSystemLevels.ToString(),
                ShowStructureLevels.ToString(),
                ShowCharmMagnet.ToString(),
                ShowDeltaFlip.ToString(),
                LineStyle ?? "",
                NqNdxSpread.ToString("0.########", inv),
                _ndxQqqRatio.ToString("0.########", inv),
                MaxGexLevels.ToString(inv),
                ShowOnlyNear.ToString(),
                NearPct.ToString("0.########", inv),
                EnableThreshold.ToString(),
                GexThreshold.ToString("0.########", inv),
                Theme ?? "",
                BarColorStyle ?? "",
                CustomBarCallColor.ToString(),
                CustomBarPutColor.ToString(),
                RightOffsetBars.ToString(inv),
                LabelPaddingBars.ToString(inv),
                ProfileOffsetBars.ToString(inv),
                ShowLabels.ToString(),
                LabelFontSize.ToString(inv),
                ShowZeroGamma.ToString(),
                LevelLineWidth.ToString(inv),
                LineLeftBars.ToString(inv),
                LineRightBars.ToString(inv),
                ShowProfileBars.ToString(),
                ProfileAllToRight.ToString(),
                ProfileWidthBars.ToString(inv),
                ProfileBarHeightTicks.ToString(inv),
                ProfileScaleMax.ToString("0.########", inv),
                AutoScaleProfileMax.ToString(),
                MaxProfileRows.ToString(inv),
                // v1.1
                ShowBreakoutLevels.ToString(),
                ShowBosM.ToString(), ShowBosW.ToString(), ShowBosD.ToString(), ShowBosH4.ToString(), ShowBosH1.ToString(),
                BosKeepPerTf.ToString(inv),
                ShowAboveH1.ToString(),
                ShowConfluence.ToString(),
                ConfluenceMinSize.ToString(inv),
                ConfluenceEmPct.ToString("0.########", inv),
                ShowAvwapLabels.ToString(),
                ShowSessionBoxes.ToString(),
                ShowBoxAsia.ToString(),
                ShowBoxEU.ToString(),
                ShowBoxPre.ToString(),
                ShowBoxUS.ToString(),
                ShowHistoricalSessions.ToString(),
                ShowAvwapAsia.ToString(),
                ShowAvwapEU.ToString(),
                ShowAvwapUS.ToString(),
                ShowAvwapPD.ToString(),
                ShowHistoricalAvwap.ToString(),
                AvwapLineWidth.ToString(inv),
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        //  v1.1 — SESSION AVWAP (local compute, semantics = TV Pine indicator)
        // ──────────────────────────────────────────────────────────────────────

        private DateTime ToEt(DateTime local)
        {
            if (_etTz == null)
            {
                try { _etTz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { try { _etTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } catch { } }
            }
            if (_etTz == null) return local; // last resort: assume chart already ET
            // NT8 bar times are in the machine's local time zone (Kind Unspecified).
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local, _etTz);
        }

        // Bar OPEN time in ET. NT8 Time[] holds the bar CLOSE time for time-based
        // bars — Pine session flags use the open, so shift back one period.
        private DateTime BarOpenEt(DateTime barCloseTime)
        {
            DateTime t = barCloseTime;
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
                t = t.AddMinutes(-BarsPeriod.Value);
            else if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
                t = t.AddSeconds(-BarsPeriod.Value);
            // Tick/Range/Volume bars: no fixed period — close time is the best proxy.
            return ToEt(t);
        }

        private void ComputeAvwapCurrentBar()
        {
            if (CurrentBar > _avwapLastComputedBar)
            {
                // First tick of this bar — snapshot state BEFORE processing it, so
                // intrabar re-entries can roll back and re-accumulate exactly once.
                _sASumPV = _aSumPV; _sASumV = _aSumV;
                _sESumPV = _eSumPV; _sESumV = _eSumV;
                _sUSumPV = _uSumPV; _sUSumV = _uSumV;
                _sPdSumPV = _pdSumPV; _sPdSumV = _pdSumV;
                _sAsiaActive = _asiaActive; _sEuActive = _euActive; _sUsActive = _usActive;
                _sInCurrentDay = _inCurrentDay;
                _sPrevInAsia = _prevInAsia; _sPrevInEU = _prevInEU; _sPrevInUS = _prevInUS;
                _avwapLastComputedBar = CurrentBar;
            }
            else if (CurrentBar == _avwapLastComputedBar)
            {
                _aSumPV = _sASumPV; _aSumV = _sASumV;
                _eSumPV = _sESumPV; _eSumV = _sESumV;
                _uSumPV = _sUSumPV; _uSumV = _sUSumV;
                _pdSumPV = _sPdSumPV; _pdSumV = _sPdSumV;
                _asiaActive = _sAsiaActive; _euActive = _sEuActive; _usActive = _sUsActive;
                _inCurrentDay = _sInCurrentDay;
                _prevInAsia = _sPrevInAsia; _prevInEU = _sPrevInEU; _prevInUS = _sPrevInUS;
            }
            else
            {
                return; // out-of-order replay — skip
            }

            DateTime et = BarOpenEt(Time[0]);
            int mins = et.Hour * 60 + et.Minute;
            bool inAsia = mins >= 1080 || mins < 180;   // 18:00–03:00
            bool inEU   = mins >= 180 && mins < 480;    // 03:00–08:00
            bool inUS   = mins >= 570 && mins < 960;    // 09:30–16:00
            bool inGap  = mins >= 960 && mins < 1080;   // 16:00–18:00

            bool asiaStart = inAsia && !_prevInAsia;
            bool euStart   = inEU && !_prevInEU;
            bool usStart   = inUS && !_prevInUS;
            _prevInAsia = inAsia; _prevInEU = inEU; _prevInUS = inUS;

            if (asiaStart) _inCurrentDay = true;
            if (inGap) _inCurrentDay = false;

            double src = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0] > 0 ? Volume[0] : 1.0; // Pine nz(volume, 1)

            if (asiaStart)
            {
                // New futures day: promote US → PD, reset Asia, EU/US not started yet.
                if (_uSumV > 0) { _pdSumPV = _uSumPV; _pdSumV = _uSumV; }
                _aSumPV = src * vol; _aSumV = vol; _asiaActive = true;
                _euActive = false; _usActive = false;
            }
            else if (_asiaActive && _inCurrentDay) { _aSumPV += src * vol; _aSumV += vol; }

            if (euStart) { _eSumPV = src * vol; _eSumV = vol; _euActive = true; }
            else if (_euActive && _inCurrentDay) { _eSumPV += src * vol; _eSumV += vol; }

            if (usStart) { _uSumPV = src * vol; _uSumV = vol; _usActive = true; }
            else if (_usActive && inUS) { _uSumPV += src * vol; _uSumV += vol; }

            // PD keeps accumulating on current-day bars (Pine behaviour).
            if (!asiaStart && _pdSumV > 0 && _inCurrentDay) { _pdSumPV += src * vol; _pdSumV += vol; }

            // "recent" = within 30h of the chart's LAST bar (not wall-clock now) so a weekend /
            // closed-market chart still shows the latest session's AVWAP instead of hiding everything.
            bool recent = ShowHistoricalAvwap || Time[0] >= Bars.GetTime(Bars.Count - 1).AddHours(-30);

            SetAvwapPlot(0, ShowAvwapAsia && _asiaActive && _inCurrentDay && recent && _aSumV > 0 ? _aSumPV / _aSumV : double.NaN);
            SetAvwapPlot(1, ShowAvwapEU && _euActive && _inCurrentDay && recent && _eSumV > 0 ? _eSumPV / _eSumV : double.NaN);
            SetAvwapPlot(2, ShowAvwapUS && _usActive && inUS && recent && _uSumV > 0 ? _uSumPV / _uSumV : double.NaN);
            SetAvwapPlot(3, ShowAvwapPD && _pdSumV > 0 && _inCurrentDay && recent ? _pdSumPV / _pdSumV : double.NaN);
        }

        private void SetAvwapPlot(int i, double v)
        {
            if (i >= Values.Length) return;
            if (double.IsNaN(v)) Values[i].Reset();
            else Values[i][0] = v;
        }

        // ──────────────────────────────────────────────────────────────────────
        //  v1.1 — LOCAL BOS (H4/H1) — 1:1 with the ATAS port of the terminal's
        //  clientEngineService.computeAllBOS. Strict quality rule (body beyond
        //  the broken level must exceed the shadow) + 1-bar invalidation: a
        //  later close back through the level kills the BOS. NOT relaxable.
        // ──────────────────────────────────────────────────────────────────────

        private static bool IsQualityBreakout(DBar curr, DBar prev, bool bull)
        {
            if (bull)
            {
                if (curr.C <= prev.H) return false;
                double bodyAbove = curr.C - prev.H;
                double upperShadow = curr.H - curr.C;
                return bodyAbove > upperShadow && bodyAbove > 0;
            }
            else
            {
                if (curr.C >= prev.L) return false;
                double bodyBelow = prev.L - curr.C;
                double lowerShadow = curr.C - curr.L;
                return bodyBelow > lowerShadow && bodyBelow > 0;
            }
        }

        private static long FloorDiv(long a, long b) { long q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }

        // Chart bars -> period buckets (minutes) anchored at 18:00 ET (Globex open).
        private List<DBar> AggregateByEtPeriod(int periodMinutes)
        {
            var outl = new List<DBar>();
            bool has = false; long curSlot = long.MinValue; DBar cur = default(DBar);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            const long anchor = 18 * 60; // 18:00 ET, minutes
            int count = Bars.Count;
            for (int i = 0; i < count; i++)
            {
                DateTime et = BarOpenEt(Bars.GetTime(i));
                long etMin = (long)Math.Floor((et - epoch).TotalMinutes);
                long slot = FloorDiv(etMin - anchor, periodMinutes) * periodMinutes + anchor;
                double o = Bars.GetOpen(i), h = Bars.GetHigh(i), l = Bars.GetLow(i), c = Bars.GetClose(i);
                if (!has || slot != curSlot)
                {
                    if (has) outl.Add(cur);
                    has = true; curSlot = slot;
                    cur = new DBar { O = o, H = h, L = l, C = c };
                }
                else
                {
                    if (h > cur.H) cur.H = h;
                    if (l < cur.L) cur.L = l;
                    cur.C = c;
                }
            }
            if (has) outl.Add(cur);
            return outl;
        }

        // Futures day (18:00 ET boundary) of a chart bar
        private DateTime FuturesDayOf(int i)
        {
            DateTime et = BarOpenEt(Bars.GetTime(i));
            return et.Hour >= 18 ? et.Date.AddDays(1) : et.Date;
        }

        // Chart bars -> one bar per calendar key of the futures day (week = its Monday, month = its 1st)
        private List<DBar> AggregateByKey(Func<DateTime, DateTime> keyOf)
        {
            var outl = new List<DBar>();
            bool has = false; DateTime curKey = DateTime.MinValue; DBar cur = default(DBar);
            int count = Bars.Count;
            for (int i = 0; i < count; i++)
            {
                DateTime k = keyOf(FuturesDayOf(i));
                double o = Bars.GetOpen(i), h = Bars.GetHigh(i), l = Bars.GetLow(i), c = Bars.GetClose(i);
                if (!has || k != curKey)
                {
                    if (has) outl.Add(cur);
                    has = true; curKey = k;
                    cur = new DBar { O = o, H = h, L = l, C = c };
                }
                else
                {
                    if (h > cur.H) cur.H = h;
                    if (l < cur.L) cur.L = l;
                    cur.C = c;
                }
            }
            if (has) outl.Add(cur);
            return outl;
        }

        // Breakouts of one timeframe, CLOSED bars only (the last element is the bar in progress and
        // is never a breakout candidate). A breakout dies when a later bar of the SAME timeframe, in
        // the opposite direction, closes back through its level — a long BO H1 lives until a bearish
        // H1 bar closes below it. Of the survivors, the last BosKeep per timeframe are drawn.
        private void DetectBosInto(List<DBar> c, string tf)
        {
            int lastClosed = c.Count - 2;            // c[Count-1] is still forming
            if (lastClosed < 1) return;
            var alive = new List<LevelEntry>();
            for (int i = 1; i <= lastClosed; i++)
            {
                var curr = c[i]; var prev = c[i - 1];
                if (IsQualityBreakout(curr, prev, true))
                {
                    bool killed = false;
                    for (int j = i + 1; j <= lastClosed; j++) { var b = c[j]; if (b.C < prev.H && b.C < b.O) { killed = true; break; } }
                    if (!killed) alive.Add(MakeBos(prev.H, true, tf));
                }
                if (IsQualityBreakout(curr, prev, false))
                {
                    bool killed = false;
                    for (int j = i + 1; j <= lastClosed; j++) { var b = c[j]; if (b.C > prev.L && b.C > b.O) { killed = true; break; } }
                    if (!killed) alive.Add(MakeBos(prev.L, false, tf));
                }
            }
            int keep = Math.Max(1, BosKeepPerTf);
            int from = Math.Max(0, alive.Count - keep);
            for (int i = from; i < alive.Count; i++) _localBos.Add(alive[i]);
        }

        private LevelEntry MakeBos(double chartPrice, bool bull, string tf)
        {
            return new LevelEntry
            {
                EsStrike = chartPrice,       // chart-native price — see IsChartSpace
                Type = bull ? "BL" : "BS",
                Label = BoLabel(bull, tf),
                Tooltip = "",
                Magnitude = 0,
                IsChartSpace = true
            };
        }

        // Triangles by timeframe (▲ H1, ▲▲ H4, ▲▲▲ D, ▲▲▲▲ W, ▲▲▲▲▲ M), then "BO L/S <tf>".
        private static string BoLabel(bool bull, string tf)
        {
            string t = bull ? "\u25B2" : "\u25BC";
            int n = tf == "M" ? 5 : tf == "W" ? 4 : tf == "D" ? 3 : tf == "H4" ? 2 : 1;
            return new string(t[0], n) + " BO " + (bull ? "L" : "S") + " " + tf;
        }

        private void ComputeLocalBos()
        {
            if (CurrentBar == _bosLastComputedBar) return; // recompute on new bar only
            _bosLastComputedBar = CurrentBar;
            _localBos.Clear();
            if (ShowBosM) DetectBosInto(AggregateByKey(d => new DateTime(d.Year, d.Month, 1)), "M");
            if (ShowBosW) DetectBosInto(AggregateByKey(d => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)).Date), "W");
            if (ShowBosD) DetectBosInto(AggregateByEtPeriod(1440), "D");
            if (ShowBosH4) DetectBosInto(AggregateByEtPeriod(240), "H4");
            if (ShowBosH1) DetectBosInto(AggregateByEtPeriod(60), "H1");
        }

        private LevelEntry MakePA(double price, string type)
        {
            return new LevelEntry { EsStrike = price, Type = type, Label = type, Tooltip = "", Magnitude = 0, IsChartSpace = true };
        }

        // Local Structure: PDH/PDL = prior futures-day (18:00 ET) high/low; PWH/PWL = prior week high/low.
        // Computed from the chart's own bars (chart-native price, no spread conversion) — mirrors the
        // local BOS. Mother is GEX-only; price-dependent levels live in each front-end.
        private void ComputeLocalPA()
        {
            if (CurrentBar == _paLastComputedBar) return;
            _paLastComputedBar = CurrentBar;
            _localPA.Clear();
            int count = Bars.Count;
            if (count < 2) return;

            var dayH = new SortedDictionary<DateTime, double>();
            var dayL = new SortedDictionary<DateTime, double>();
            for (int i = 0; i < count; i++)
            {
                DateTime et = BarOpenEt(Bars.GetTime(i));
                DateTime fd = (et.Hour >= 18 ? et.Date.AddDays(1) : et.Date);   // futures day (18:00 ET boundary)
                double h = Bars.GetHigh(i), l = Bars.GetLow(i);
                if (!dayH.ContainsKey(fd)) { dayH[fd] = h; dayL[fd] = l; }
                else { if (h > dayH[fd]) dayH[fd] = h; if (l < dayL[fd]) dayL[fd] = l; }
            }
            var days = new List<DateTime>(dayH.Keys);
            if (days.Count >= 2)
            {
                var pd = days[days.Count - 2];   // prior completed day (last = current, in-progress)
                _localPA.Add(MakePA(dayH[pd], "PDH"));
                _localPA.Add(MakePA(dayL[pd], "PDL"));
            }

            var weekH = new SortedDictionary<DateTime, double>();
            var weekL = new SortedDictionary<DateTime, double>();
            foreach (var d in days)
            {
                int dow = (int)d.DayOfWeek;                 // Sunday = 0
                int back = (dow == 0 ? 6 : dow - 1);        // days since Monday
                DateTime wk = d.AddDays(-back).Date;        // Monday of that week
                if (!weekH.ContainsKey(wk)) { weekH[wk] = dayH[d]; weekL[wk] = dayL[d]; }
                else { if (dayH[d] > weekH[wk]) weekH[wk] = dayH[d]; if (dayL[d] < weekL[wk]) weekL[wk] = dayL[d]; }
            }
            var weeks = new List<DateTime>(weekH.Keys);
            if (weeks.Count >= 2)
            {
                var pw = weeks[weeks.Count - 2];
                _localPA.Add(MakePA(weekH[pw], "PWH"));
                _localPA.Add(MakePA(weekL[pw], "PWL"));
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  v1.1 — SESSION BOXES (= TV Pine): Asia/EU/Pre/US hi-lo rectangles.
        //  Stateless: rebuilt from the visible bar history on every redraw —
        //  no lifecycle state to corrupt across reconnects/reloads.
        // ──────────────────────────────────────────────────────────────────────

        private static char SessionTypeOf(int mins)
        {
            if (mins >= 1080 || mins < 180) return 'A';       // Asia 18:00–03:00
            if (mins >= 180 && mins < 480) return 'E';        // EU 03:00–08:00
            if (mins >= 480 && mins < 570) return 'P';        // Pre 08:00–09:30
            if (mins >= 570 && mins < 960) return 'U';        // US 09:30–16:00
            return ' ';                                       // gap 16:00–18:00
        }

        private void DrawSessionBoxes()
        {
            // Cap the scan: 30h unless historical toggle (then hard cap for perf).
            DateTime cutoff = Bars.GetTime(Bars.Count - 1).AddHours(-30);   // relative to chart's last bar (weekend-safe)
            int maxScan = ShowHistoricalSessions ? Math.Min(CurrentBar, 20000) : CurrentBar;

            // Find the oldest barsAgo inside the window.
            int oldest = 0;
            for (int b = 0; b <= maxScan; b++)
            {
                if (!ShowHistoricalSessions && Time[b] < cutoff) break;
                oldest = b;
            }
            if (oldest < 1) return;

            char runType = ' ';
            int runStart = -1, runEnd = -1;
            double runHi = 0, runLo = 0;
            int boxIdx = 0;

            Action flush = () =>
            {
                if (runType == ' ' || runStart < 0) return;
                bool show =
                    (runType == 'A' && ShowBoxAsia) ||
                    (runType == 'E' && ShowBoxEU) ||
                    (runType == 'P' && ShowBoxPre) ||
                    (runType == 'U' && ShowBoxUS);
                if (!show) return;

                Color c;
                switch (runType)
                {
                    case 'A': c = Color.FromRgb(0xf5, 0x9e, 0x0b); break; // amber
                    case 'E': c = Color.FromRgb(0x3b, 0x82, 0xf6); break; // blue
                    case 'P': c = Color.FromRgb(0xa8, 0x55, 0xf7); break; // purple
                    default:  c = Color.FromRgb(0x22, 0xc5, 0x5e); break; // green
                }
                string tag = $"TLADeSessBox_{runType}_{boxIdx}";
                Draw.Rectangle(this, tag, false,
                    runStart, runHi, runEnd, runLo,
                    MakeBrush(c, 77),          // border ≈ 30% alpha (Pine border 70 transp)
                    MakeBrush(c, 255), 8);     // area fill at 8% opacity (Pine 90-92 transp)
                _tags.Add(tag);
                boxIdx++;
            };

            for (int b = oldest; b >= 0; b--)   // oldest → newest
            {
                DateTime et = BarOpenEt(Time[b]);
                char t = SessionTypeOf(et.Hour * 60 + et.Minute);
                if (t != runType)
                {
                    flush();
                    runType = t; runStart = b; runEnd = b;
                    runHi = High[b]; runLo = Low[b];
                }
                else if (t != ' ')
                {
                    runEnd = b;
                    if (High[b] > runHi) runHi = High[b];
                    if (Low[b] < runLo) runLo = Low[b];
                }
            }
            flush();
        }

        private void DrawStatus()
        {
            int anchorBase = -Math.Max(0, RightOffsetBars);
            int labelAnchor = anchorBase + Math.Max(0, LabelPaddingBars);
            int profileAnchor = -(Math.Max(0, RightOffsetBars) + Math.Max(0, ProfileOffsetBars));

            int lineLeft = labelAnchor + Math.Max(0, LineLeftBars);
            if (lineLeft > CurrentBar) lineLeft = CurrentBar;
            int lineRight = labelAnchor - Math.Max(0, LineRightBars);

            string txt =
                $"TLADeGexDashboardNT\n" +
                $"levels: {_levels.Count}\n" +
                $"profile: {_profile.Count}\n" +
                $"baseAnchorBarsAgo: {anchorBase}\n" +
                $"labelAnchorBarsAgo: {labelAnchor}\n" +
                $"profileAnchorBarsAgo: {profileAnchor}\n" +
                $"lineLeftBarsAgo: {lineLeft}\n" +
                $"lineRightBarsAgo: {lineRight}\n" +
                $"ProfileAllToRight: {ProfileAllToRight}\n" +
                $"bar: {CurrentBar}";

            Draw.TextFixed(this, "TLADeStatus", txt,
                TextPosition.TopLeft,
                Brushes.DimGray,
                _labelFont ?? new SimpleFont("Arial", 12),
                Brushes.Transparent,
                Brushes.Transparent,
                0);
        }

        private void UpdateLabelFont()
        {
            if (LabelFontSize < 6) LabelFontSize = 6;
            if (LabelFontSize > 50) LabelFontSize = 50;

            if (_labelFont == null || _prevFontSize != LabelFontSize)
            {
                _prevFontSize = LabelFontSize;
                _labelFont = new SimpleFont("Arial", LabelFontSize);
            }
        }

        private bool ParseIfChanged(bool force)
        {
            string raw = (GexDataInput ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (!force && string.Equals(raw, _prevInput, StringComparison.Ordinal))
                return false;

            _prevInput = raw;
            _levels.Clear();
            _profile.Clear();

            if (string.IsNullOrEmpty(raw))
                return true;

            // v7.1: Strip S: spread prefix if present (e.g. "S:49.74|L:...|P:...")
            // Allows real-time ES-SPX spread embedded by TLADe export without breaking parsing
            if (raw.StartsWith("S:", StringComparison.Ordinal))
            {
                int sEnd = raw.IndexOf('|');
                if (sEnd > 2)
                {
                    string spreadStr = raw.Substring(2, sEnd - 2);
                    if (double.TryParse(spreadStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedSpread)
                        && parsedSpread > 0)
                    {
                        if (IsNqFamily()) NqNdxSpread = parsedSpread;
                        else EsSpxSpread = parsedSpread;
                    }
                    raw = raw.Substring(sEnd + 1);
                }
            }
            // R: NDX→QQQ ratio (NQ family strings only)
            if (raw.StartsWith("R:", StringComparison.Ordinal))
            {
                int rEnd = raw.IndexOf('|');
                if (rEnd > 2)
                {
                    if (double.TryParse(raw.Substring(2, rEnd - 2), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double parsedRatio) && parsedRatio > 0)
                        _ndxQqqRatio = parsedRatio;
                    raw = raw.Substring(rEnd + 1);
                }
            }

            string levelsData = "";
            string profileData = "";

            int pipePos = raw.IndexOf("|P:", StringComparison.Ordinal);
            if (pipePos >= 0)
            {
                string beforePipe = raw.Substring(0, pipePos);
                if (beforePipe.StartsWith("L:", StringComparison.Ordinal))
                    levelsData = beforePipe.Substring(2);

                profileData = raw.Substring(pipePos + 3);
            }
            else
            {
                if (raw.StartsWith("L:", StringComparison.Ordinal))
                    levelsData = raw.Substring(2);
            }

            if (!string.IsNullOrEmpty(levelsData))
            {
                string[] pairs = levelsData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in pairs)
                {
                    string[] parts = p.Split(',');
                    if (parts.Length < 3) continue;

                    if (!double.TryParse(parts[0], NumberStyles.Any, inv, out double strike))
                        continue;

                    string type = (parts[1] ?? "").Trim();
                    string label = (parts[2] ?? "").Trim();

                    string tooltip = parts.Length >= 4 ? (parts[3] ?? "") : "";
                    tooltip = tooltip.Replace("~", "\n");

                    double mag = 0.0;
                    if (parts.Length >= 5)
                        double.TryParse(parts[4], NumberStyles.Any, inv, out mag);

                    _levels.Add(new LevelEntry
                    {
                        EsStrike = strike,
                        Type = type,
                        Label = label,
                        Tooltip = tooltip,
                        Magnitude = Math.Abs(mag)
                    });
                }
            }

            ResetWallFlips();

            if (!string.IsNullOrEmpty(profileData))
            {
                string[] rows = profileData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                int n = 0;

                foreach (var r in rows)
                {
                    if (n >= MaxProfileRows)
                        break;

                    string[] parts = r.Split(',');
                    if (parts.Length != 3)
                        continue;

                    if (!double.TryParse(parts[0], NumberStyles.Any, inv, out double strike)) continue;
                    if (!double.TryParse(parts[1], NumberStyles.Any, inv, out double value)) continue;
                    if (!double.TryParse(parts[2], NumberStyles.Any, inv, out double sign)) continue;

                    _profile.Add(new ProfileEntry { EsStrike = strike, Value = value, Sign = sign });
                    n++;
                }
            }

            return true;
        }

        private void DrawAll()
        {
            ClearAll();
            _alertLevels.Clear();
            if (!TfActive())
            {
                for (int i = 0; i < 4 && i < Values.Length; i++) Values[i].Reset();
                return;
            }

            double spot = Close[0];

            // Base anchor for layout (future)
            int anchorBaseBarsAgo = -Math.Max(0, RightOffsetBars);

            // Label + line anchor: move LEFT by padding so profile doesn't overwrite it
            int labelBarsAgo = anchorBaseBarsAgo + Math.Max(0, LabelPaddingBars);

            // Profile anchor: move further RIGHT by ProfileOffsetBars
            int profileBarsAgo = -(Math.Max(0, RightOffsetBars) + Math.Max(0, ProfileOffsetBars));

            // Clamp only on the past side (BarsAgo cannot exceed CurrentBar)
            if (labelBarsAgo > CurrentBar) labelBarsAgo = CurrentBar;

            int leftBarsAgo = labelBarsAgo + Math.Max(0, LineLeftBars);
            if (leftBarsAgo > CurrentBar) leftBarsAgo = CurrentBar;

            // Level lines stop at the label position (labels sit to the right of the line end)
            int rightBarsAgo = LineRightBars > 0
                ? labelBarsAgo - Math.Max(0, LineRightBars)
                : labelBarsAgo;

            // Labels sit to the right of line end, in the gap before the profile
            int labelPosBarsAgo = labelBarsAgo - 6;

            // v1.1 — session boxes first so lines/labels draw on top
            if (ShowSessionBoxes)
                DrawSessionBoxes();

            if (_levels.Count > 0)
                DrawLevels(spot, labelPosBarsAgo, leftBarsAgo, rightBarsAgo);

            // v1.1 — local BOS levels (chart-space, no spread conversion)
            if (ShowBreakoutLevels)
            {
                ComputeLocalBos();
                DrawBosLevels(spot, labelPosBarsAgo, leftBarsAgo, rightBarsAgo);
            }

            // v3.3 — local Structure (PDH/PDL/PWH/PWL) from chart bars (Mother is GEX-only;
            // price-dependent levels are computed front-end-side, no longer shipped in the feed)
            if (ShowStructureLevels)
            {
                ComputeLocalPA();
                DrawPALevels(spot, labelPosBarsAgo, leftBarsAgo, rightBarsAgo);
            }

            if (ShowProfileBars && _profile.Count > 0)
                DrawProfileBars(profileBarsAgo);

            if (ShowAvwapLabels)
                DrawAvwapLabels(labelPosBarsAgo);

            if (ShowConfluence)
                DrawConfluenceZones(labelPosBarsAgo, leftBarsAgo, rightBarsAgo);
        }

        // AVWAP labels at the same anchor and in the same "price + name" form as every other label
        private void DrawAvwapLabels(int labelBarsAgo)
        {
            string[] names = { "AVWAP Asia", "AVWAP EU", "AVWAP US", "AVWAP US Prev Day" };
            bool[] on = { ShowAvwapAsia, ShowAvwapEU, ShowAvwapUS, ShowAvwapPD };
            for (int i = 0; i < 4 && i < Values.Length; i++)
            {
                if (!on[i] || !Values[i].IsValidDataPoint(0)) continue;
                double v = Values[i][0];
                if (double.IsNaN(v) || v <= 0) continue;
                string tag = $"TLADeAvwapLbl_{i}";
                Brush b = Plots[i].Brush;
                var t = Draw.Text(this, tag, $"{v.ToString("0.##", inv)} {names[i]}", labelBarsAgo, v, b);
                if (t != null && _labelFont != null) t.Font = _labelFont;
                _tags.Add(tag);
            }
        }

        // Confluence zones: every level on the chart (feed levels, breakouts, AVWAPs, structure) sorted
        // by price and clustered within a band sized on the EM range — the TradingView rule. No EM in
        // the data → no zones, never a default width.
        private void DrawConfluenceZones(int labelBarsAgo, int leftBarsAgo, int rightBarsAgo)
        {
            double emH = double.NaN, emL = double.NaN;
            foreach (var lvl in _levels)
            {
                if (lvl.Type == "EH") emH = ConvertPrice(lvl.EsStrike);
                else if (lvl.Type == "EL") emL = ConvertPrice(lvl.EsStrike);
            }
            if (double.IsNaN(emH) || double.IsNaN(emL) || emH - emL <= 0) return;
            double band = (emH - emL) * (ConfluenceEmPct / 100.0) / 2.0;   // half-band

            var prices = new List<double>();
            var names = new List<string>();
            foreach (var lvl in _levels)
            {
                if (!(IsGex(lvl.Type) || IsSystem(lvl.Type))) continue;
                prices.Add(ConvertPrice(lvl.EsStrike));
                names.Add($"{FormatPrice(lvl.EsStrike)} {LevelName(lvl.Type, lvl.Label)}");
            }
            if (ShowBreakoutLevels)
                foreach (var bo in _localBos) { prices.Add(bo.EsStrike); names.Add($"{Math.Round(bo.EsStrike).ToString("0", inv)} {bo.Label}"); }
            string[] avNames = { "AVWAP Asia", "AVWAP EU", "AVWAP US", "AVWAP US Prev Day" };
            bool[] avOn = { ShowAvwapAsia, ShowAvwapEU, ShowAvwapUS, ShowAvwapPD };
            for (int i = 0; i < 4 && i < Values.Length; i++)
            {
                if (!avOn[i] || !Values[i].IsValidDataPoint(0)) continue;
                double v = Values[i][0];
                if (double.IsNaN(v) || v <= 0) continue;
                prices.Add(v); names.Add($"{Math.Round(v).ToString("0", inv)} {avNames[i]}");
            }
            if (ShowStructureLevels)
                foreach (var pa in _localPA) { prices.Add(pa.EsStrike); names.Add($"{Math.Round(pa.EsStrike).ToString("0", inv)} {LevelName(pa.Type, pa.Label)}"); }

            int n = prices.Count;
            if (n < 2) return;
            var idx = new List<int>();
            for (int i = 0; i < n; i++) idx.Add(i);
            idx.Sort((a, b) => prices[a].CompareTo(prices[b]));

            int minSize = Math.Max(2, ConfluenceMinSize);
            int boxNo = 0;
            int i0 = 0;
            while (i0 < n)
            {
                double cMin = prices[idx[i0]], cMax = cMin;
                var items = new List<string> { names[idx[i0]] };
                int j = i0 + 1;
                while (j < n)
                {
                    double next = prices[idx[j]];
                    if (next - cMin <= 2 * band) { cMax = next; items.Add(names[idx[j]]); j++; }
                    else break;
                }
                int size = items.Count;
                if (size >= minSize)
                {
                    int capped = Math.Min(size, 5);
                    // TradingView colours: 3 → #facc15 @25%, 4 → #fb923c @30%, 5+ → #ef4444 @35%
                    Color c = capped >= 5 ? Color.FromRgb(0xef, 0x44, 0x44) : capped >= 4 ? Color.FromRgb(0xfb, 0x92, 0x3c) : Color.FromRgb(0xfa, 0xcc, 0x15);
                    int opacity = capped >= 5 ? 35 : capped >= 4 ? 30 : 25;
                    double top = cMax, bottom = cMin;
                    if (top - bottom < TickSize) { top += TickSize / 2.0; bottom -= TickSize / 2.0; }
                    string tag = $"TLADeConf_{boxNo}";
                    Draw.Rectangle(this, tag, false, leftBarsAgo, top, rightBarsAgo, bottom, MakeBrush(c, 128), MakeBrush(c, 255), opacity);
                    _tags.Add(tag);
                    string txt = capped >= 5 ? "UBER" : capped + "x";
                    string ttag = $"TLADeConfLbl_{boxNo}";
                    var t = Draw.Text(this, ttag, txt, rightBarsAgo, (top + bottom) / 2.0, MakeBrush(c, 255));
                    if (t != null && _labelFont != null) t.Font = _labelFont;
                    _tags.Add(ttag);
                    boxNo++;
                }
                i0 = j;
            }
        }

        private void DrawBosLevels(double spot, int labelBarsAgo, int leftBarsAgo, int rightBarsAgo)
        {
            Brush blBrush = MakeBrush(C_BO_BULL, 255);
            Brush bsBrush = MakeBrush(C_BO_BEAR, 255);

            foreach (var lvl in _localBos)
            {
                double y = lvl.EsStrike; // chart-native price, no ConvertPrice
                bool inRange = !ShowOnlyNear || (Math.Abs(spot - y) / Math.Max(1e-9, spot) * 100.0 <= NearPct);
                if (!inRange) continue;

                Brush lvlBrush = lvl.Type == "BL" ? blBrush : bsBrush;
                string tf = lvl.Label.Substring(lvl.Label.LastIndexOf(' ') + 1);
                // M/W/D solid and heavier, H4/H1 dashed and lighter — as the TradingView indicator
                bool higherTf = tf == "M" || tf == "W" || tf == "D";
                int baseW = Math.Max(1, Math.Min(10, LevelLineWidth));
                int w = tf == "M" ? baseW + 2 : tf == "W" ? baseW + 1 : tf == "D" ? baseW : Math.Max(1, baseW - 1);
                string key = $"{lvl.Type}_{tf}_{y.ToString("0.####", inv)}";

                string lineTag = $"TLADeLine_{key}";
                Draw.Line(this, lineTag, false, leftBarsAgo, y, rightBarsAgo, y, lvlBrush, higherTf ? DashStyleHelper.Solid : DashStyleHelper.Dash, w);
                _tags.Add(lineTag);
                _alertLevels.Add(new AlertLevel { Price = y, Name = $"{lvl.Label} {y.ToString("0.##", inv)}", Kind = "bo" });

                if (ShowLabels)
                {
                    string textTag = $"TLADeText_{key}";
                    string display = $"{lvl.Label} {(DisplayScale() == "SPY" || DisplayScale() == "QQQ" ? y.ToString("0.##", inv) : y.ToString("0.##", inv))}";
                    var t = Draw.Text(this, textTag, display, labelBarsAgo, y, lvlBrush);
                    if (t != null && _labelFont != null)
                        t.Font = _labelFont;
                    _tags.Add(textTag);
                }
            }
        }

        private void DrawPALevels(double spot, int labelBarsAgo, int leftBarsAgo, int rightBarsAgo)
        {
            Brush paBrush = MakeBrush(C_STRUCT, 255); // Structure (PDH/PDL/PWH/PWL)
            foreach (var lvl in _localPA)
            {
                double y = lvl.EsStrike; // chart-native price
                bool inRange = !ShowOnlyNear || (Math.Abs(spot - y) / Math.Max(1e-9, spot) * 100.0 <= NearPct);
                if (!inRange) continue;

                int paW = Math.Max(1, Math.Min(10, LevelLineWidth));
                string key = $"{lvl.Type}_{y.ToString("0.####", inv)}";
                string lineTag = $"TLADeLine_{key}";
                Draw.Line(this, lineTag, false, leftBarsAgo, y, rightBarsAgo, y, paBrush, DashStyleHelper.Dot, paW);
                _tags.Add(lineTag);

                if (ShowLabels)
                {
                    string textTag = $"TLADeText_{key}";
                    string display = $"{(DisplayScale() == "SPY" || DisplayScale() == "QQQ" ? y.ToString("0.##", inv) : Math.Round(y).ToString("0", inv))} {LevelName(lvl.Type, lvl.Label)}";
                    var t = Draw.Text(this, textTag, display, labelBarsAgo, y, paBrush);
                    if (t != null && _labelFont != null) t.Font = _labelFont;
                    _tags.Add(textTag);
                }
            }
        }

        private void DrawLevels(double spot, int labelBarsAgo, int leftBarsAgo, int rightBarsAgo)
        {
            // Without the spread a cash chart gets NOTHING, and is told why. Drawing the
            // strikes in futures points instead - the old behaviour - put a number on the
            // axis that looks right and is not. (Stefano, 7/10/2026)
            if (CashScaleWithoutSpread())
            {
                Draw.TextFixed(this, "TLADeNoSpread",
                    "TLADe - no levels on " + DisplayScale() + Environment.NewLine +
                    "waiting for the spread from the data feed." + Environment.NewLine +
                    "Cash levels are not drawn without it.",
                    TextPosition.TopRight,
                    Brushes.Goldenrod,
                    _labelFont ?? new SimpleFont("Arial", 12),
                    Brushes.Transparent,
                    Brushes.Transparent,
                    0);
                return;
            }

            int maxVisible = MaxGexLevels <= 0 ? 10 : MaxGexLevels;
            int halfMax = (int)Math.Ceiling(maxVisible / 2.0);
            bool maxIsAll = maxVisible >= 999;

            double closestAboveEs = double.NaN, closestBelowEs = double.NaN;
            double closestAboveDist = double.MaxValue, closestBelowDist = double.MaxValue;

            foreach (var lvl in _levels)
            {
                if (!IsGex(lvl.Type)) continue;
                double y0 = ConvertPrice(lvl.EsStrike);
                double dist = Math.Abs(y0 - spot);

                if (y0 > spot && dist < closestAboveDist) { closestAboveDist = dist; closestAboveEs = lvl.EsStrike; }
                else if (y0 <= spot && dist < closestBelowDist) { closestBelowDist = dist; closestBelowEs = lvl.EsStrike; }
            }

            int gexAboveDrawn = 0, gexBelowDrawn = 0;

            foreach (var lvl in _levels)
            {
                double y = ConvertPrice(lvl.EsStrike);

                bool inRange = !ShowOnlyNear || (Math.Abs(spot - y) / Math.Max(1e-9, spot) * 100.0 <= NearPct);

                bool isGex = IsGex(lvl.Type);
                bool isSystem = IsSystem(lvl.Type);
                bool isStructure = IsStructure(lvl.Type);

                bool passesThreshold =
                    !EnableThreshold ||
                    !isGex ||
                    lvl.Magnitude == 0.0 ||
                    lvl.Magnitude >= GexThreshold;

                bool isProtected = isGex && (NearlyEqual(lvl.EsStrike, closestAboveEs) || NearlyEqual(lvl.EsStrike, closestBelowEs));
                bool isAbove = y > spot;

                bool shouldShow = false;

                if (isGex && ShowGexLevels && passesThreshold)
                {
                    if (maxIsAll) shouldShow = true;
                    else if (isProtected) shouldShow = true;
                    else if (isAbove && gexAboveDrawn < halfMax) shouldShow = true;
                    else if (!isAbove && gexBelowDrawn < halfMax) shouldShow = true;
                }
                else if (isSystem && (ShowSystemLevels || (lvl.Type == "ZG" && ShowZeroGamma))) shouldShow = true;
                else if (IsCharm(lvl.Type) && ShowCharmMagnet) shouldShow = true;
                else if (IsDeltaFlip(lvl.Type) && ShowDeltaFlip) shouldShow = true;
                // Structure (PDH/PDL/PWH/PWL) is now computed locally (DrawPALevels), not from the
                // feed — Mother is GEX-only. Feed structure, if any, is intentionally not drawn here.
                else if (isStructure) shouldShow = false;

                if (!shouldShow || !inRange)
                    continue;

                Brush lvlBrush = Brushes.Gray;
                bool isWallFlipped = false;

                // Colour = NATURE (a Call Wall stays the call colour whatever its role). A flipped
                // wall is told by a thinner line and the "↺" suffix, never by swapping colours.
                if (lvl.Type == "CW")
                {
                    isWallFlipped = IsWallFlipped(lvl.EsStrike);
                    lvlBrush = _negBrush;
                    if (!maxIsAll && !isProtected) { if (isAbove) gexAboveDrawn++; else gexBelowDrawn++; }
                }
                else if (lvl.Type == "PW")
                {
                    isWallFlipped = IsWallFlipped(lvl.EsStrike);
                    lvlBrush = _posBrush;
                    if (!maxIsAll && !isProtected) { if (isAbove) gexAboveDrawn++; else gexBelowDrawn++; }
                }
                else if (lvl.Type == "ZG") lvlBrush = MakeBrush(C_ZG, 255);
                else if (lvl.Type == "MP") lvlBrush = MakeBrush(C_MP, 255);
                else if (lvl.Type == "EH" || lvl.Type == "EL" || lvl.Type == "EHR" || lvl.Type == "ELR") lvlBrush = MakeBrush(C_EM, 255);
                else if (lvl.Type == "VH" || lvl.Type == "VL") lvlBrush = MakeBrush(C_VB, 255);
                else if (IsCharm(lvl.Type)) lvlBrush = MakeBrush(C_CHARM, 255);
                else if (IsDeltaFlip(lvl.Type)) lvlBrush = MakeBrush(C_DFLIP, 255);
                else if (isStructure) lvlBrush = MakeBrush(C_STRUCT, 255);

                string key = $"{lvl.Type}_{lvl.EsStrike.ToString("0.####", inv)}";

                string lineTag = $"TLADeLine_{key}";
                // Line styles as the TradingView indicator: walls and Zero Gamma in the user's style,
                // Max Pain / Vol Bands dotted, EM and Delta Flip dashed, Charm Magnet solid.
                DashStyleHelper lineStyle =
                    IsCharm(lvl.Type) ? DashStyleHelper.Solid :
                    (lvl.Type == "MP" || lvl.Type == "VH" || lvl.Type == "VL" || isStructure) ? DashStyleHelper.Dot :
                    (lvl.Type == "EH" || lvl.Type == "EL" || lvl.Type == "EHR" || lvl.Type == "ELR" || IsDeltaFlip(lvl.Type)) ? DashStyleHelper.Dash :
                    UserLineStyle();
                int userW = Math.Max(1, Math.Min(10, LevelLineWidth));
                int lineWidth = (lvl.Type == "ZG" || lvl.Type == "MP") ? Math.Max(2, userW) : userW;
                if (isWallFlipped) lineWidth = Math.Max(1, lineWidth - 1);
                Draw.Line(this, lineTag, false, leftBarsAgo, y, rightBarsAgo, y, lvlBrush, lineStyle, lineWidth);
                _tags.Add(lineTag);

                if (ShowLabels)
                {
                    string textTag = $"TLADeText_{key}";
                    string display = $"{FormatPrice(lvl.EsStrike)} {LevelName(lvl.Type, lvl.Label)}{(isWallFlipped ? " \u21BA" : "")}";
                    var t = Draw.Text(this, textTag, display, labelBarsAgo, y, lvlBrush);
                    if (t != null && _labelFont != null)
                        t.Font = _labelFont;
                    _tags.Add(textTag);
                }
                if (isGex || isSystem)
                    _alertLevels.Add(new AlertLevel { Price = y, Name = $"{FormatPrice(lvl.EsStrike)} {LevelName(lvl.Type, lvl.Label)}", Kind = isGex ? "wall" : "system" });
            }
        }

        private void DrawProfileBars(int profileBarsAgo)
        {
            double maxAbs = Math.Max(1e-9, ProfileScaleMax);
            if (AutoScaleProfileMax)
            {
                double dataMax = 0.0;
                for (int i = 0; i < _profile.Count; i++)
                    dataMax = Math.Max(dataMax, Math.Abs(_profile[i].Value));
                maxAbs = Math.Max(maxAbs, dataMax);
            }

            double halfHeight = (Math.Max(1, ProfileBarHeightTicks) * TickSize) / 2.0;
            int widthBars = Math.Max(1, ProfileWidthBars);

            for (int i = 0; i < _profile.Count; i++)
            {
                var pr = _profile[i];
                double y = ConvertPrice(pr.EsStrike);

                double lenValue = ProfileAllToRight ? Math.Abs(pr.Value) : pr.Value;

                int barLen = (int)Math.Round((lenValue / maxAbs) * widthBars);
                if (barLen == 0)
                    continue;

                int start = profileBarsAgo;

                // If AllToRight: ALWAYS extend farther right into future (more negative barsAgo)
                int end = ProfileAllToRight
                    ? (profileBarsAgo - Math.Abs(barLen))
                    : (profileBarsAgo - barLen);

                // clamp only past-side (barsAgo cannot exceed CurrentBar)
                if (start > CurrentBar) start = CurrentBar;
                if (end > CurrentBar) end = CurrentBar;
                if (start == end) continue;

                int x1 = Math.Max(start, end);
                int x2 = Math.Min(start, end);

                // call=negColor, put=posColor (inverted from level colors, matching TV)
                Brush baseBrush = (pr.Sign >= 0) ? _profCallBrush : _profPutBrush;
                Color baseC = ((SolidColorBrush)baseBrush).Color;

                double intensity = Math.Min(1.0, Math.Abs(pr.Value) / maxAbs);
                // Alpha range: 60% (weakest) to 100% (strongest) — solid visible bars
                byte alpha = (byte)Math.Round(255.0 * (0.60 + intensity * 0.40));

                Brush fill = MakeBrush(baseC, alpha);
                Brush outline = MakeBrush(baseC, alpha);

                string tag = $"TLADeProf_{pr.EsStrike.ToString("0.####", inv)}_{i}";
                Draw.Rectangle(this, tag, false,
                    x1, y + halfHeight,
                    x2, y - halfHeight,
                    outline, fill, 1);

                _tags.Add(tag);
            }
        }

        // The indicator is an intraday tool: above 1H (4H, Daily, Weekly…) it draws nothing unless asked.
        private bool TfActive()
        {
            if (ShowAboveH1) return true;
            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Minute: return BarsPeriod.Value <= 60;
                case BarsPeriodType.Second: return true;
                case BarsPeriodType.Day:
                case BarsPeriodType.Week:
                case BarsPeriodType.Month:
                case BarsPeriodType.Year: return false;
                default: return true;   // tick / range / volume bars: intraday
            }
        }

        private void ClearAll()
        {
            foreach (var t in _tags)
                RemoveDrawObject(t);
            _tags.Clear();
        }

        private bool IsGex(string t) => t == "CW" || t == "PW" || t == "GL";
        private bool IsSystem(string t) => t == "ZG" || t == "MP" || t == "EH" || t == "EL" || t == "EHR" || t == "ELR" || t == "VH" || t == "VL";
        private bool IsCharm(string t) => t == "CM";
        private bool IsDeltaFlip(string t) => t == "DF";

        // One name per level code, the same words on every platform. Labels read "price + name";
        // the code (CW, ZG, EHR…) is for the feed and the tooltip, not the chart.
        private static string LevelName(string code, string feedLabel)
        {
            switch (code)
            {
                case "CW": return "Call Wall";
                case "PW": return "Put Wall";
                case "GL": return "GEX Level";
                case "ZG": return "Zero Gamma";
                case "MP": return "Max Pain";
                case "EH": return "EM High Globex";
                case "EL": return "EM Low Globex";
                case "EHR": return "EM High RTH";
                case "ELR": return "EM Low RTH";
                case "VH": return "Vol High";
                case "VL": return "Vol Low";
                case "CM": return "Charm Magnet";
                case "DF": return "Delta Flip";
                case "PDH": return "Previous Day High";
                case "PDL": return "Previous Day Low";
                case "PWH": return "Previous Week High";
                case "PWL": return "Previous Week Low";
                default: return feedLabel ?? code;
            }
        }

        // The TradingView palette, so the two charts read the same
        private static readonly Color C_ZG = (Color)ColorConverter.ConvertFromString("#9ca3af");
        private static readonly Color C_MP = (Color)ColorConverter.ConvertFromString("#ef4444");
        private static readonly Color C_EM = (Color)ColorConverter.ConvertFromString("#3b82f6");
        private static readonly Color C_VB = (Color)ColorConverter.ConvertFromString("#9ca3af");
        private static readonly Color C_STRUCT = (Color)ColorConverter.ConvertFromString("#9ca3af");
        private static readonly Color C_CHARM = (Color)ColorConverter.ConvertFromString("#f97316");
        private static readonly Color C_DFLIP = (Color)ColorConverter.ConvertFromString("#f59e0b");
        private static readonly Color C_BO_BULL = (Color)ColorConverter.ConvertFromString("#22c55e");
        private static readonly Color C_BO_BEAR = (Color)ColorConverter.ConvertFromString("#ef4444");
        private bool IsStructure(string t) => t == "PDH" || t == "PDL" || t == "PWH" || t == "PWL";

        private bool NearlyEqual(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            return Math.Abs(a - b) < 1e-9;
        }

        /// <summary>
        /// Runtime-resolved DisplayTicker. When AutoDetectTicker is true, this
        /// derives the display family from the chart's Instrument symbol
        /// (MNQ/NQ/NDX/QQQ → NQ family, MES/ES/SPX/SPY → ES family) so the
        /// levels land in the chart's own price range without the user having
        /// to configure it. The DisplayTicker property itself is never
        /// overwritten — NT8 would persist the override into the workspace
        /// and force the wrong value on a differently-symbolled chart.
        /// </summary>
        /// <summary>
        /// WHICH DATA to ask for: the instrument family, ES or NQ. SPX and SPY are the S&amp;P
        /// family, NDX and QQQ the Nasdaq one.
        /// </summary>
        private string DataFamily()
        {
            if (AutoDetectTicker && Instrument != null && Instrument.MasterInstrument != null)
            {
                string sym = (Instrument.MasterInstrument.Name ?? "").ToUpperInvariant();
                if (sym.StartsWith("MNQ") || sym.StartsWith("NQ") || sym == "NDX" || sym == "QQQ")
                    return "NQ";
                if (sym.StartsWith("MES") || sym.StartsWith("ES") || sym == "SPX" || sym == "SPY")
                    return "ES";
                // unknown symbol: fall through to the user's manual setting
            }
            string manual = (DisplayTicker ?? "ES").ToUpperInvariant();
            return (manual == "NQ" || manual == "NDX" || manual == "QQQ") ? "NQ" : "ES";
        }

        /// <summary>
        /// WHICH SCALE to draw in: the chart's own ticker, not its family.
        ///
        /// Keeping this apart from DataFamily() is the whole point. The two questions used to
        /// share one answer, which returned the FAMILY — so a SPY chart asked for S&amp;P data
        /// (right) and then drew it in ES points (wrong). The "SPY" and "QQQ" branches of
        /// ConvertPrice were unreachable whenever auto-detect was on, which is the default,
        /// and every level landed ten times above the top of the axis. Reported by a
        /// subscriber as "the indicator does not plot levels on SPY" (7/10/2026): they were
        /// plotted, just off-screen, which looks exactly the same.
        /// </summary>
        private string DisplayScale()
        {
            if (AutoDetectTicker && Instrument != null && Instrument.MasterInstrument != null)
            {
                string sym = (Instrument.MasterInstrument.Name ?? "").ToUpperInvariant();
                if (sym == "SPY") return "SPY";
                if (sym == "QQQ") return "QQQ";
                if (sym == "SPX") return "SPX";
                if (sym == "NDX") return "NDX";
                if (sym.StartsWith("MNQ") || sym.StartsWith("NQ")) return "NQ";
                if (sym.StartsWith("MES") || sym.StartsWith("ES")) return "ES";
                // unknown symbol: fall through to the user's manual setting
            }
            return (DisplayTicker ?? "ES").ToUpperInvariant();
        }

        /// <summary>
        /// The chart is drawn in a cash scale (SPX/SPY/NDX/QQQ) and we do not have the spread
        /// for it. Nothing may be drawn in that state.
        ///
        /// The previous behaviour was to fall back to futures points and draw anyway. That is
        /// worse than drawing nothing: a confident, wrong number on the axis is indis-
        /// tinguishable from a right one. Without the exact spread you do not trade
        /// (Stefano, 7/10/2026).
        /// </summary>
        private bool CashScaleWithoutSpread()
        {
            switch (DisplayScale())
            {
                case "SPX":
                case "SPY": return EsSpxSpread <= 0;
                case "NDX":
                case "QQQ": return NqNdxSpread <= 0;
                default:    return false;   // ES and NQ are the strikes' own scale
            }
        }

        private bool IsNqFamily()
        {
            return DataFamily() == "NQ";
        }

        private double ConvertPrice(double esPrice)
        {
            string t = DisplayScale().ToUpperInvariant();
            switch (t)
            {
                // No identity fallback: a cash scale without its spread returns NaN and the
                // caller draws nothing. CashScaleWithoutSpread() says why.
                case "SPX": return EsSpxSpread <= 0 ? double.NaN : esPrice - EsSpxSpread;
                case "SPY": return EsSpxSpread <= 0 ? double.NaN : (esPrice - EsSpxSpread) / 10.0;
                case "NQ":  return esPrice;  // raw NQ price
                case "NDX": return NqNdxSpread <= 0 ? double.NaN : esPrice - NqNdxSpread;
                case "QQQ": return NqNdxSpread <= 0 ? double.NaN : (esPrice - NqNdxSpread) / (_ndxQqqRatio > 0 ? _ndxQqqRatio : 40.0);
                default:    return esPrice;  // ES
            }
        }

        // Chart-display price back into futures space (the strikes' space)
        private double ToFuturesSpace(double displayPrice)
        {
            string t = DisplayScale().ToUpperInvariant();
            switch (t)
            {
                case "SPX": return EsSpxSpread <= 0 ? double.NaN : displayPrice + EsSpxSpread;
                case "SPY": return EsSpxSpread <= 0 ? double.NaN : displayPrice * 10.0 + EsSpxSpread;
                case "NDX": return NqNdxSpread <= 0 ? double.NaN : displayPrice + NqNdxSpread;
                case "QQQ": return NqNdxSpread <= 0 ? double.NaN : displayPrice * (_ndxQqqRatio > 0 ? _ndxQqqRatio : 40.0) + NqNdxSpread;
                default:    return displayPrice;
            }
        }

        // A CLOSE that crosses the level: previous close on one side, the close that just printed on
        // the other. Evaluated when the bar closes, so a wick through a wall does not fire.
        private void CheckLevelAlerts()
        {
            double c1 = Close[1], c2 = Close[2];
            foreach (var al in _alertLevels)
            {
                bool wanted = al.Kind == "wall" ? AlertWalls : al.Kind == "system" ? AlertSystem : AlertBos;
                if (!wanted) continue;
                bool up = c2 < al.Price && c1 >= al.Price;
                bool down = c2 > al.Price && c1 <= al.Price;
                if (!up && !down) continue;
                string msg = $"TLADe {al.Name} crossed {(up ? "UP" : "DOWN")}";
                Alert($"TLADeX_{al.Kind}_{al.Price.ToString("0.####", inv)}", Priority.High, msg,
                      NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav", 10, Brushes.Black, Brushes.Orange);
            }
        }

        private void ResetWallFlips()
        {
            _wallCounter.Clear(); _wallFlipped.Clear(); _wallType.Clear();
            foreach (var lvl in _levels)
            {
                if (lvl.Type != "CW" && lvl.Type != "PW") continue;
                if (_wallType.ContainsKey(lvl.EsStrike)) continue;
                _wallType[lvl.EsStrike] = lvl.Type;
                _wallCounter[lvl.EsStrike] = 0;
                _wallFlipped[lvl.EsStrike] = false;
            }
        }

        private void UpdateWallFlips(double close5mDisplay)
        {
            if (_wallType.Count == 0) return;
            double c = ToFuturesSpace(close5mDisplay);
            foreach (var strike in new List<double>(_wallType.Keys))
            {
                bool isF = _wallFlipped[strike];
                int cnt = _wallCounter[strike];
                bool beyond = _wallType[strike] == "CW" ? (isF ? c < strike : c > strike) : (isF ? c > strike : c < strike);
                if (beyond)
                {
                    cnt++;
                    if (cnt >= 2) { isF = !isF; cnt = 0; _flipDirty = true; }
                }
                else cnt = 0;
                _wallCounter[strike] = cnt;
                _wallFlipped[strike] = isF;
            }
        }

        private bool IsWallFlipped(double strike)
        {
            bool f; return _wallFlipped.TryGetValue(strike, out f) && f;
        }

        private DashStyleHelper UserLineStyle()
        {
            string st = (LineStyle ?? "").Trim();
            if (st.Equals("Solid", StringComparison.OrdinalIgnoreCase)) return DashStyleHelper.Solid;
            if (st.Equals("Dashed", StringComparison.OrdinalIgnoreCase)) return DashStyleHelper.Dash;
            return DashStyleHelper.Dot;
        }

        private string FormatPrice(double esPrice)
        {
            double converted = ConvertPrice(esPrice);
            string t = DisplayScale().ToUpperInvariant();
            if (t == "SPY" || t == "QQQ")
                return converted.ToString("0.##", inv);
            return Math.Round(converted).ToString("0", inv);
        }

        private Brush MakeBrush(Color c, byte alpha)
        {
            var bc = Color.FromArgb(alpha, c.R, c.G, c.B);
            var b = new SolidColorBrush(bc);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        private void UpdateThemeBrushes()
        {
            _posColor = (Color)ColorConverter.ConvertFromString("#22c55e");
            _negColor = (Color)ColorConverter.ConvertFromString("#ef4444");

            string theme = (Theme ?? "").Trim();

            if (theme.Equals("Boreal", StringComparison.OrdinalIgnoreCase))
            {
                _posColor = (Color)ColorConverter.ConvertFromString("#22d3ee");
                _negColor = (Color)ColorConverter.ConvertFromString("#f472b6");
            }
            else if (theme.Equals("Lady Trader", StringComparison.OrdinalIgnoreCase))
            {
                _posColor = (Color)ColorConverter.ConvertFromString("#2dd4bf");
                _negColor = (Color)ColorConverter.ConvertFromString("#c084fc");
            }

            var pb = new SolidColorBrush(_posColor);
            var nb = new SolidColorBrush(_negColor);
            if (pb.CanFreeze) pb.Freeze();
            if (nb.CanFreeze) nb.Freeze();
            _posBrush = pb;
            _negBrush = nb;

            // Profile bar brushes (independent of level brushes)
            string bcs = (BarColorStyle ?? "").Trim();
            if (bcs.Equals("Greyscale", StringComparison.OrdinalIgnoreCase))
            {
                _profCallBrush = new SolidColorBrush(Color.FromRgb(0xa0, 0xa0, 0xa0));
                _profPutBrush  = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
            }
            else if (bcs.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                _profCallBrush = new SolidColorBrush(CustomBarCallColor);
                _profPutBrush  = new SolidColorBrush(CustomBarPutColor);
            }
            else // Theme Colors (inverted: call=neg, put=pos)
            {
                _profCallBrush = _negBrush;
                _profPutBrush  = _posBrush;
            }
            if (((SolidColorBrush)_profCallBrush).CanFreeze) ((SolidColorBrush)_profCallBrush).Freeze();
            if (((SolidColorBrush)_profPutBrush).CanFreeze)  ((SolidColorBrush)_profPutBrush).Freeze();
        }

        // -------------------------
        // Inputs
        // -------------------------
        [Display(Name = "Auto-detect Ticker from chart symbol",
                 Description = "When on, MNQ/NQ/NDX/QQQ charts read NDX levels and MES/ES/SPX/SPY charts read SPX levels — regardless of the Display Ticker setting below. Uncheck to force the Display Ticker manually (e.g. plot SPX levels on an ES chart).",
                 GroupName = "Ticker Settings", Order = -1)]
        public bool AutoDetectTicker { get; set; }

        [Display(Name = "Display Ticker (ES/SPX/SPY/NQ/NDX/QQQ)",
                 Description = "Manual family selector. Only honoured when Auto-detect is off.",
                 GroupName = "Ticker Settings", Order = 0)]
        public string DisplayTicker { get; set; }

        [Display(Name = "ES-SPX Spread (0 = from data string S:)", GroupName = "Ticker Settings", Order = 1)]
        public double EsSpxSpread { get; set; }

        [Display(Name = "NQ-NDX Spread (0 = from data string S:)", GroupName = "Ticker Settings", Order = 2)]
        public double NqNdxSpread { get; set; }

        [Display(Name = "GEX Data (paste from TLADe)", GroupName = "General Settings", Order = 0)]
        public string GexDataInput { get; set; }

        [Display(Name = "Show GEX Levels (CW/PW/GL)", GroupName = "Level Visibility", Order = 0)]
        public bool ShowGexLevels { get; set; }

        [Display(Name = "Show System Levels (ZG/MP/EH/EL/VH/VL)", GroupName = "Level Visibility", Order = 1)]
        public bool ShowSystemLevels { get; set; }

        [Display(Name = "Show Structure Levels (PDH/PDL/PWH/PWL)", GroupName = "Level Visibility", Order = 2)]
        public bool ShowStructureLevels { get; set; }

        [Display(Name = "Show Charm Magnet (CM)", GroupName = "Level Visibility", Order = 3)]
        public bool ShowCharmMagnet { get; set; }

        [Display(Name = "Show Delta Flip (DF)", GroupName = "Level Visibility", Order = 4)]
        public bool ShowDeltaFlip { get; set; }

        [Display(Name = "Show Zero Gamma line (even when System off)", GroupName = "Level Visibility", Order = 8)]
        public bool ShowZeroGamma { get; set; }

        [Range(1, 999)]
        [Display(Name = "Max GEX Levels (999=All)", GroupName = "Level Visibility", Order = 3)]
        public int MaxGexLevels { get; set; }

        [Display(Name = "Show only levels near price", GroupName = "Level Visibility", Order = 4)]
        public bool ShowOnlyNear { get; set; }

        [Range(0.1, 25.0)]
        [Display(Name = "  ↳ Radius (%)", GroupName = "Level Visibility", Order = 5)]
        public double NearPct { get; set; }

        [Display(Name = "Enable GEX Threshold Filter", GroupName = "Level Visibility", Order = 6)]
        public bool EnableThreshold { get; set; }

        [Range(0.0, 5000.0)]
        [Display(Name = "  ↳ Min GEX Magnitude (M)", GroupName = "Level Visibility", Order = 7)]
        public double GexThreshold { get; set; }

        [Display(Name = "Theme (Wall Street Classic/Boreal/Lady Trader)", GroupName = "Colors", Order = 0)]
        public string Theme { get; set; }

        [Display(Name = "Bar Color Style (Theme Colors/Greyscale/Custom)", GroupName = "Colors", Order = 1)]
        public string BarColorStyle { get; set; }

        [XmlIgnore]
        [Display(Name = "  ↳ Custom Call Bar Color", GroupName = "Colors", Order = 2)]
        public System.Windows.Media.Color CustomBarCallColor { get; set; }

        [XmlIgnore]
        [Display(Name = "  ↳ Custom Put Bar Color", GroupName = "Colors", Order = 3)]
        public System.Windows.Media.Color CustomBarPutColor { get; set; }

        [Range(0, 500)]
        [Display(Name = "Right Offset Bars (base)", GroupName = "Layout", Order = 0)]
        public int RightOffsetBars { get; set; }

        [Range(0, 500)]
        [Display(Name = "Label Padding Bars (move left)", GroupName = "Layout", Order = 1)]
        public int LabelPaddingBars { get; set; }

        [Range(0, 500)]
        [Display(Name = "Profile Extra Offset Bars (move right)", GroupName = "Layout", Order = 2)]
        public int ProfileOffsetBars { get; set; }

        [Display(Name = "Show Labels", GroupName = "Price Levels Style", Order = 0)]
        public bool ShowLabels { get; set; }

        [Range(6, 50)]
        [Display(Name = "Label Font Size", GroupName = "Price Levels Style", Order = 1)]
        public int LabelFontSize { get; set; }

        [Range(0, 5000)]
        [Display(Name = "Line Left Bars (from label)", GroupName = "Price Levels Style", Order = 2)]
        public int LineLeftBars { get; set; }

        [Range(0, 2000)]
        [Display(Name = "Line Right Bars (from label)", GroupName = "Price Levels Style", Order = 3)]
        public int LineRightBars { get; set; }

        [Display(Name = "Line Style (Solid/Dashed/Dotted)", GroupName = "Price Levels Style", Order = 3)]
        public string LineStyle { get; set; }

        [Range(1, 10)]
        [Display(Name = "Level Line Width (GEX/system)", GroupName = "Price Levels Style", Order = 4)]
        public int LevelLineWidth { get; set; }

        [Display(Name = "Show Profile Bars (P:)", GroupName = "Profile", Order = 0)]
        public bool ShowProfileBars { get; set; }

        [Display(Name = "Profile Bars All To Right (puts too)", GroupName = "Profile", Order = 1)]
        public bool ProfileAllToRight { get; set; }

        [Range(1, 500)]
        [Display(Name = "Profile Width (bars)", GroupName = "Profile", Order = 2)]
        public int ProfileWidthBars { get; set; }

        [Range(1, 50)]
        [Display(Name = "Profile Bar Height (ticks)", GroupName = "Profile", Order = 3)]
        public int ProfileBarHeightTicks { get; set; }

        [Range(0.1, 1000.0)]
        [Display(Name = "Profile Scale Max", GroupName = "Profile", Order = 4)]
        public double ProfileScaleMax { get; set; }

        [Display(Name = "Auto Scale Profile Max", GroupName = "Profile", Order = 5)]
        public bool AutoScaleProfileMax { get; set; }

        [Range(10, 5000)]
        [Display(Name = "Max Profile Rows", GroupName = "Profile", Order = 6)]
        public int MaxProfileRows { get; set; }

        [Display(Name = "Auto-Fetch from TLADe API", GroupName = "General Settings", Order = 1)]
        public bool AutoFetchEnabled { get; set; }

        [Display(Name = "API Key (from TLADe Terminal)", GroupName = "General Settings", Order = 2)]
        public string ApiKey { get; set; }

        [Display(Name = "Show Status Text", GroupName = "Developer", Order = 0)]
        public bool ShowStatusText { get; set; }

        // ── v1.1: Breakout / Session AVWAP / Session Boxes ──

        [Display(Name = "Show Breakout Structure",
                 Description = "Breakouts computed from the chart's own bars, on closed bars of each timeframe: a close beyond the previous bar's extreme with the body past the level larger than the shadow. A breakout dies when a later bar of the same timeframe, in the opposite direction, closes back through it.",
                 GroupName = "Breakout Structure", Order = 0)]
        public bool ShowBreakoutLevels { get; set; }

        [Display(Name = "Monthly", GroupName = "Breakout Structure", Order = 1)]
        public bool ShowBosM { get; set; }
        [Display(Name = "Weekly", GroupName = "Breakout Structure", Order = 2)]
        public bool ShowBosW { get; set; }
        [Display(Name = "Daily", GroupName = "Breakout Structure", Order = 3)]
        public bool ShowBosD { get; set; }
        [Display(Name = "4H", GroupName = "Breakout Structure", Order = 4)]
        public bool ShowBosH4 { get; set; }
        [Display(Name = "1H", GroupName = "Breakout Structure", Order = 5)]
        public bool ShowBosH1 { get; set; }

        [Display(Name = "Draw on timeframes above 1H", Description = "Off: on 4H, Daily, Weekly and Monthly charts the indicator stays silent.", GroupName = "Ticker Settings", Order = 9)]
        public bool ShowAboveH1 { get; set; }

        [Display(Name = "Enable Level Cross Alerts", GroupName = "Alerts", Order = 0)]
        public bool EnableAlerts { get; set; }
        [Display(Name = "Call/Put Walls", GroupName = "Alerts", Order = 1)]
        public bool AlertWalls { get; set; }
        [Display(Name = "ZG / Max Pain / EM / Vol Bands", GroupName = "Alerts", Order = 2)]
        public bool AlertSystem { get; set; }
        [Display(Name = "Breakouts", GroupName = "Alerts", Order = 3)]
        public bool AlertBos { get; set; }

        [Display(Name = "Show Confluence Zones", Description = "Boxes where several levels sit within a band sized on the EM range.", GroupName = "Confluence Zones", Order = 0)]
        public bool ShowConfluence { get; set; }

        [Range(2, 5)]
        [Display(Name = "Min cluster size for box", GroupName = "Confluence Zones", Order = 1)]
        public int ConfluenceMinSize { get; set; }

        [Range(1.0, 30.0)]
        [Display(Name = "Band Width (% of EM)", Description = "Total band as a percentage of the EM range (EM High − EM Low), split half above and half below (7% = ±3.5%).", GroupName = "Confluence Zones", Order = 2)]
        public double ConfluenceEmPct { get; set; }

        [Display(Name = "Show AVWAP labels", GroupName = "Session AVWAP", Order = 6)]
        public bool ShowAvwapLabels { get; set; }

        [Range(1, 10)]
        [Display(Name = "Keep last N per timeframe", Description = "How many live breakouts to keep for each timeframe; the oldest goes when a new one forms.", GroupName = "Breakout Structure", Order = 6)]
        public int BosKeepPerTf { get; set; }

        [Display(Name = "Show Session AVWAP — Asia", GroupName = "Session AVWAP", Order = 0)]
        public bool ShowAvwapAsia { get; set; }

        [Display(Name = "Show Session AVWAP — EU", GroupName = "Session AVWAP", Order = 1)]
        public bool ShowAvwapEU { get; set; }

        [Display(Name = "Show Session AVWAP — US", GroupName = "Session AVWAP", Order = 2)]
        public bool ShowAvwapUS { get; set; }

        [Display(Name = "Show Session AVWAP — Prev Day US", GroupName = "Session AVWAP", Order = 3)]
        public bool ShowAvwapPD { get; set; }

        [Display(Name = "Show Historical AVWAP (beyond 30h)", GroupName = "Session AVWAP", Order = 4)]
        public bool ShowHistoricalAvwap { get; set; }

        [Range(1, 8)]
        [Display(Name = "AVWAP Line Width", GroupName = "Session AVWAP", Order = 5)]
        public int AvwapLineWidth { get; set; }

        [Display(Name = "Show Session Boxes", GroupName = "Session Boxes", Order = 0)]
        public bool ShowSessionBoxes { get; set; }

        [Display(Name = "  ↳ Asia (18:00–03:00 ET)", GroupName = "Session Boxes", Order = 1)]
        public bool ShowBoxAsia { get; set; }

        [Display(Name = "  ↳ EU (03:00–08:00 ET)", GroupName = "Session Boxes", Order = 2)]
        public bool ShowBoxEU { get; set; }

        [Display(Name = "  ↳ Pre (08:00–09:30 ET)", GroupName = "Session Boxes", Order = 3)]
        public bool ShowBoxPre { get; set; }

        [Display(Name = "  ↳ US (09:30–16:00 ET)", GroupName = "Session Boxes", Order = 4)]
        public bool ShowBoxUS { get; set; }

        [Display(Name = "Show Historical Sessions (beyond 30h)", GroupName = "Session Boxes", Order = 5)]
        public bool ShowHistoricalSessions { get; set; }
    }
}


