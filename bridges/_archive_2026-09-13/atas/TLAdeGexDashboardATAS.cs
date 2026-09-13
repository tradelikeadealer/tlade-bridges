// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Dashboard contribution
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe GEX Dashboard — ATAS Edition  v3.1.0
//  ─────────────────────────────────────────────────────────────────────────────
//  Mihai's v3.0.0 with two new features (n8n webhook POSTs on every update,
//  GEX direction marker showing positive/negative gamma per wall), with a
//  TLADe-side fix on top: strikes for ES/NQ are passed through raw because
//  the cloud already publishes them in futures space — subtracting the
//  ES↔SPX spread again left the levels ~19 points off vs the TLADe terminal.
//  SPX/NDX/SPY/QQQ paths unchanged.
//
//  v3.0.0 (Mihai): REAL OnRender — v2.0 dynamic LineSeries didn't render in
//          ATAS. Pattern: EnableCustomDrawing + SubscribeToDrawingEvents in
//          CTOR, then OnRender(RenderContext, DrawingLayouts).
//
//  ARCHITECTURE:
//    1. Auto-fetch from TLADe Cloud Functions (X-API-Key, mode=free fallback).
//    2. 6 fetch windows ET/day.
//    3. Manual paste fallback (GexDataInput).
//    4. Parse S:|L:|P: string -> _levels.
//    5. OnRender draws horizontal lines + labels + axis markers.
//    6. n8n webhook (optional): POST levels JSON on every data update.
//    7. GEX direction marker (optional): small line next to each label —
//       right = positive GEX, left = negative GEX.
//
//  AUTO-DETECT TICKER:
//    If DisplayTicker="ES" but the chart is NQ/MNQ → auto-fetch NDX.
//
//  LEVEL TYPES:
//    CW=Call Wall  PW=Put Wall  GL=GEX Level
//    ZG=Zero Gamma  MP=Max Pain  EH=EM High  EL=EM Low
//    VH=Vol Band Hi  VL=Vol Band Lo
//    PDH/PDL/PWH/PWL=Structure
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;
using DColor = System.Drawing.Color;
using DRect = System.Drawing.Rectangle;
using StringAlignment = System.Drawing.StringAlignment;

namespace ATAS.Indicators.Technical
{
    [DisplayName("TLADe GEX Dashboard")]
    [Description("GEX Walls + System Levels (auto-fetch from TLADe Cloud, native OnRender)")]
    public class TLAdeGexDashboardATAS : Indicator
    {
        // ──────────────────────────────────────────────────────────────────────
        //  STATE
        // ──────────────────────────────────────────────────────────────────────

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private const string API_URL =
            "https://europe-west1-omggex.cloudfunctions.net/indicatorData";
        // Slot ET — minuti dalla mezzanotte ET, ORDINATO ASCENDENTE.
        // L'ordering è critico per TimeUntilNextSlot (v3.1 scheduler), che
        // itera e breakja sul primo slot > nowMins. Se non ordinato, salta
        // tutti gli slot dello stesso giorno e va al successivo "futures day".
        // 125=02:05 EU, 485=08:05 PRE, 575=09:35 RTH, 635=10:35 OR, 785=13:05 CLOSE, 1085=18:05 ASIA.
        private static readonly int[] FETCH_MINUTES_ET = { 125, 485, 575, 635, 785, 1085 };

        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS", "tlade_gex.log");

        private readonly CultureInfo _inv = CultureInfo.InvariantCulture;
        private readonly object _lvlLock = new object();
        private readonly List<LevelEntry> _levels = new List<LevelEntry>();

        private string   _rawData            = "";
        private bool     _isDelayedMode      = false;
        private int      _lastFetchMinuteEt  = -1;
        private DateTime _lastFetchUtc       = DateTime.MinValue;
        private bool     _initialFetchTried  = false;
        // v3.1 — Scheduler indipendente dai tick. Six absolute ET slots/day
        // + 1 fetch al mount = 7 chiamate/giorno deterministiche. Sostituisce
        // il legacy tick-driven path che falliva silenziosamente nelle finestre
        // pre-RTH quando il chart non riceveva tick (cash index, futures
        // quiet hours, weekend). See TryScheduledFetch — kept as dead code.
        private CancellationTokenSource _schedulerCts;

        // v3.1.1 — Session AVWAP local compute (= mirrors the Pine indicator).
        // 4 polylines (Asia / EU / US / Prev Day) computed from hlc3 * volume
        // cumulative, reset at each session anchor in ET. No round-trip
        // through the cloud — same data the terminal uses for AVWAP polylines.
        private const decimal AVWAP_NA = decimal.MinValue;
        private decimal[] _avwapAsia;
        private decimal[] _avwapEU;
        private decimal[] _avwapUS;
        private decimal[] _avwapPD;
        private decimal _aSumPV, _aSumV;
        private decimal _eSumPV, _eSumV;
        private decimal _uSumPV, _uSumV;
        private decimal _pdSumPV, _pdSumV;
        // Snapshot of accumulators taken just BEFORE the current bar is processed
        // for the first time. Used to roll back when ATAS re-invokes OnCalculate
        // on the same bar tick-by-tick (live update) — without this every tick
        // would double-count hlc3*vol on the current bar.
        private decimal _snapASumPV, _snapASumV;
        private decimal _snapESumPV, _snapESumV;
        private decimal _snapUSumPV, _snapUSumV;
        private decimal _snapPDSumPV, _snapPDSumV;
        private int _avwapLastComputedBar = -1;

        // v3.2 — local multi-TF BOS (W/D/H4/H1), computed from the chart's own
        // bars (1:1 port of terminal clientEngineService.computeAllBOS). PA moved
        // client-side on the TLADe cloud, so technicalLevels (BOdl/BOds) are no
        // longer in the published snapshot; we reproduce the Break-of-Structure
        // here from native chart candles instead of relying on the feed.
        private readonly List<LevelEntry> _localBos = new List<LevelEntry>();
        private readonly List<LevelEntry> _localPA = new List<LevelEntry>();
        private int _paLastComputedBars = -1;
        private int _bosLastComputedBars = -1;
        private DateTime _lastAsiaAnchor = DateTime.MinValue;
        private DateTime _lastEUAnchor   = DateTime.MinValue;
        private DateTime _lastUSAnchor   = DateTime.MinValue;

        private RenderFont _fn;
        private readonly RenderStringFormat _sfL =
            new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

        // ──────────────────────────────────────────────────────────────────────
        //  PROPERTIES
        // ──────────────────────────────────────────────────────────────────────

        [Display(Name = "API Key (empty = FREE mode)", GroupName = "1. Source", Order = 1)]
        public string ApiKey { get; set; } = "";

        [Display(Name = "Auto-fetch from TLADe Cloud", GroupName = "1. Source", Order = 2)]
        public bool AutoFetch { get; set; } = true;

        [Display(Name = "Manual data string (override)", GroupName = "1. Source", Order = 3,
                 Description = "Paste S:|L:|P: from TLADe Terminal. Leave empty for auto-fetch.")]
        public string GexDataInput { get; set; } = "";

        [Display(Name = "Ticker display (auto/ES/SPX/SPY/NQ/NDX/QQQ)", GroupName = "2. Layout", Order = 1,
                 Description = "auto = detect from the chart instrument")]
        public string DisplayTicker { get; set; } = "auto";

        [Display(Name = "ES↔SPX spread (auto from S:)", GroupName = "2. Layout", Order = 2,
                 Description = "Read automatically from the cloud S: header. Can be overridden with Manual spread.")]
        [Range(0, 200)]
        public double EsSpxSpread { get; set; } = 24.0;

        [Display(Name = "NQ↔NDX spread", GroupName = "2. Layout", Order = 3)]
        [Range(0, 200)]
        public double NqNdxSpread { get; set; } = 40.0;

        [Display(Name = "Manual spread override (0 = auto)", GroupName = "2. Layout", Order = 4,
                 Description = "If > 0, forces the ES↔SPX spread to this value instead of S: from cloud. Useful to match TLADe Terminal.")]
        [Range(0, 200)]
        public double ManualSpreadOverride { get; set; } = 0.0;

        [Display(Name = "Snap to instrument tick", GroupName = "2. Layout", Order = 5,
                 Description = "Rounds prices to the nearest tick (0.25 on ES). Line + label land exactly on the ATAS grid.")]
        public bool SnapToTick { get; set; } = true;

        [Display(Name = "Show GEX walls (CW/PW)", GroupName = "3. Visibility", Order = 1)]
        public bool ShowGexLevels { get; set; } = true;

        [Display(Name = "Show System (ZG/MP/EM/VB)", GroupName = "3. Visibility", Order = 2)]
        public bool ShowSystemLevels { get; set; } = true;

        [Display(Name = "Show Structure (PDH/PDL/PWH/PWL)", GroupName = "3. Visibility", Order = 3)]
        public bool ShowStructureLevels { get; set; } = true;

        [Display(Name = "Show Breakout Areas (BL/BS)", GroupName = "3. Visibility", Order = 7)]
        public bool ShowBreakoutLevels { get; set; } = true;

        [Display(Name = "Show Charm Magnet (CM)", GroupName = "3. Visibility", Order = 8)]
        public bool ShowCharmMagnet { get; set; } = true;

        // VP (POC/VAH/VAL) is intentionally NOT in the TLADe payload: ATAS
        // already ships a built-in VolumeProfile study that computes it
        // directly from the chart's own bars. Stack it alongside the TLADe
        // Dashboard for the same view the terminal renders.
        // Session AVWAP is computed locally (= same as Pine), see group 5.

        [Display(Name = "Show Session AVWAP — Asia", GroupName = "5. Session AVWAP", Order = 1)]
        public bool ShowAvwapAsia { get; set; } = true;

        [Display(Name = "Show Session AVWAP — EU", GroupName = "5. Session AVWAP", Order = 2)]
        public bool ShowAvwapEU { get; set; } = true;

        [Display(Name = "Show Session AVWAP — US", GroupName = "5. Session AVWAP", Order = 3)]
        public bool ShowAvwapUS { get; set; } = true;

        [Display(Name = "Show Session AVWAP — Prev Day US", GroupName = "5. Session AVWAP", Order = 4)]
        public bool ShowAvwapPD { get; set; } = true;

        [Display(Name = "Show Historical AVWAP (prev days)", GroupName = "5. Session AVWAP", Order = 5,
                 Description = "When off, only the current trading day is rendered.")]
        public bool ShowHistoricalAvwap { get; set; } = false;

        [Display(Name = "AVWAP line width", GroupName = "5. Session AVWAP", Order = 6)]
        [Range(1, 4)]
        public int AvwapLineWidth { get; set; } = 2;

        [Display(Name = "Show AVWAP labels", GroupName = "5. Session AVWAP", Order = 7)]
        public bool ShowAvwapLabels { get; set; } = true;

        [Display(Name = "Show labels", GroupName = "3. Visibility", Order = 4)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Show price-axis markers", GroupName = "3. Visibility", Order = 5)]
        public bool ShowAxisMarkers { get; set; } = true;

        [Display(Name = "Show status badge (mode/count)", GroupName = "3. Visibility", Order = 6)]
        public bool ShowStatus { get; set; } = true;

        [Display(Name = "Max GEX walls (5..20, 999=All)", GroupName = "4. Filter", Order = 1)]
        [Range(2, 999)]
        public int MaxGexLevels { get; set; } = 10;

        [Display(Name = "Enable threshold filter", GroupName = "4. Filter", Order = 2)]
        public bool EnableThreshold { get; set; } = false;

        [Display(Name = "Threshold magnitude (M)", GroupName = "4. Filter", Order = 3)]
        public double GexThreshold { get; set; } = 50.0;

        [Display(Name = "Line width GEX", GroupName = "5. Style", Order = 1)]
        [Range(1, 5)]
        public int LineWidthGex { get; set; } = 1;

        [Display(Name = "Line width System/ZG/MP", GroupName = "5. Style", Order = 2)]
        [Range(1, 5)]
        public int LineWidthSystem { get; set; } = 2;

        [Display(Name = "Font size label", GroupName = "5. Style", Order = 3)]
        [Range(7, 16)]
        public int LabelFontSize { get; set; } = 9;

        [Display(Name = "Label text inherits line color", GroupName = "5. Style", Order = 7,
                 Description = "Default ON. Uncheck to force a custom text color (useful if red/green is not visible on your background).")]
        public bool LabelInheritLineColor { get; set; } = true;

        [Display(Name = "Label text color (when not inheriting)", GroupName = "5. Style", Order = 8)]
        public Color LabelTextColor { get; set; } = Colors.White;

        [Display(Name = "Label background opacity (0-255)", GroupName = "5. Style", Order = 9)]
        [Range(0, 255)]
        public int LabelBgAlpha { get; set; } = 140;

        [Display(Name = "Label background differs above/below spot", GroupName = "5. Style", Order = 10,
                 Description = "Default OFF (background derived from line color). When ON, levels above spot use the Above color, below spot use Below.")]
        public bool LabelBgPositional { get; set; } = false;

        [Display(Name = "Label background - above spot", GroupName = "5. Style", Order = 11)]
        public Color LabelBgAbove { get; set; } = Color.FromRgb(0xdc, 0x26, 0x26); // brighter red

        [Display(Name = "Label background - below spot", GroupName = "5. Style", Order = 12)]
        public Color LabelBgBelow { get; set; } = Color.FromRgb(0x16, 0xa3, 0x4a); // brighter green

        [Display(Name = "Scale wall lines by magnitude", GroupName = "5. Style", Order = 4,
                 Description = "CW/PW: line length is proportional to magnitude. System levels (ZG/MP/EM/VB/structure) stay full width.")]
        public bool ScaleWallsByMag { get; set; } = true;

        [Display(Name = "Wall line max width (% chart)", GroupName = "5. Style", Order = 5)]
        [Range(10, 100)]
        public int WallMaxPct { get; set; } = 60;

        [Display(Name = "Wall line min width (% chart)", GroupName = "5. Style", Order = 6)]
        [Range(1, 50)]
        public int WallMinPct { get; set; } = 5;

        [Display(Name = "Send to n8n", GroupName = "6. n8n Webhook", Order = 1,
                 Description = "Check to POST the levels to the n8n webhook on every data update.")]
        public bool N8nEnabled { get; set; } = false;

        [Display(Name = "n8n Webhook URL", GroupName = "6. n8n Webhook", Order = 2,
                 Description = "Paste the n8n webhook URL (e.g. https://host/webhook/atas-gex). Data is sent as JSON in the body.")]
        public string N8nWebhookUrl { get; set; } = "";

        [Display(Name = "Show GEX direction (left/right)", GroupName = "7. GEX Direction", Order = 1,
                 Description = "Draws a small line next to each level's label box: right = positive GEX, left = negative GEX (from the cloud P: section).")]
        public bool ShowGexDirection { get; set; } = true;

        [Display(Name = "Length proportional to wall strength", GroupName = "7. GEX Direction", Order = 2,
                 Description = "When ON, line length is proportional to wall magnitude (strong gamma = long, weak = short). When OFF, all use the maximum length.")]
        public bool GexScaleByStrength { get; set; } = true;

        [Display(Name = "GEX line min length (px)", GroupName = "7. GEX Direction", Order = 3)]
        [Range(1, 200)]
        public int GexTickLengthMin { get; set; } = 4;

        [Display(Name = "GEX line max length (px)", GroupName = "7. GEX Direction", Order = 4)]
        [Range(2, 400)]
        public int GexTickLength { get; set; } = 40;

        [Display(Name = "GEX line thickness", GroupName = "7. GEX Direction", Order = 5)]
        [Range(1, 20)]
        public int GexTickWidth { get; set; } = 3;

        // ──────────────────────────────────────────────────────────────────────
        //  CONSTRUCTOR
        // ──────────────────────────────────────────────────────────────────────

        public TLAdeGexDashboardATAS() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            SubscribeToDrawingEvents(DrawingLayouts.Final);
            Panel = IndicatorDataProvider.CandlesPanel;
            DataSeries[0].IsHidden = true;
            TryLoadDefaultApiKey();
            Log($"CTOR v3.0 instance hash={GetHashCode()} apiKeySet={!string.IsNullOrEmpty(ApiKey)}");
        }

        private void TryLoadDefaultApiKey()
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                 "ATAS", "tlade_apikey.txt"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "ATAS", "tlade_apikey.txt"),
                };
                foreach (var p in candidates)
                {
                    if (!File.Exists(p)) continue;
                    var k = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrEmpty(k)) { ApiKey = k; return; }
                }
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  ONCALCULATE — drives fetch only; rendering is OnRender
        // ──────────────────────────────────────────────────────────────────────

        private int _ocCalls = 0;
        protected override void OnCalculate(int bar, decimal value)
        {
            try
            {
                // v3.1.1 — Session AVWAP: compute on every bar (historical + live).
                // Must run before the `bar != CurrentBar - 1` short-circuit
                // below: that gate is for the cloud fetch path, but AVWAP
                // needs every historical bar to populate its polylines.
                ComputeAvwapForBar(bar);

                _ocCalls++;
                if (bar != CurrentBar - 1) return;

                if (_ocCalls <= 3 || _ocCalls % 2000 == 0)
                    Log($"OnCalc #{_ocCalls} (live) bar={bar} CurrentBar={CurrentBar} initialFetched={_initialFetchTried}");

                if (!string.IsNullOrEmpty(GexDataInput))
                {
                    if (GexDataInput != _rawData)
                    {
                        _rawData = GexDataInput;
                        ParseData(_rawData);
                        RedrawChart();
                        Task.Run(() => PushToN8n());
                    }
                    return;
                }

                // Fetch lifecycle is now owned by the absolute-time scheduler
                // (OnInitialize → RunScheduler). OnCalculate only handles the
                // manual-paste override path above.
            }
            catch (Exception ex)
            {
                Log($"OnCalculate EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  LIFECYCLE — absolute-time scheduler (v3.1)
        // ──────────────────────────────────────────────────────────────────────

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Log($"OnInitialize: starting scheduler (autoFetch={AutoFetch})");
            if (!AutoFetch) return;
            _schedulerCts?.Cancel();
            _schedulerCts = new CancellationTokenSource();
            // 1 immediate fetch at mount + 6 scheduled fetches/day at absolute
            // ET slots. Independent from OnCalculate tick rate — fires even
            // when the chart receives zero ticks (cash index out of RTH,
            // futures quiet hours, weekend).
            _ = RunScheduler(_schedulerCts.Token);
        }

        protected override void OnDispose()
        {
            try { _schedulerCts?.Cancel(); } catch { }
            _schedulerCts = null;
            Log("OnDispose: scheduler cancelled");
            base.OnDispose();
        }

        private async Task RunScheduler(CancellationToken ct)
        {
            try
            {
                // Mount fetch first.
                Log("Scheduler: mount fetch");
                await FetchOnce().ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    var delay = TimeUntilNextSlot(GetEtTime());
                    Log($"Scheduler: sleeping {delay.TotalMinutes:F1}min until next ET slot");
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                    if (ct.IsCancellationRequested) return;
                    if (!AutoFetch) { Log("Scheduler: AutoFetch off — pausing 5min"); await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); continue; }
                    Log("Scheduler: scheduled fetch");
                    await FetchOnce().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log($"Scheduler EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Minutes until the next ET fetch slot. If we're past the last slot of
        // the day, wraps to the first slot of the next day. Adds a 1s buffer so
        // we land *just after* the slot boundary and the cloud has it ready.
        private TimeSpan TimeUntilNextSlot(DateTime nowEt)
        {
            int nowMins = nowEt.Hour * 60 + nowEt.Minute;
            int? next = null;
            foreach (var slot in FETCH_MINUTES_ET)
            {
                if (slot > nowMins) { next = slot; break; }
            }
            int diffMins;
            if (next.HasValue)
            {
                diffMins = next.Value - nowMins;
            }
            else
            {
                // No more slots today — wrap to first slot of next day.
                diffMins = (1440 - nowMins) + FETCH_MINUTES_ET[0];
            }
            return TimeSpan.FromMinutes(diffMins)
                 - TimeSpan.FromSeconds(nowEt.Second)
                 + TimeSpan.FromSeconds(1);
        }

        // ──────────────────────────────────────────────────────────────────────
        //  SESSION AVWAP — local compute (v3.1.1)
        //  Mirrors the Pine indicator. 4 polylines (Asia / EU / US / Prev Day)
        //  computed from hlc3 * volume cumulative, reset at each session anchor
        //  in ET. Asia anchor at 18:00 ET = futures day start; on each new Asia
        //  session the previous US AVWAP is "promoted" to PD (= Pine behaviour).
        // ──────────────────────────────────────────────────────────────────────

        private void EnsureAvwapArrays(int barCount)
        {
            if (_avwapAsia != null && _avwapAsia.Length >= barCount) return;
            int newSize = Math.Max(barCount, 1024);
            var na = (int idx) => AVWAP_NA;
            // Initialize all slots to AVWAP_NA so the renderer can break the
            // polyline at gaps cleanly (no spurious lines from zero).
            var aA = new decimal[newSize];
            var aE = new decimal[newSize];
            var aU = new decimal[newSize];
            var aP = new decimal[newSize];
            for (int i = 0; i < newSize; i++) { aA[i] = aE[i] = aU[i] = aP[i] = AVWAP_NA; }
            // Copy over existing values if the array is being grown.
            if (_avwapAsia != null)
            {
                int copyLen = Math.Min(_avwapAsia.Length, newSize);
                Array.Copy(_avwapAsia, aA, copyLen);
                Array.Copy(_avwapEU,   aE, copyLen);
                Array.Copy(_avwapUS,   aU, copyLen);
                Array.Copy(_avwapPD,   aP, copyLen);
            }
            _avwapAsia = aA; _avwapEU = aE; _avwapUS = aU; _avwapPD = aP;
        }

        private void ComputeAvwapForBar(int bar)
        {
            EnsureAvwapArrays(bar + 1);
            var candle = GetCandle(bar);
            if (candle == null) return;

            // ATAS calls OnCalculate(bar=N) once per bar at startup, then
            // re-calls OnCalculate(bar=N) on every live tick of the current
            // bar. Without rollback, the second+ tick on bar N would
            // accumulate hlc3*vol a second time.
            if (bar > _avwapLastComputedBar)
            {
                // First time on this bar — snapshot the accumulators BEFORE
                // processing it. Next live-tick call restores from snapshot.
                _snapASumPV = _aSumPV; _snapASumV = _aSumV;
                _snapESumPV = _eSumPV; _snapESumV = _eSumV;
                _snapUSumPV = _uSumPV; _snapUSumV = _uSumV;
                _snapPDSumPV = _pdSumPV; _snapPDSumV = _pdSumV;
                _avwapLastComputedBar = bar;
            }
            else if (bar == _avwapLastComputedBar)
            {
                // Live-tick re-entry — restore snapshot so we can re-accumulate
                // exactly once.
                _aSumPV = _snapASumPV; _aSumV = _snapASumV;
                _eSumPV = _snapESumPV; _eSumV = _snapESumV;
                _uSumPV = _snapUSumPV; _uSumV = _snapUSumV;
                _pdSumPV = _snapPDSumPV; _pdSumV = _snapPDSumV;
            }
            else
            {
                // bar < lastComputed — out-of-order replay, skip (rare).
                return;
            }

            var et = ToEt(candle.Time);
            var asiaAnchor = AsiaAnchorFor(et);
            var euAnchor   = EUAnchorFor(et);
            var usAnchor   = USAnchorFor(et);

            // New Asia session = futures day rollover. Promote US → PD before reset.
            if (asiaAnchor != _lastAsiaAnchor)
            {
                _pdSumPV = _uSumPV;
                _pdSumV  = _uSumV;
                _aSumPV  = 0;
                _aSumV   = 0;
                _lastAsiaAnchor = asiaAnchor;
            }
            if (euAnchor != _lastEUAnchor) { _eSumPV = 0; _eSumV = 0; _lastEUAnchor = euAnchor; }
            if (usAnchor != _lastUSAnchor) { _uSumPV = 0; _uSumV = 0; _lastUSAnchor = usAnchor; }

            decimal hlc3 = (candle.High + candle.Low + candle.Close) / 3m;
            decimal vol  = candle.Volume;

            // Accumulate only if the bar is past the session anchor (= guards
            // against an Asia-anchor slipping a few seconds after the bar
            // close, which would otherwise count the bar twice).
            if (et >= asiaAnchor) { _aSumPV += hlc3 * vol; _aSumV += vol; }
            if (et >= euAnchor)   { _eSumPV += hlc3 * vol; _eSumV += vol; }
            if (et >= usAnchor)   { _uSumPV += hlc3 * vol; _uSumV += vol; }

            _avwapAsia[bar] = _aSumV > 0 ? _aSumPV / _aSumV : AVWAP_NA;
            _avwapEU[bar]   = _eSumV > 0 ? _eSumPV / _eSumV : AVWAP_NA;
            _avwapUS[bar]   = _uSumV > 0 ? _uSumPV / _uSumV : AVWAP_NA;
            _avwapPD[bar]   = _pdSumV > 0 ? _pdSumPV / _pdSumV : AVWAP_NA;
        }

        private static DateTime AsiaAnchorFor(DateTime et)
        {
            // 18:00 ET — futures day start
            var today18 = new DateTime(et.Year, et.Month, et.Day, 18, 0, 0);
            return et >= today18 ? today18 : today18.AddDays(-1);
        }
        private static DateTime EUAnchorFor(DateTime et)
        {
            // 02:00 ET — Europe open
            var today02 = new DateTime(et.Year, et.Month, et.Day, 2, 0, 0);
            return et >= today02 ? today02 : today02.AddDays(-1);
        }
        private static DateTime USAnchorFor(DateTime et)
        {
            // 09:30 ET — RTH open
            var today0930 = new DateTime(et.Year, et.Month, et.Day, 9, 30, 0);
            return et >= today0930 ? today0930 : today0930.AddDays(-1);
        }

        private static DateTime ToEt(DateTime barTime)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var utc = barTime.Kind == DateTimeKind.Utc ? barTime : DateTime.SpecifyKind(barTime, DateTimeKind.Utc);
                return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            }
            catch
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    var utc = barTime.Kind == DateTimeKind.Utc ? barTime : DateTime.SpecifyKind(barTime, DateTimeKind.Utc);
                    return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
                }
                catch { return barTime.AddHours(-5); }
            }
        }

        private void TryScheduledFetch()
        {
            try
            {
                var et = GetEtTime();
                int etMins = et.Hour * 60 + et.Minute;
                int matched = -1;
                foreach (var slot in FETCH_MINUTES_ET)
                {
                    int diff = etMins - slot;
                    if (diff >= 0 && diff < 5) { matched = slot; break; }
                }
                if (matched < 0) return;
                if (matched == _lastFetchMinuteEt) return;
                if ((DateTime.UtcNow - _lastFetchUtc).TotalMinutes < 4) return;

                _lastFetchMinuteEt = matched;
                _lastFetchUtc      = DateTime.UtcNow;
                Task.Run(() => FetchOnce());
            }
            catch (Exception ex)
            {
                Log($"TryScheduledFetch ERR: {ex.Message}");
            }
        }

        private static DateTime GetEtTime()
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            }
            catch
            {
                try
                {
                    var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                }
                catch { return DateTime.UtcNow.AddHours(-5); }
            }
        }

        private async Task FetchOnce()
        {
            try
            {
                bool hasKey   = !string.IsNullOrEmpty(ApiKey);
                string ticker = IsNqFamily() ? "NDX" : "SPX";
                string url    = hasKey
                    ? $"{API_URL}?ticker={ticker}"
                    : $"{API_URL}?ticker={ticker}&mode=free";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "TLADe-ATAS/3.0");
                if (hasKey) req.Headers.Add("X-API-Key", ApiKey);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                Log($"FETCH {ticker} ({(hasKey ? "live" : "free")}): HTTP {(int)resp.StatusCode} bytes={body?.Length ?? 0}");

                if (!resp.IsSuccessStatusCode) return;
                if (string.IsNullOrEmpty(body) || !body.Contains("L:")) return;

                _isDelayedMode = !hasKey;
                _rawData       = body;
                ParseData(body);
                RedrawChart();
                await PushToN8n().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"FetchOnce ERR: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  N8N WEBHOOK PUSH
        // ──────────────────────────────────────────────────────────────────────

        private async Task PushToN8n()
        {
            try
            {
                if (!N8nEnabled) return;
                var url = (N8nWebhookUrl ?? "").Trim();
                if (string.IsNullOrEmpty(url)) return;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    Log($"N8N skip: URL invalid '{url}'");
                    return;
                }

                List<LevelEntry> snap;
                lock (_lvlLock) snap = new List<LevelEntry>(_levels);
                if (snap.Count == 0) return;

                string ticker = ResolveTicker();
                string mode   = _isDelayedMode ? "FREE" : "LIVE";
                double spot   = GetLiveSpot();

                var sb = new System.Text.StringBuilder(2048);
                sb.Append('{');
                sb.Append("\"source\":\"TLADe GEX Dashboard\",");
                sb.Append("\"ticker\":\"").Append(JsonEsc(ticker)).Append("\",");
                sb.Append("\"mode\":\"").Append(mode).Append("\",");
                sb.Append("\"spot\":").Append(spot.ToString("0.####", _inv)).Append(',');
                sb.Append("\"esSpxSpread\":").Append(EffectiveEsSpread.ToString("0.####", _inv)).Append(',');
                sb.Append("\"timestampUtc\":\"").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", _inv)).Append("\",");
                sb.Append("\"count\":").Append(snap.Count).Append(',');
                sb.Append("\"raw\":\"").Append(JsonEsc(_rawData)).Append("\",");
                sb.Append("\"levels\":[");
                for (int i = 0; i < snap.Count; i++)
                {
                    var lvl = snap[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    sb.Append("\"strike\":").Append(lvl.RawStrike.ToString("0.####", _inv)).Append(',');
                    sb.Append("\"price\":").Append(ConvertPrice(lvl.RawStrike).ToString("0.####", _inv)).Append(',');
                    sb.Append("\"type\":\"").Append(JsonEsc(lvl.Type)).Append("\",");
                    sb.Append("\"label\":\"").Append(JsonEsc(lvl.Label)).Append("\",");
                    sb.Append("\"magnitude\":").Append(lvl.Magnitude.ToString("0.####", _inv));
                    sb.Append('}');
                }
                sb.Append("]}");

                using var content = new StringContent(sb.ToString(),
                    System.Text.Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(url, content).ConfigureAwait(false);
                Log($"N8N POST {ticker} levels={snap.Count}: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                Log($"N8N PushToN8n ERR: {ex.Message}");
            }
        }

        private static string JsonEsc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────────
        //  PARSE
        // ──────────────────────────────────────────────────────────────────────

        private void ParseData(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                lock (_lvlLock) _levels.Clear();
                Log("PARSED cleared (empty input)");
                return;
            }
            var fresh = new List<LevelEntry>();

            raw = raw.Replace("\r", "").Replace("\n", "").Trim();

            if (raw.StartsWith("S:", StringComparison.Ordinal))
            {
                int sEnd = raw.IndexOf('|');
                if (sEnd > 2)
                {
                    var spreadStr = raw.Substring(2, sEnd - 2);
                    if (double.TryParse(spreadStr, NumberStyles.Float, _inv, out var sp) && sp > 0)
                        EsSpxSpread = sp;
                    raw = raw.Substring(sEnd + 1);
                }
            }

            string levelsData = "";
            string profileData = "";
            int pPos = raw.IndexOf("|P:", StringComparison.Ordinal);
            if (pPos >= 0)
            {
                var before = raw.Substring(0, pPos);
                if (before.StartsWith("L:", StringComparison.Ordinal))
                    levelsData = before.Substring(2);
                profileData = raw.Substring(pPos + 3);   // dupa "|P:"
            }
            else if (raw.StartsWith("L:", StringComparison.Ordinal))
            {
                levelsData = raw.Substring(2);
            }

            // P: = profil GEX per strike -> map strike(rotunjit) -> semn (+1/-1)
            var signMap = new Dictionary<long, int>();
            if (!string.IsNullOrEmpty(profileData))
            {
                foreach (var pair in profileData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = pair.Split(',');
                    if (p.Length < 2) continue;
                    if (!double.TryParse(p[0], NumberStyles.Any, _inv, out var pstrike)) continue;
                    int sign = 0;
                    if (p.Length >= 3 && int.TryParse(p[2], NumberStyles.Any, _inv, out var s))
                        sign = Math.Sign(s);
                    else if (double.TryParse(p[1], NumberStyles.Any, _inv, out var v))
                        sign = v >= 0 ? 1 : -1;
                    signMap[(long)Math.Round(pstrike)] = sign;
                }
            }

            if (!string.IsNullOrEmpty(levelsData))
            {
                foreach (var pair in levelsData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split(',');
                    if (parts.Length < 3) continue;
                    if (!double.TryParse(parts[0], NumberStyles.Any, _inv, out var strike)) continue;
                    var type  = (parts[1] ?? "").Trim();
                    var label = (parts[2] ?? "").Trim();
                    double mag = 0;
                    if (parts.Length >= 5) double.TryParse(parts[4], NumberStyles.Any, _inv, out mag);
                    int gsign = signMap.TryGetValue((long)Math.Round(strike), out var sv) ? sv : 0;
                    fresh.Add(new LevelEntry
                    {
                        RawStrike = strike,
                        Type      = type,
                        Label     = label,
                        Magnitude = Math.Abs(mag),
                        GexSign   = gsign,
                    });
                }
            }

            if (fresh.Count == 0)
            {
                Log($"PARSED skipped: 0 levels from {raw.Length} chars (input incomplete/invalid) — keeping existing");
                return;
            }
            lock (_lvlLock) { _levels.Clear(); _levels.AddRange(fresh); }
            Log($"PARSED levels={fresh.Count}");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  ONRENDER — real rendering on UI thread
        // ──────────────────────────────────────────────────────────────────────

        protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
        {
            try
            {
                if (layout != DrawingLayouts.Final) return;
                if (ChartInfo == null) return;

                EnsureFont();

                int regionW = ChartInfo.PriceChartContainer.Region.Width;
                int x1 = 0;
                int x2 = regionW;

                // Snapshot under lock
                List<LevelEntry> snap;
                lock (_lvlLock) snap = new List<LevelEntry>(_levels);

                if (ShowStatus)
                    DrawStatus(ctx, snap.Count);

                // v3.1.1 — Session AVWAP polylines (= local compute, no payload
                // dependency). Rendered before the snap.Count gate so they
                // appear even if no cloud levels are loaded yet.
                if (ShowAvwapAsia) DrawAvwapPolyline(ctx, _avwapAsia, AVWAP_COLOR_ASIA, "Asia");
                if (ShowAvwapEU)   DrawAvwapPolyline(ctx, _avwapEU,   AVWAP_COLOR_EU,   "EU");
                if (ShowAvwapUS)   DrawAvwapPolyline(ctx, _avwapUS,   AVWAP_COLOR_US,   "US");
                if (ShowAvwapPD)   DrawAvwapPolyline(ctx, _avwapPD,   AVWAP_COLOR_PD,   "PD");

                // v3.2 — local multi-TF BOS (W/D/H4/H1) computed from the chart's
                // native bars (1:1 with terminal computeAllBOS). Cloud PA is
                // deprecated client-side, so these are computed here. Added before
                // the snap.Count gate so they render even with no cloud levels.
                if (ShowBreakoutLevels)
                {
                    // CurrentBar is the bar COUNT — valid indices are 0..CurrentBar-1.
                    ComputeLocalBos(CurrentBar);
                    snap.AddRange(_localBos);
                }

                // Local Structure (PDH/PDL/PWH/PWL) from the chart's own bars — Mother is GEX-only,
                // price-dependent levels are computed front-end-side. Added before the snap.Count gate.
                if (ShowStructureLevels)
                {
                    ComputeLocalPA(CurrentBar);
                    snap.AddRange(_localPA);
                }

                if (snap.Count == 0) return;

                double spot = GetLiveSpot();
                int maxVisible = MaxGexLevels <= 0 ? 10 : MaxGexLevels;
                bool maxIsAll = maxVisible >= 999;
                int halfMax = (int)Math.Ceiling(maxVisible / 2.0);

                // Closest CW/PW above/below for "protect" rule
                double closestAbove = double.NaN, closestBelow = double.NaN;
                double dAbove = double.MaxValue, dBelow = double.MaxValue;
                foreach (var lvl in snap)
                {
                    if (!IsGex(lvl.Type)) continue;
                    double y = ConvertPrice(lvl.RawStrike);
                    double d = Math.Abs(y - spot);
                    if (y > spot && d < dAbove) { dAbove = d; closestAbove = lvl.RawStrike; }
                    else if (y <= spot && d < dBelow) { dBelow = d; closestBelow = lvl.RawStrike; }
                }

                // Max wall magnitude for length scaling
                double maxWallMag = 0;
                foreach (var lvl in snap)
                    if (IsGex(lvl.Type) && lvl.Magnitude > maxWallMag)
                        maxWallMag = lvl.Magnitude;
                if (maxWallMag <= 0) maxWallMag = 1;

                // Max magnitude printre nivelele cu directie GEX, pt scalarea lungimii liniutei
                double maxTickMag = 0;
                foreach (var lvl in snap)
                    if (lvl.GexSign != 0 && lvl.Magnitude > maxTickMag)
                        maxTickMag = lvl.Magnitude;
                if (maxTickMag <= 0) maxTickMag = 1;

                double maxWallPx = regionW * (WallMaxPct / 100.0);
                double minWallPx = regionW * (WallMinPct / 100.0);

                int aboveDrawn = 0, belowDrawn = 0;
                int drawn = 0;

                foreach (var lvl in snap)
                {
                    double y = ConvertPrice(lvl.RawStrike);
                    bool isGex          = IsGex(lvl.Type);
                    bool isSystem       = IsSystem(lvl.Type);
                    bool isStructure    = IsStructure(lvl.Type);
                    bool isBreakout     = IsBreakout(lvl.Type);
                    bool isCharm        = IsCharmMagnet(lvl.Type);
                    bool passes = !EnableThreshold || !isGex ||
                                  lvl.Magnitude == 0 || lvl.Magnitude >= GexThreshold;
                    bool isProtected = isGex && (NearlyEqual(lvl.RawStrike, closestAbove) ||
                                                 NearlyEqual(lvl.RawStrike, closestBelow));
                    bool isAbove = y > spot;
                    bool show = false;

                    if (isGex && ShowGexLevels && passes)
                    {
                        if (maxIsAll) show = true;
                        else if (isProtected) show = true;
                        else if (isAbove && aboveDrawn < halfMax) show = true;
                        else if (!isAbove && belowDrawn < halfMax) show = true;
                    }
                    else if (isSystem    && ShowSystemLevels)    show = true;
                    else if (isStructure && ShowStructureLevels) show = true;
                    else if (isBreakout  && ShowBreakoutLevels)  show = true;
                    else if (isCharm     && ShowCharmMagnet)     show = true;

                    if (!show) continue;

                    DColor color;
                    int width = isSystem ? LineWidthSystem : LineWidthGex;

                    if (lvl.Type == "CW")
                    {
                        bool flipped = y < spot;
                        color = flipped ? DColor.LimeGreen : DColor.FromArgb(0xef, 0x44, 0x44);
                        if (!maxIsAll && !isProtected) { if (isAbove) aboveDrawn++; else belowDrawn++; }
                    }
                    else if (lvl.Type == "PW")
                    {
                        bool flipped = y > spot;
                        color = flipped ? DColor.FromArgb(0xef, 0x44, 0x44) : DColor.LimeGreen;
                        if (!maxIsAll && !isProtected) { if (isAbove) aboveDrawn++; else belowDrawn++; }
                    }
                    else if (lvl.Type == "ZG") color = DColor.DodgerBlue;
                    else if (lvl.Type == "MP") color = DColor.Orange;
                    else if (lvl.Type == "EH" || lvl.Type == "EL") color = DColor.Gold;
                    else if (lvl.Type == "VH" || lvl.Type == "VL") color = DColor.FromArgb(100, 180, 255);
                    // v3.1 — new families (palettes chosen to be distinct from CW/PW red/green)
                    else if (lvl.Type == "BL") color = DColor.FromArgb(0x10, 0xB9, 0x81);  // emerald — Breakout Long
                    else if (lvl.Type == "BS") color = DColor.FromArgb(0xF4, 0x3F, 0x5E);  // rose    — Breakout Short
                    else if (lvl.Type == "CM") color = DColor.FromArgb(0xC0, 0x73, 0xFF);  // violet  — Charm Magnet
                    else color = DColor.DimGray;

                    // Compute line start: scaled by magnitude for walls, full-width otherwise
                    int lineX1 = x1;
                    if (ScaleWallsByMag && isGex && maxWallMag > 0)
                    {
                        double frac = Math.Min(1.0, lvl.Magnitude / maxWallMag);
                        double len  = minWallPx + frac * (maxWallPx - minWallPx);
                        lineX1 = x2 - (int)len;
                        if (lineX1 < 0) lineX1 = 0;
                    }

                    int tickLen = GexTickLength;
                    if (GexScaleByStrength)
                    {
                        double frac = lvl.Magnitude > 0 ? Math.Min(1.0, lvl.Magnitude / maxTickMag) : 0;
                        tickLen = (int)Math.Round(GexTickLengthMin + frac * (GexTickLength - GexTickLengthMin));
                        if (tickLen < 1) tickLen = 1;
                    }

                    HL(ctx, (decimal)y, $"{FormatPrice(lvl.RawStrike)} {lvl.Label}", color, width, lineX1, x2, isAbove, lvl.GexSign, tickLen);
                    drawn++;
                }

                if (_ocCalls > 0 && _ocCalls % 5000 == 0)
                    Log($"OnRender drew {drawn}/{snap.Count} levels");
            }
            catch (Exception ex)
            {
                Log($"OnRender EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HL(RenderContext ctx, decimal price, string label, DColor color, int width, int x1, int x2, bool isAbove, int gexSign, int tickLen)
        {
            if (price <= 0m) return;
            int y = ChartInfo.GetYByPrice(price, false);
            var pen = new RenderPen(color, width);
            ctx.DrawLine(pen, x1, y, x2, y);

            if (ShowLabels)
            {
                var sz = ctx.MeasureString(label, _fn);
                int labW = (int)sz.Width;
                int labH = (int)sz.Height;
                int lx = (x1 > labW + 10) ? (x1 - labW - 6) : (x2 - labW - 6);
                int ly = y - labH - 1;
                byte ba = (byte)Math.Clamp(LabelBgAlpha, 0, 255);
                DColor bg;
                if (LabelBgPositional)
                {
                    var src = isAbove ? LabelBgAbove : LabelBgBelow;
                    bg = DColor.FromArgb(ba, src.R, src.G, src.B);
                }
                else
                {
                    bg = DColor.FromArgb(ba,
                        Math.Min(color.R / 4 + 8, 255),
                        Math.Min(color.G / 4 + 8, 255),
                        Math.Min(color.B / 4 + 8, 255));
                }
                var textCol = LabelInheritLineColor
                    ? color
                    : DColor.FromArgb(LabelTextColor.A, LabelTextColor.R, LabelTextColor.G, LabelTextColor.B);
                int boxX = lx - 2, boxY = ly - 1, boxW = labW + 6, boxH = labH + 2;
                ctx.FillRectangle(bg, new DRect(boxX, boxY, boxW, boxH));
                ctx.DrawString(label, _fn, textCol,
                    new DRect(lx, ly, labW + 4, labH), _sfL);

                // Liniuta directie GEX: dreapta = pozitiv, stanga = negativ. Culoarea liniei nivelului.
                if (ShowGexDirection && gexSign != 0)
                {
                    int midY = boxY + boxH / 2;
                    const int gap = 3;
                    int tx1, tx2;
                    if (gexSign > 0) { tx1 = boxX + boxW + gap; tx2 = tx1 + tickLen; }
                    else             { tx2 = boxX - gap;        tx1 = tx2 - tickLen; }
                    var tickPen = new RenderPen(color, GexTickWidth);
                    ctx.DrawLine(tickPen, tx1, midY, tx2, midY);
                }
            }

            if (ShowAxisMarkers)
            {
                try { this.DrawLabelOnPriceAxis(ctx, price.ToString("0.##", _inv), y, _fn, color, color); }
                catch { }
            }
        }

        private void DrawStatus(RenderContext ctx, int count)
        {
            try
            {
                string mode = _isDelayedMode ? "FREE" : "LIVE";
                string txt = $" TLADe GEX [{mode}] levels={count}";
                var sz = ctx.MeasureString(txt, _fn);
                int x = 4, y = 4;
                var bg = DColor.FromArgb(180, 12, 14, 22);
                ctx.FillRectangle(bg, new DRect(x, y, (int)sz.Width + 8, (int)sz.Height + 4));
                ctx.DrawString(txt, _fn, DColor.Gainsboro,
                    new DRect(x + 4, y + 2, (int)sz.Width + 4, (int)sz.Height + 2), _sfL);
            }
            catch { }
        }

        private void EnsureFont()
        {
            if (_fn == null || _fn.Size != LabelFontSize)
                _fn = new RenderFont("Arial", LabelFontSize);
        }

        // Session AVWAP colors — match the Pine indicator's palette so the
        // ATAS chart and a TradingView screenshot stack identically.
        private static readonly DColor AVWAP_COLOR_ASIA = DColor.FromArgb(0xF5, 0x9E, 0x0B); // amber
        private static readonly DColor AVWAP_COLOR_EU   = DColor.FromArgb(0x3B, 0x82, 0xF6); // blue
        private static readonly DColor AVWAP_COLOR_US   = DColor.FromArgb(0x22, 0xC5, 0x5E); // green
        private static readonly DColor AVWAP_COLOR_PD   = DColor.FromArgb(0x6E, 0xE7, 0xB7); // dim green

        private void DrawAvwapPolyline(RenderContext ctx, decimal[] arr, DColor color, string label)
        {
            if (arr == null || arr.Length == 0) return;
            try
            {
                int firstBar = ChartInfo.PriceChartContainer.FirstVisibleBarNumber;
                int lastBar  = ChartInfo.PriceChartContainer.LastVisibleBarNumber;
                if (firstBar < 0) firstBar = 0;
                if (lastBar  >= arr.Length) lastBar = arr.Length - 1;

                // When ShowHistoricalAvwap is OFF, clip to the current futures
                // day (= bars whose ET time is on or after the most recent
                // 18:00 ET anchor). Avoids a wall of overlapping polylines
                // from past sessions cluttering the chart.
                DateTime? minEt = null;
                if (!ShowHistoricalAvwap)
                {
                    // Clip to the last BAR's futures day (data-relative), not wall-clock now —
                    // else a weekend / closed-market chart hides the latest session's AVWAP.
                    var lastC = GetCandle(lastBar);
                    minEt = AsiaAnchorFor(lastC != null ? ToEt(lastC.Time) : GetEtTime());
                }

                var pen = new RenderPen(color, AvwapLineWidth);
                int? prevX = null, prevY = null;
                int lastValidX = -1, lastValidY = -1;
                int drawn = 0;

                for (int b = firstBar; b <= lastBar; b++)
                {
                    decimal v = arr[b];
                    if (v == AVWAP_NA) { prevX = prevY = null; continue; }

                    if (minEt.HasValue)
                    {
                        var c = GetCandle(b);
                        if (c == null) { prevX = prevY = null; continue; }
                        if (ToEt(c.Time) < minEt.Value) { prevX = prevY = null; continue; }
                    }

                    int x = ChartInfo.PriceChartContainer.GetXByBar(b);
                    int y = ChartInfo.PriceChartContainer.GetYByPrice(v, false);

                    if (prevX.HasValue)
                    {
                        ctx.DrawLine(pen, prevX.Value, prevY.Value, x, y);
                        drawn++;
                    }
                    prevX = x; prevY = y;
                    lastValidX = x; lastValidY = y;
                }

                if (ShowAvwapLabels && lastValidX >= 0)
                {
                    string txt = $" {label} ";
                    var sz = ctx.MeasureString(txt, _fn);
                    var bg = DColor.FromArgb(180, color.R, color.G, color.B);
                    int lx = lastValidX + 2;
                    int ly = lastValidY - (int)sz.Height / 2;
                    ctx.FillRectangle(bg, new DRect(lx, ly, (int)sz.Width + 4, (int)sz.Height + 2));
                    ctx.DrawString(txt, _fn, DColor.Black,
                        new DRect(lx + 2, ly + 1, (int)sz.Width + 2, (int)sz.Height + 2), _sfL);
                }
            }
            catch (Exception ex)
            {
                Log($"DrawAvwapPolyline({label}) EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  HELPERS
        // ──────────────────────────────────────────────────────────────────────

        private double GetLiveSpot()
        {
            try
            {
                var idx = CurrentBar - 1;
                if (idx < 0) return 0;
                var c = GetCandle(idx);
                if (c == null && idx > 0) c = GetCandle(idx - 1);
                return c != null ? (double)c.Close : 0;
            }
            catch { return 0; }
        }

        private static bool IsGex(string t)       => t == "CW" || t == "PW" || t == "GL";
        private static bool IsSystem(string t)    => t == "ZG" || t == "MP" || t == "EH" ||
                                                     t == "EL" || t == "VH" || t == "VL";
        // v3.1 — 2 new families surfaced by the extended cloud payload.
        // The parser is generic (= any type tag from L: lands in _levels) so
        // we only need to classify + color them here.
        // VP (POC/VAH/VAL) and Session AVWAP are NOT in the payload by design:
        // ATAS native VolumeProfile + VWAP studies compute those from local
        // chart bars — no point duplicating them here.
        private static bool IsBreakout(string t)       => t == "BL"  || t == "BS";
        private static bool IsCharmMagnet(string t)    => t == "CM";
        private static bool IsStructure(string t) => t == "PDH" || t == "PDL" ||
                                                     t == "PWH" || t == "PWL";

        private static bool NearlyEqual(double a, double b) =>
            !double.IsNaN(a) && !double.IsNaN(b) && Math.Abs(a - b) < 1e-9;

        private bool IsNqFamily()
        {
            var t = (DisplayTicker ?? "").ToUpperInvariant();
            if (t == "NQ" || t == "NDX" || t == "QQQ") return true;
            if (t == "ES" || t == "SPX" || t == "SPY") return false;
            // "auto" or unknown -> probe instrument
            var inst = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
            return inst.Contains("NQ") || inst.Contains("NDX") || inst.Contains("MNQ");
        }

        private string ResolveTicker()
        {
            var t = (DisplayTicker ?? "").ToUpperInvariant();
            if (t == "ES" || t == "SPX" || t == "SPY" || t == "NQ" || t == "NDX" || t == "QQQ")
                return t;
            // auto
            var inst = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
            if (inst.Contains("NQ") || inst.Contains("NDX") || inst.Contains("MNQ")) return "NQ";
            return "ES";
        }

        private double EffectiveEsSpread =>
            ManualSpreadOverride > 0 ? ManualSpreadOverride : EsSpxSpread;

        // ──────────────────────────────────────────────────────────────────────
        //  LOCAL BOS — v3.2  (1:1 port of terminal clientEngineService.computeAllBOS)
        //  Multi-timeframe Break-of-Structure: W / D / H4 / H1 — ALL of them, like
        //  the terminal (no per-TF toggles, no defaults invented). For each TF the
        //  most recent quality bull + bear BOS is detected (bull -> level at prior
        //  high, bear -> prior low), validated by no later close back through it.
        //  All bars are aggregated from the chart's OWN candles (native ES/NQ price,
        //  no spread, no cloud): daily by ET day, weekly by ISO week, H4/H1 by
        //  240/60-min buckets anchored at 18:00 ET (Globex open) — same as the terminal.
        // ──────────────────────────────────────────────────────────────────────
        private struct DBar { public DateTime Day; public double H, L, C; }

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

        // Chart bars -> daily bars by ET calendar day.
        private List<DBar> BuildDailyBars(int barCount)
        {
            var days = new List<DBar>();
            bool has = false; DateTime curDay = DateTime.MinValue; DBar cur = default;
            for (int i = 0; i < barCount; i++)
            {
                var c = GetCandle(i);
                if (c == null) continue;
                DateTime day = ToEt(c.Time).Date;
                if (!has || day != curDay)
                {
                    if (has) days.Add(cur);
                    curDay = day; has = true;
                    cur = new DBar { Day = day, H = (double)c.High, L = (double)c.Low, C = (double)c.Close };
                }
                else
                {
                    if ((double)c.High > cur.H) cur.H = (double)c.High;
                    if ((double)c.Low  < cur.L) cur.L = (double)c.Low;
                    cur.C = (double)c.Close;
                }
            }
            if (has) days.Add(cur);
            return days;
        }

        // Daily -> weekly by ISO week (= clientEngineService.aggregateToWeekly).
        private static List<DBar> AggregateToWeekly(List<DBar> daily)
        {
            var outl = new List<DBar>();
            bool has = false; int curKey = int.MinValue; DBar cur = default;
            foreach (var d in daily)
            {
                int key = System.Globalization.ISOWeek.GetYear(d.Day) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(d.Day);
                if (!has || key != curKey) { if (has) outl.Add(cur); has = true; curKey = key; cur = d; }
                else { if (d.H > cur.H) cur.H = d.H; if (d.L < cur.L) cur.L = d.L; cur.C = d.C; }
            }
            if (has) outl.Add(cur);
            return outl;
        }

        private static long FloorDiv(long a, long b) { long q = a / b; if ((a % b != 0) && ((a < 0) != (b < 0))) q--; return q; }

        // Chart bars -> period buckets (minutes) anchored at 18:00 ET (Globex open),
        // = clientEngineService.aggregateCandles. Used for H4 (240) and H1 (60).
        private List<DBar> AggregateByEtPeriod(int barCount, int periodMinutes)
        {
            var outl = new List<DBar>();
            bool has = false; long curSlot = long.MinValue; DBar cur = default;
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            const long anchor = 18 * 60; // 18:00 ET, minutes
            for (int i = 0; i < barCount; i++)
            {
                var c = GetCandle(i);
                if (c == null) continue;
                var et = ToEt(c.Time);
                long etMin = (long)Math.Floor((et - epoch).TotalMinutes);
                long slot = FloorDiv(etMin - anchor, periodMinutes) * periodMinutes + anchor;
                if (!has || slot != curSlot)
                {
                    if (has) outl.Add(cur);
                    has = true; curSlot = slot;
                    cur = new DBar { Day = et.Date, H = (double)c.High, L = (double)c.Low, C = (double)c.Close };
                }
                else { if ((double)c.High > cur.H) cur.H = (double)c.High; if ((double)c.Low < cur.L) cur.L = (double)c.Low; cur.C = (double)c.Close; }
            }
            if (has) outl.Add(cur);
            return outl;
        }

        // = clientEngineService.detectBOS: most recent valid bull + bear BOS.
        private void DetectBosInto(List<DBar> c, string tf, List<LevelEntry> outList)
        {
            if (c.Count < 3) return;
            bool foundBull = false, foundBear = false;
            for (int i = c.Count - 2; i >= 2 && (!foundBull || !foundBear); i--)
            {
                var curr = c[i]; var prev = c[i - 1];
                if (!foundBull && IsQualityBreakout(curr, prev, true))
                {
                    bool valid = true;
                    for (int j = i + 1; j < c.Count; j++) if (c[j].C < prev.H) { valid = false; break; }
                    if (valid) { outList.Add(MakeBos(prev.H, true, tf)); foundBull = true; }
                }
                if (!foundBear && IsQualityBreakout(curr, prev, false))
                {
                    bool valid = true;
                    for (int j = i + 1; j < c.Count; j++) if (c[j].C > prev.L) { valid = false; break; }
                    if (valid) { outList.Add(MakeBos(prev.L, false, tf)); foundBear = true; }
                }
            }
        }

        private LevelEntry MakeBos(double chartPrice, bool bull, string tf)
        {
            return new LevelEntry
            {
                RawStrike = ChartToRaw(chartPrice),
                Type = bull ? "BL" : "BS",
                Label = "BOS " + tf + (bull ? " L" : " S"),
                Magnitude = 0,
                GexSign = 0
            };
        }

        private LevelEntry MakePA(double chartPrice, string type)
        {
            return new LevelEntry
            {
                RawStrike = ChartToRaw(chartPrice),
                Type = type,        // PDH/PDL/PWH/PWL
                Label = type,
                Magnitude = 0,
                GexSign = 0
            };
        }

        // Local Structure: PDH/PDL = prior futures-day (18:00 ET) high/low; PWH/PWL = prior week
        // (Monday-anchored) high/low, from the chart's own bars. Same futures-day + week logic as
        // the terminal & the NT8 bridge, so all front-ends agree. Cloud PA is deprecated client-side.
        private void ComputeLocalPA(int barCount)
        {
            if (barCount == _paLastComputedBars) return;
            _paLastComputedBars = barCount;
            _localPA.Clear();
            if (barCount < 2) return;

            var dayH = new SortedDictionary<DateTime, double>();
            var dayL = new SortedDictionary<DateTime, double>();
            for (int i = 0; i < barCount; i++)
            {
                var c = GetCandle(i);
                if (c == null) continue;
                var et = ToEt(c.Time);
                var fd = (et.Hour >= 18 ? et.Date.AddDays(1) : et.Date);   // futures day (18:00 ET boundary)
                double h = (double)c.High, l = (double)c.Low;
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
                var wk = d.AddDays(-back).Date;             // Monday of that week
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

        // = clientEngineService.computeAllBOS: W / D / H4 / H1, all of them.
        private void ComputeLocalBos(int barCount)
        {
            if (barCount == _bosLastComputedBars) return;   // recompute only when bars change
            _bosLastComputedBars = barCount;
            _localBos.Clear();
            // H1/H4: from the chart's OWN intraday candles — identical to the
            // terminal (same ES minutes, same 18:00 ET anchor, same algorithm).
            var h4 = AggregateByEtPeriod(barCount, 240);
            var h1 = AggregateByEtPeriod(barCount, 60);
            DetectBosInto(h4, "H4", _localBos);
            DetectBosInto(h1, "H1", _localBos);

            // TODO (WS): D/W. The terminal computes daily/weekly BOS from Yahoo
            // ^GSPC CASH daily (fetchDailyCandles, interval=1d), NOT from the
            // chart's futures bars — so aggregating the chart here would NOT match
            // the terminal. To reproduce exactly, fetch ^GSPC (SPX) / ^NDX (NDX)
            // 1y/1d from Yahoo, apply the S: spread, then DetectBosInto(.., "D"/"W").
            // Skipped for now so we never show discrepant D/W levels.

            Log($"BOS local: bars={barCount} h4={h4.Count} h1={h1.Count} found={_localBos.Count} ticker={ResolveTicker()}");
        }

        // BOS levels come from native chart candles (ES/NQ) — already in chart price,
        // no spread. ChartToRaw maps to RawStrike space so the shared ConvertPrice()
        // round-trips back to the same price (ES/NQ/SPX/NDX pass-through; SPY x10, QQQ x40).
        private double ChartToRaw(double chartPrice)
        {
            switch (ResolveTicker())
            {
                case "SPY": return chartPrice * 10.0;
                case "QQQ": return chartPrice * 40.0;
                default:    return chartPrice;
            }
        }

        private double ConvertPrice(double rawStrike)
        {
            // The TLADe cloud publishes ES/NQ strikes ALREADY in futures space
            // (the ES↔SPX spread is applied server-side before the snapshot is
            // written). So for the futures tickers we just pass the raw strike
            // through. SPX / NDX stay raw; SPY = SPX / 10; QQQ = NDX / 40.
            // SnapToTick rounds to the instrument's tick grid afterwards.
            double cv;
            switch (ResolveTicker())
            {
                case "SPX": cv = rawStrike; break;
                case "SPY": cv = rawStrike / 10.0; break;
                case "ES":  cv = rawStrike; break;                          // already in ES futures space
                case "NDX": cv = rawStrike; break;
                case "QQQ": cv = rawStrike / 40.0; break;
                case "NQ":  cv = rawStrike; break;                          // already in NQ futures space
                default:    cv = rawStrike; break;
            }
            return SnapPrice(cv);
        }

        private double SnapPrice(double price)
        {
            if (!SnapToTick) return price;
            double tick = (double)(InstrumentInfo?.TickSize ?? 0.25m);
            if (tick <= 0) return price;
            return Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
        }

        private string FormatPrice(double rawStrike)
        {
            double cv = ConvertPrice(rawStrike);
            var t = ResolveTicker();
            if (t == "SPY" || t == "QQQ") return cv.ToString("0.##", _inv);
            // 2 decimals so user sees snapped value (e.g. 7382.50 not 7383)
            return cv.ToString("0.00", _inv);
        }

        private static void Log(string msg)
        {
            try
            {
                // MyDocuments may be OneDrive-redirected (e.g. ...\OneDrive\Dokumente);
                // AppendAllText does NOT create the dir, so create it or logging is
                // silently lost.
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [TLAdeGex] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  STRUCTS
        // ──────────────────────────────────────────────────────────────────────

        private struct LevelEntry
        {
            public double RawStrike;   // raw SPX or NDX from cloud
            public string Type;
            public string Label;
            public double Magnitude;
            public int    GexSign;     // +1 = GEX pozitiv (dreapta), -1 = negativ (stanga), 0 = necunoscut
        }
    }
}
