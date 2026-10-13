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

// TLADe GEX Levels — ATAS — release 3.5.2 (13 October 2026)
// Canonical feature set = the TradingView ES indicator: same names, colours, line styles, wall-flip
// rule (two 5-minute closes), breakout rule (closed bars, opposite-bar invalidation, keep last N),
// confluence zones (% of EM), profile bars, themes, silent above 1H, level-cross alerts.
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

        // profile (P:) rows, wall flips, drawn levels for the alerts, index→ETF ratio
        private struct ProfileEntry { public double RawStrike, Value; public int Sign; }
        private readonly List<ProfileEntry> _profile = new List<ProfileEntry>();
        private readonly Dictionary<double, bool> _wallFlipped = new Dictionary<double, bool>();
        private int _flipsComputedBars = -1;
        private string _flipsComputedKey = "";
        private struct AlertLevel { public double Price; public string Name; public string Kind; }
        private readonly List<AlertLevel> _alertLevels = new List<AlertLevel>();
        private readonly object _alertLock = new object();
        private int _alertLastBar = -1;
        private double _ndxQqqRatio = 0.0;
        private DColor _posColor = DColor.FromArgb(0x22, 0xc5, 0x5e), _negColor = DColor.FromArgb(0xef, 0x44, 0x44);
        private static readonly DColor C_ZG = DColor.FromArgb(0x9c, 0xa3, 0xaf), C_MP = DColor.FromArgb(0xef, 0x44, 0x44),
            C_EM = DColor.FromArgb(0x3b, 0x82, 0xf6), C_VB = DColor.FromArgb(0x9c, 0xa3, 0xaf), C_STRUCT = DColor.FromArgb(0x9c, 0xa3, 0xaf),
            C_CHARM = DColor.FromArgb(0xf9, 0x73, 0x16), C_DFLIP = DColor.FromArgb(0xf5, 0x9e, 0x0b),
            C_BO_BULL = DColor.FromArgb(0x22, 0xc5, 0x5e), C_BO_BEAR = DColor.FromArgb(0xef, 0x44, 0x44);

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
        public double EsSpxSpread { get; set; } = 0.0;   // 0 = not known: strikes shown as-is until S: arrives

        [Display(Name = "NQ↔NDX spread", GroupName = "2. Layout", Order = 3)]
        [Range(0, 200)]
        public double NqNdxSpread { get; set; } = 0.0;

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
        public bool ShowStructureLevels { get; set; } = false;

        [Display(Name = "Show Delta Flip (DF)", GroupName = "3. Visibility", Order = 9)]
        public bool ShowDeltaFlip { get; set; } = true;

        [Display(Name = "Show only levels near price", GroupName = "4. Filter", Order = 4)]
        public bool ShowOnlyNear { get; set; } = false;
        [Display(Name = "  Radius (%)", GroupName = "4. Filter", Order = 5)]
        public double NearPct { get; set; } = 3.0;

        [Display(Name = "Draw on timeframes above 1H", GroupName = "2. Layout", Order = 6,
            Description = "Off: on 4H, Daily, Weekly and Monthly charts the indicator stays silent.")]
        public bool ShowAboveH1 { get; set; } = false;

        [Display(Name = "Theme (Wall Street Classic/Boreal/Lady Trader)", GroupName = "5. Style", Order = 0)]
        public string Theme { get; set; } = "Wall Street Classic";
        [Display(Name = "Line style walls/ZG (Solid/Dashed/Dotted)", GroupName = "5. Style", Order = 0)]
        public string LineStyle { get; set; } = "Dotted";

        [Display(Name = "Show Breakout Structure", GroupName = "6. Breakout Structure", Order = 0,
            Description = "Breakouts from the chart's own bars, on closed bars of each timeframe. A breakout dies when a later bar of the same timeframe, in the opposite direction, closes back through it.")]
        public bool ShowBreakoutGroup { get; set; } = false;
        [Display(Name = "Monthly", GroupName = "6. Breakout Structure", Order = 1)] public bool ShowBosM { get; set; } = false;
        [Display(Name = "Weekly", GroupName = "6. Breakout Structure", Order = 2)] public bool ShowBosW { get; set; } = false;
        [Display(Name = "Daily", GroupName = "6. Breakout Structure", Order = 3)] public bool ShowBosD { get; set; } = true;
        [Display(Name = "4H", GroupName = "6. Breakout Structure", Order = 4)] public bool ShowBosH4 { get; set; } = true;
        [Display(Name = "1H", GroupName = "6. Breakout Structure", Order = 5)] public bool ShowBosH1 { get; set; } = true;
        [Range(1, 10)]
        [Display(Name = "Keep last N per timeframe", GroupName = "6. Breakout Structure", Order = 6)] public int BosKeepPerTf { get; set; } = 2;

        [Display(Name = "Show Confluence Zones", GroupName = "7. Confluence Zones", Order = 0)]
        public bool ShowConfluence { get; set; } = false;
        [Range(2, 5)]
        [Display(Name = "Min cluster size for box", GroupName = "7. Confluence Zones", Order = 1)] public int ConfluenceMinSize { get; set; } = 3;
        [Display(Name = "Band Width (% of EM)", GroupName = "7. Confluence Zones", Order = 2,
            Description = "Total band as a percentage of the EM range (EM High − EM Low), split half above and half below (7% = ±3.5%). No EM in the data → no zones.")]
        public double ConfluenceEmPct { get; set; } = 7.0;

        [Display(Name = "Show Profile Bars (P:)", GroupName = "8. Profile", Order = 0)]
        public bool ShowProfileBars { get; set; } = true;
        [Display(Name = "Profile width (px)", GroupName = "8. Profile", Order = 1)] public int ProfileWidthPx { get; set; } = 120;
        [Display(Name = "Profile bar height (ticks)", GroupName = "8. Profile", Order = 2)] public int ProfileBarHeightTicks { get; set; } = 8;
        [Display(Name = "Profile scale max", GroupName = "8. Profile", Order = 3)] public double ProfileScaleMax { get; set; } = 10.0;

        [Display(Name = "Show Session Boxes", GroupName = "8b. Session Boxes", Order = 0)] public bool ShowSessionBoxes { get; set; } = false;
        [Display(Name = "Asia (18:00-03:00 ET)", GroupName = "8b. Session Boxes", Order = 1)] public bool ShowBoxAsia { get; set; } = true;
        [Display(Name = "Europe (03:00-08:00 ET)", GroupName = "8b. Session Boxes", Order = 2)] public bool ShowBoxEU { get; set; } = true;
        [Display(Name = "Pre-Market (08:00-09:30 ET)", GroupName = "8b. Session Boxes", Order = 3)] public bool ShowBoxPre { get; set; } = true;
        [Display(Name = "US RTH (09:30-16:00 ET)", GroupName = "8b. Session Boxes", Order = 4)] public bool ShowBoxUS { get; set; } = true;
        [Display(Name = "Show Historical (prev days)", GroupName = "8b. Session Boxes", Order = 5)] public bool ShowSessionsHist { get; set; } = false;

        [Display(Name = "Enable Level Cross Alerts", GroupName = "9. Alerts", Order = 0)] public bool EnableAlerts { get; set; } = false;
        [Display(Name = "Call/Put Walls", GroupName = "9. Alerts", Order = 1)] public bool AlertWalls { get; set; } = true;
        [Display(Name = "ZG / Max Pain / EM / Vol Bands", GroupName = "9. Alerts", Order = 2)] public bool AlertSystem { get; set; } = true;
        [Display(Name = "Breakouts", GroupName = "9. Alerts", Order = 3)] public bool AlertBos { get; set; } = true;

        [Browsable(false)]
        public bool ShowBreakoutLevels { get => ShowBreakoutGroup; set => ShowBreakoutGroup = value; }

        [Display(Name = "Show Charm Magnet (CM)", GroupName = "3. Visibility", Order = 8)]
        public bool ShowCharmMagnet { get; set; } = false;

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
        // A CLOSE that crosses a drawn level: the close before on one side, the last closed bar on the
        // other. Evaluated once when a bar closes, so a wick through a wall does not fire.
        private void CheckLevelAlerts(int closedBar)
        {
            if (closedBar < 1 || closedBar == _alertLastBar) return;
            _alertLastBar = closedBar;
            var c1c = GetCandle(closedBar); var c2c = GetCandle(closedBar - 1);
            if (c1c == null || c2c == null) return;
            double c1 = (double)c1c.Close, c2 = (double)c2c.Close;
            List<AlertLevel> lv;
            lock (_alertLock) lv = new List<AlertLevel>(_alertLevels);
            foreach (var al in lv)
            {
                bool wanted = al.Kind == "wall" ? AlertWalls : al.Kind == "system" ? AlertSystem : AlertBos;
                if (!wanted) continue;
                bool up = c2 < al.Price && c1 >= al.Price, down = c2 > al.Price && c1 <= al.Price;
                if (!up && !down) continue;
                try { AddAlert("alert1", InstrumentInfo?.Instrument ?? "", $"TLADe {al.Name} crossed {(up ? "UP" : "DOWN")}", Colors.Black, Colors.Orange); }
                catch (Exception ex) { Log($"alert EX: {ex.Message}"); }
            }
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (EnableAlerts && bar == CurrentBar - 1 && bar >= 2) CheckLevelAlerts(bar - 1);
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
            // 03:00 ET — Europe open (was 02:00: the TradingView indicator and the chart both anchor at 03:00)
            var today03 = new DateTime(et.Year, et.Month, et.Day, 3, 0, 0);
            return et >= today03 ? today03 : today03.AddDays(-1);
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
                    {
                        if (IsNqFamily()) NqNdxSpread = sp; else EsSpxSpread = sp;
                    }
                    raw = raw.Substring(sEnd + 1);
                }
            }
            // R: index→ETF ratio — NDX→QQQ on an NQ string, SPX→SPY on an ES one
            if (raw.StartsWith("R:", StringComparison.Ordinal))
            {
                int rEnd = raw.IndexOf('|');
                if (rEnd > 2)
                {
                    if (double.TryParse(raw.Substring(2, rEnd - 2), NumberStyles.Float, _inv, out var rr) && rr > 0) _ndxQqqRatio = rr;
                    raw = raw.Substring(rEnd + 1);
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
            var freshProfile = new List<ProfileEntry>();
            if (!string.IsNullOrEmpty(profileData))
            {
                foreach (var pair in profileData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = pair.Split(',');
                    if (p.Length < 2) continue;
                    if (!double.TryParse(p[0], NumberStyles.Any, _inv, out var pstrike)) continue;
                    double pval = 0; double.TryParse(p[1], NumberStyles.Any, _inv, out pval);
                    int sign = 0;
                    if (p.Length >= 3 && int.TryParse(p[2], NumberStyles.Any, _inv, out var s))
                        sign = Math.Sign(s);
                    else sign = pval >= 0 ? 1 : -1;
                    signMap[(long)Math.Round(pstrike)] = sign;
                    freshProfile.Add(new ProfileEntry { RawStrike = pstrike, Value = pval, Sign = sign });
                }
            }
            lock (_lvlLock) { _profile.Clear(); _profile.AddRange(freshProfile); }

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
                ResolveTheme();
                if (!TfActive()) { lock (_alertLock) _alertLevels.Clear(); return; }

                int regionW = ChartInfo.PriceChartContainer.Region.Width;
                int x1 = 0;
                int x2 = regionW;

                // Snapshot under lock
                List<LevelEntry> snap;
                List<ProfileEntry> prof;
                lock (_lvlLock) { snap = new List<LevelEntry>(_levels); prof = new List<ProfileEntry>(_profile); }
                var alerts = new List<AlertLevel>();

                if (ShowSessionBoxes)
                    DrawSessionBoxes(ctx);
                if (ShowProfileBars && prof.Count > 0)
                    DrawProfileBars(ctx, prof, x2);
                if (ShowConfluence)
                    DrawConfluenceZones(ctx, snap, x1, x2);
                ComputeWallFlips(CurrentBar, snap);

                if (ShowStatus)
                    DrawStatus(ctx, snap.Count);

                // v3.1.1 — Session AVWAP polylines (= local compute, no payload
                // dependency). Rendered before the snap.Count gate so they
                // appear even if no cloud levels are loaded yet.
                if (ShowAvwapAsia) DrawAvwapPolyline(ctx, _avwapAsia, AVWAP_COLOR_ASIA, "AVWAP Asia");
                if (ShowAvwapEU)   DrawAvwapPolyline(ctx, _avwapEU,   AVWAP_COLOR_EU,   "AVWAP EU");
                if (ShowAvwapUS)   DrawAvwapPolyline(ctx, _avwapUS,   AVWAP_COLOR_US,   "AVWAP US");
                if (ShowAvwapPD)   DrawAvwapPolyline(ctx, _avwapPD,   AVWAP_COLOR_PD,   "AVWAP US Prev Day");

                // v3.2 — local multi-TF BOS (W/D/H4/H1) computed from the chart's
                // native bars (1:1 with terminal computeAllBOS). Cloud PA is
                // deprecated client-side, so these are computed here. Added before
                // the snap.Count gate so they render even with no cloud levels.
                if (ShowBreakoutGroup)
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
                    bool isDf           = IsDeltaFlip(lvl.Type);
                    bool inRange = !ShowOnlyNear || (spot > 0 && Math.Abs(spot - y) / spot * 100.0 <= NearPct);
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
                    else if (isStructure && ShowStructureLevels && lvl.IsLocal) show = true;   // feed structure ignored
                    else if (isBreakout  && ShowBreakoutGroup && lvl.IsLocal) show = true;      // feed breakouts ignored
                    else if (isCharm     && ShowCharmMagnet)     show = true;
                    else if (isDf        && ShowDeltaFlip)       show = true;

                    if (!show || !inRange) continue;

                    DColor color;
                    int width = isSystem ? LineWidthSystem : LineWidthGex;
                    bool isWallFlipped = false;

                    // Colour = NATURE (a Call Wall stays the call colour whatever its role); a flipped
                    // wall is told by a thinner line and the "↺" suffix.
                    if (lvl.Type == "CW")
                    {
                        isWallFlipped = _wallFlipped.TryGetValue(lvl.RawStrike, out var f1) && f1;
                        color = _negColor;
                        if (!maxIsAll && !isProtected) { if (isAbove) aboveDrawn++; else belowDrawn++; }
                    }
                    else if (lvl.Type == "PW")
                    {
                        isWallFlipped = _wallFlipped.TryGetValue(lvl.RawStrike, out var f2) && f2;
                        color = _posColor;
                        if (!maxIsAll && !isProtected) { if (isAbove) aboveDrawn++; else belowDrawn++; }
                    }
                    else if (lvl.Type == "ZG") color = C_ZG;
                    else if (lvl.Type == "MP") color = C_MP;
                    else if (lvl.Type == "EH" || lvl.Type == "EL" || lvl.Type == "EHR" || lvl.Type == "ELR") color = C_EM;
                    else if (lvl.Type == "VH" || lvl.Type == "VL") color = C_VB;
                    else if (lvl.Type == "BL") color = C_BO_BULL;
                    else if (lvl.Type == "BS") color = C_BO_BEAR;
                    else if (lvl.Type == "CM") color = C_CHARM;
                    else if (lvl.Type == "DF") color = C_DFLIP;
                    else color = C_STRUCT;
                    if (isWallFlipped) width = Math.Max(1, width - 1);
                    var dash = isBreakout ? (lvl.BoHigherTf ? System.Drawing.Drawing2D.DashStyle.Solid : System.Drawing.Drawing2D.DashStyle.Dash) : StyleFor(lvl.Type);
                    if (isBreakout) width = lvl.BoWidth;

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

                    string name = isBreakout ? $"{lvl.Label} {ConvertPrice(lvl.RawStrike).ToString("0.00", _inv)}"
                                             : $"{FormatPrice(lvl.RawStrike)} {LevelName(lvl.Type, lvl.Label)}{(isWallFlipped ? " \u21BA" : "")}";
                    HL(ctx, (decimal)y, name, color, width, lineX1, x2, isAbove, lvl.GexSign, tickLen, dash);
                    if (isGex || isSystem || isBreakout)
                        alerts.Add(new AlertLevel { Price = y, Name = name, Kind = isGex ? "wall" : isSystem ? "system" : "bo" });
                    drawn++;
                }
                lock (_alertLock) { _alertLevels.Clear(); _alertLevels.AddRange(alerts); }

                if (_ocCalls > 0 && _ocCalls % 5000 == 0)
                    Log($"OnRender drew {drawn}/{snap.Count} levels");
            }
            catch (Exception ex)
            {
                Log($"OnRender EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void HL(RenderContext ctx, decimal price, string label, DColor color, int width, int x1, int x2, bool isAbove, int gexSign, int tickLen, System.Drawing.Drawing2D.DashStyle dash)
        {
            if (price <= 0m) return;
            int y = ChartInfo.GetYByPrice(price, false);
            var pen = new RenderPen(color, width, dash);
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

        // Session boxes: Asia / Europe / Pre / US hi-lo rectangles of the last futures day on the chart
        // (18:00 ET boundary), or of every day with "Show Historical". Same sessions as the TradingView
        // indicator: Asia 18:00-03:00, EU 03:00-08:00, Pre 08:00-09:30, US 09:30-16:00 ET.
        private void DrawSessionBoxes(RenderContext ctx)
        {
            try
            {
                int n = CurrentBar;
                if (n < 2) return;
                var lastC = GetCandle(n - 1); if (lastC == null) return;
                var lastEt = ToEt(lastC.Time);
                DateTime lastDay = lastEt.Hour >= 18 ? lastEt.Date.AddDays(1) : lastEt.Date;
                bool[] on = { ShowBoxAsia, ShowBoxEU, ShowBoxPre, ShowBoxUS };
                DColor[] cols = { DColor.FromArgb(26, 0xf5, 0x9e, 0x0b), DColor.FromArgb(26, 0x3b, 0x82, 0xf6), DColor.FromArgb(20, 0xa8, 0x55, 0xf7), DColor.FromArgb(20, 0x22, 0xc5, 0x5e) };
                int curType = -1, b0 = -1, b1 = -1; double hi = 0, lo = 0;
                int firstBar = ShowSessionsHist ? 0 : Math.Max(0, n - 2000);
                Action flush = () =>
                {
                    if (curType < 0 || b0 < 0 || !on[curType]) return;
                    int x0 = ChartInfo.PriceChartContainer.GetXByBar(b0), x1 = ChartInfo.PriceChartContainer.GetXByBar(b1);
                    if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
                    int yT = ChartInfo.GetYByPrice((decimal)hi, false), yB = ChartInfo.GetYByPrice((decimal)lo, false);
                    var c = cols[curType];
                    ctx.FillRectangle(c, new DRect(x0, yT, Math.Max(2, x1 - x0), Math.Max(1, yB - yT)));
                    ctx.DrawRectangle(new RenderPen(DColor.FromArgb(90, c.R, c.G, c.B), 1), new DRect(x0, yT, Math.Max(2, x1 - x0), Math.Max(1, yB - yT)));
                };
                for (int i = firstBar; i < n; i++)
                {
                    var c = GetCandle(i); if (c == null) continue;
                    var et = ToEt(c.Time);
                    DateTime fd = et.Hour >= 18 ? et.Date.AddDays(1) : et.Date;
                    if (!ShowSessionsHist && fd != lastDay) continue;
                    int mins = et.Hour * 60 + et.Minute;
                    int type = (mins >= 1080 || mins < 180) ? 0 : mins < 480 ? 1 : mins < 570 ? 2 : mins < 960 ? 3 : -1;
                    if (type != curType)
                    {
                        flush();
                        curType = type; b0 = i; b1 = i; hi = (double)c.High; lo = (double)c.Low;
                    }
                    else
                    {
                        b1 = i;
                        if ((double)c.High > hi) hi = (double)c.High;
                        if ((double)c.Low < lo) lo = (double)c.Low;
                    }
                }
                flush();
            }
            catch (Exception ex) { Log($"DrawSessionBoxes EX: {ex.Message}"); }
        }

        // GEX profile: a right-edge histogram of the P: rows, calls one colour and puts the other.
        private void DrawProfileBars(RenderContext ctx, List<ProfileEntry> prof, int x2)
        {
            try
            {
                double maxAbs = Math.Max(1e-9, ProfileScaleMax);
                double tick = (double)(InstrumentInfo?.TickSize ?? 0.25m);
                int barH = Math.Max(2, Math.Abs(ChartInfo.GetYByPrice((decimal)(0.0), false) - ChartInfo.GetYByPrice((decimal)(tick * Math.Max(1, ProfileBarHeightTicks)), false)));
                int widthPx = Math.Max(10, ProfileWidthPx);
                foreach (var pr in prof)
                {
                    double frac = Math.Min(1.0, Math.Abs(pr.Value) / maxAbs);
                    if (frac <= 0) continue;
                    double y = ConvertPrice(pr.RawStrike);
                    int yPix = ChartInfo.GetYByPrice((decimal)y, false);
                    int len = (int)Math.Round(frac * widthPx);
                    var baseC = pr.Sign >= 0 ? _negColor : _posColor;   // calls = neg colour, puts = pos colour (as the Pine profile)
                    byte alpha = (byte)Math.Clamp((int)Math.Round(255.0 * (0.20 + frac * 0.50)), 0, 255);
                    ctx.FillRectangle(DColor.FromArgb(alpha, baseC.R, baseC.G, baseC.B), new DRect(x2 - len, yPix - barH / 2, len, Math.Max(1, barH)));
                }
            }
            catch (Exception ex) { Log($"DrawProfileBars EX: {ex.Message}"); }
        }

        // Confluence zones: every level on the chart (feed levels, breakouts, AVWAPs, structure), sorted
        // and clustered within a band sized on the EM range. No EM in the data → no zones.
        private void DrawConfluenceZones(RenderContext ctx, List<LevelEntry> snap, int x1, int x2)
        {
            try
            {
                double emH = double.NaN, emL = double.NaN;
                foreach (var l in snap) { if (l.Type == "EH") emH = ConvertPrice(l.RawStrike); else if (l.Type == "EL") emL = ConvertPrice(l.RawStrike); }
                if (double.IsNaN(emH) || double.IsNaN(emL) || emH - emL <= 0) return;
                double band = (emH - emL) * (ConfluenceEmPct / 100.0) / 2.0;
                var prices = new List<double>();
                foreach (var l in snap) if (IsGex(l.Type) || IsSystem(l.Type)) prices.Add(ConvertPrice(l.RawStrike));
                if (ShowBreakoutGroup) { ComputeLocalBos(CurrentBar); foreach (var b in _localBos) prices.Add(ConvertPrice(b.RawStrike)); }
                if (ShowStructureLevels) { ComputeLocalPA(CurrentBar); foreach (var pa in _localPA) prices.Add(ConvertPrice(pa.RawStrike)); }
                decimal[][] av = { _avwapAsia, _avwapEU, _avwapUS, _avwapPD };
                bool[] on = { ShowAvwapAsia, ShowAvwapEU, ShowAvwapUS, ShowAvwapPD };
                int lastIdx = CurrentBar - 1;
                for (int i = 0; i < 4; i++)
                {
                    if (!on[i] || av[i] == null || lastIdx < 0 || lastIdx >= av[i].Length) continue;
                    decimal v = av[i][lastIdx]; if (v <= 0) continue;
                    prices.Add((double)v);
                }
                prices.Sort();
                int n = prices.Count, minSize = Math.Max(2, ConfluenceMinSize);
                int i0 = 0;
                while (i0 < n)
                {
                    double cMin = prices[i0], cMax = cMin; int size = 1, j = i0 + 1;
                    while (j < n) { if (prices[j] - cMin <= 2 * band) { cMax = prices[j]; size++; j++; } else break; }
                    if (size >= minSize)
                    {
                        int capped = Math.Min(size, 5);
                        DColor c = capped >= 5 ? DColor.FromArgb(0xef, 0x44, 0x44) : capped >= 4 ? DColor.FromArgb(0xfb, 0x92, 0x3c) : DColor.FromArgb(0xfa, 0xcc, 0x15);
                        byte alpha = (byte)(capped >= 5 ? 89 : capped >= 4 ? 77 : 64);
                        int yTop = ChartInfo.GetYByPrice((decimal)cMax, false), yBot = ChartInfo.GetYByPrice((decimal)cMin, false);
                        if (yBot - yTop < 2) { yTop -= 1; yBot += 1; }
                        ctx.FillRectangle(DColor.FromArgb(alpha, c.R, c.G, c.B), new DRect(x1, yTop, x2 - x1, yBot - yTop));
                        string txt = capped >= 5 ? "UBER" : capped + "x";
                        var sz = ctx.MeasureString(txt, _fn);
                        ctx.DrawString(txt, _fn, DColor.FromArgb(220, c.R, c.G, c.B), new DRect(x1 + 4, (yTop + yBot) / 2 - (int)sz.Height / 2, (int)sz.Width + 4, (int)sz.Height), _sfL);
                    }
                    i0 = j;
                }
            }
            catch (Exception ex) { Log($"DrawConfluenceZones EX: {ex.Message}"); }
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
                int lastValidX = -1, lastValidY = -1; double lastValidValue = 0;
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
                    lastValidX = x; lastValidY = y; lastValidValue = (double)v;
                }

                if (ShowAvwapLabels && lastValidX >= 0)
                {
                    string txt = $" {lastValidValue.ToString("0.00", _inv)} {label} ";
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
                                                     t == "EL" || t == "EHR" || t == "ELR" || t == "VH" || t == "VL";
        private static bool IsDeltaFlip(string t) => t == "DF";

        // One name per level code, the same words on every platform. Labels read "price + name".
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

        private System.Drawing.Drawing2D.DashStyle UserLineStyle()
        {
            var st = (LineStyle ?? "").Trim();
            if (st.Equals("Solid", StringComparison.OrdinalIgnoreCase)) return System.Drawing.Drawing2D.DashStyle.Solid;
            if (st.Equals("Dashed", StringComparison.OrdinalIgnoreCase)) return System.Drawing.Drawing2D.DashStyle.Dash;
            return System.Drawing.Drawing2D.DashStyle.Dot;
        }

        // Line styles as the TradingView indicator: walls and Zero Gamma in the user's style, Max Pain /
        // Vol Bands / Structure dotted, EM and Delta Flip dashed, Charm Magnet solid, breakouts by timeframe.
        private System.Drawing.Drawing2D.DashStyle StyleFor(string t)
        {
            if (t == "CM") return System.Drawing.Drawing2D.DashStyle.Solid;
            if (t == "MP" || t == "VH" || t == "VL" || IsStructure(t)) return System.Drawing.Drawing2D.DashStyle.Dot;
            if (t == "EH" || t == "EL" || t == "EHR" || t == "ELR" || t == "DF") return System.Drawing.Drawing2D.DashStyle.Dash;
            return UserLineStyle();
        }

        private void ResolveTheme()
        {
            var th = (Theme ?? "").Trim();
            if (th.Equals("Boreal", StringComparison.OrdinalIgnoreCase)) { _posColor = DColor.FromArgb(0x22, 0xd3, 0xee); _negColor = DColor.FromArgb(0xf4, 0x72, 0xb6); }
            else if (th.Equals("Lady Trader", StringComparison.OrdinalIgnoreCase)) { _posColor = DColor.FromArgb(0x2d, 0xd4, 0xbf); _negColor = DColor.FromArgb(0xc0, 0x84, 0xfc); }
            else { _posColor = DColor.FromArgb(0x22, 0xc5, 0x5e); _negColor = DColor.FromArgb(0xef, 0x44, 0x44); }
        }

        // The indicator is an intraday tool: above 1H it draws nothing unless asked.
        private bool TfActive()
        {
            if (ShowAboveH1) return true;
            try
            {
                var tf = (ChartInfo?.TimeFrame ?? "").Trim().ToUpperInvariant();
                if (tf.StartsWith("D") || tf.StartsWith("W") || tf.StartsWith("MN")) return false;
                if (tf.StartsWith("H")) { int h; return int.TryParse(tf.Substring(1), out h) ? h <= 1 : true; }
                if (tf.StartsWith("M")) { int m; return int.TryParse(tf.Substring(1), out m) ? m <= 60 : true; }
                int mins; if (int.TryParse(tf, out mins)) return mins <= 60;
                return true;   // tick / range / volume bars: intraday
            }
            catch { return true; }
        }

        private int ChartTfMinutes()
        {
            try
            {
                var tf = (ChartInfo?.TimeFrame ?? "").Trim().ToUpperInvariant();
                int v;
                if (tf.StartsWith("H") && int.TryParse(tf.Substring(1), out v)) return v * 60;
                if (tf.StartsWith("M") && int.TryParse(tf.Substring(1), out v)) return v;
                if (int.TryParse(tf, out v)) return v;
            }
            catch { }
            return 0;
        }

        // Wall flip, the TradingView rule: two consecutive 5-minute closes beyond the strike flip the
        // wall, two the other way restore it. Replayed on the chart's bars aggregated to 5 minutes
        // (on charts above 5 minutes each bar close counts as one close). Deterministic on reload.
        private void ComputeWallFlips(int barCount, List<LevelEntry> snap)
        {
            string key = string.Join(";", snap.FindAll(l => l.Type == "CW" || l.Type == "PW").ConvertAll(l => l.Type + l.RawStrike.ToString("0.##", _inv)));
            if (barCount == _flipsComputedBars && key == _flipsComputedKey) return;
            _flipsComputedBars = barCount; _flipsComputedKey = key;
            _wallFlipped.Clear();
            var walls = snap.FindAll(l => l.Type == "CW" || l.Type == "PW");
            if (walls.Count == 0 || barCount < 3) return;
            int tf = ChartTfMinutes();
            var bars = (tf > 0 && tf < 5) ? AggregateByEtPeriod(barCount, 5) : null;
            int n = walls.Count;
            var cnt = new int[n]; var flipped = new bool[n];
            int last = (bars != null ? bars.Count : barCount) - 2;   // the last bar is still forming
            for (int i = Math.Max(0, last - 3000); i <= last; i++)
            {
                double close = bars != null ? bars[i].C : (double)(GetCandle(i)?.Close ?? 0m);
                if (close <= 0) continue;
                double c = ChartToRaw(close);
                for (int w = 0; w < n; w++)
                {
                    double k = walls[w].RawStrike; bool cw = walls[w].Type == "CW";
                    bool beyond = cw ? (flipped[w] ? c < k : c > k) : (flipped[w] ? c > k : c < k);
                    if (beyond) { cnt[w]++; if (cnt[w] >= 2) { flipped[w] = !flipped[w]; cnt[w] = 0; } }
                    else cnt[w] = 0;
                }
            }
            for (int w = 0; w < n; w++) _wallFlipped[walls[w].RawStrike] = flipped[w];
        }
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
        private double EffectiveNqSpread =>
            ManualSpreadOverride > 0 ? ManualSpreadOverride : NqNdxSpread;

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
        private struct DBar { public DateTime Day; public double O, H, L, C; }

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
                    cur = new DBar { Day = day, O = (double)c.Open, H = (double)c.High, L = (double)c.Low, C = (double)c.Close };
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

        // Chart bars -> one bar per calendar key of the FUTURES day (week = its Monday, month = its 1st)
        private List<DBar> AggregateByFuturesKey(int barCount, bool monthly)
        {
            var outl = new List<DBar>();
            bool has = false; DateTime curKey = DateTime.MinValue; DBar cur = default;
            for (int i = 0; i < barCount; i++)
            {
                var c = GetCandle(i);
                if (c == null) continue;
                var et = ToEt(c.Time);
                DateTime fd = et.Hour >= 18 ? et.Date.AddDays(1) : et.Date;
                DateTime key = monthly ? new DateTime(fd.Year, fd.Month, 1) : fd.AddDays(-(((int)fd.DayOfWeek + 6) % 7)).Date;
                if (!has || key != curKey)
                {
                    if (has) outl.Add(cur);
                    has = true; curKey = key;
                    cur = new DBar { Day = fd, O = (double)c.Open, H = (double)c.High, L = (double)c.Low, C = (double)c.Close };
                }
                else { if ((double)c.High > cur.H) cur.H = (double)c.High; if ((double)c.Low < cur.L) cur.L = (double)c.Low; cur.C = (double)c.Close; }
            }
            if (has) outl.Add(cur);
            return outl;
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
                    cur = new DBar { Day = et.Date, O = (double)c.Open, H = (double)c.High, L = (double)c.Low, C = (double)c.Close };
                }
                else { if ((double)c.High > cur.H) cur.H = (double)c.High; if ((double)c.Low < cur.L) cur.L = (double)c.Low; cur.C = (double)c.Close; }
            }
            if (has) outl.Add(cur);
            return outl;
        }

        // = clientEngineService.detectBOS: most recent valid bull + bear BOS.
        // Breakouts of one timeframe, CLOSED bars only (the last element is the bar in progress). A
        // breakout dies when a later bar of the SAME timeframe, in the opposite direction, closes back
        // through its level — a long BO H1 lives until a bearish H1 bar closes below it. Of the
        // survivors, the last BosKeepPerTf per timeframe are kept.
        private void DetectBosInto(List<DBar> c, string tf, List<LevelEntry> outList)
        {
            int lastClosed = c.Count - 2;
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
            for (int i = Math.Max(0, alive.Count - keep); i < alive.Count; i++) outList.Add(alive[i]);
        }

        // Triangles by timeframe (▲ H1, ▲▲ H4, ▲▲▲ D, ▲▲▲▲ W, ▲▲▲▲▲ M), then "BO L/S <tf>".
        private static string BoLabel(bool bull, string tf)
        {
            string t = bull ? "\u25B2" : "\u25BC";
            int n = tf == "M" ? 5 : tf == "W" ? 4 : tf == "D" ? 3 : tf == "H4" ? 2 : 1;
            return new string(t[0], n) + " BO " + (bull ? "L" : "S") + " " + tf;
        }

        private LevelEntry MakeBos(double chartPrice, bool bull, string tf)
        {
            return new LevelEntry
            {
                RawStrike = ChartToRaw(chartPrice),
                Type = bull ? "BL" : "BS",
                Label = BoLabel(bull, tf),
                Magnitude = 0,
                GexSign = 0,
                IsLocal = true,
                BoHigherTf = tf == "M" || tf == "W" || tf == "D",
                BoWidth = tf == "M" ? Math.Max(1, LineWidthGex) + 2 : tf == "W" ? Math.Max(1, LineWidthGex) + 1 : tf == "D" ? Math.Max(1, LineWidthGex) : Math.Max(1, LineWidthGex)
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
                GexSign = 0,
                IsLocal = true
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
            string key = $"{barCount}|{ShowBosM}{ShowBosW}{ShowBosD}{ShowBosH4}{ShowBosH1}|{BosKeepPerTf}";
            if (key == _bosComputedKey) return;
            _bosComputedKey = key;
            _localBos.Clear();
            if (ShowBosM)  DetectBosInto(AggregateByFuturesKey(barCount, true), "M", _localBos);
            if (ShowBosW)  DetectBosInto(AggregateByFuturesKey(barCount, false), "W", _localBos);
            if (ShowBosD)  DetectBosInto(AggregateByEtPeriod(barCount, 1440), "D", _localBos);
            if (ShowBosH4) DetectBosInto(AggregateByEtPeriod(barCount, 240), "H4", _localBos);
            if (ShowBosH1) DetectBosInto(AggregateByEtPeriod(barCount, 60), "H1", _localBos);
        }
        private string _bosComputedKey = "";

        // BOS levels come from native chart candles (ES/NQ) — already in chart price,
        // no spread. ChartToRaw maps to RawStrike space so the shared ConvertPrice()
        // round-trips back to the same price (ES/NQ/SPX/NDX pass-through; SPY e QQQ col rapporto da R:, 10 e 40 come ripiego).
        private double ChartToRaw(double chartPrice)
        {
            double esSp = EffectiveEsSpread, nqSp = EffectiveNqSpread;
            double ratio = _ndxQqqRatio > 0 ? _ndxQqqRatio : 40.0;
            // Lo stesso campo R: porta il rapporto della famiglia della stringa: ~41 per
            // NDX/QQQ, ~10 per SPX/SPY. La banda evita il caso incrociato — stringa NQ
            // caricata mentre si guarda SPY — in cui si applicherebbe 41 a SPY. Fuori
            // banda si ripiega su 10, che e' quello che c'era scritto fisso fino al
            // 13/10/2026 (valore vero quel giorno: 10,034; errore: 2,6 punti).
            double spyRatio = (_ndxQqqRatio >= 9.0 && _ndxQqqRatio <= 12.0) ? _ndxQqqRatio : 10.0;
            switch (ResolveTicker())
            {
                case "SPX": return esSp <= 0 ? chartPrice : chartPrice + esSp;
                case "SPY": return esSp <= 0 ? chartPrice : chartPrice * spyRatio + esSp;
                case "NDX": return nqSp <= 0 ? chartPrice : chartPrice + nqSp;
                case "QQQ": return nqSp <= 0 ? chartPrice : chartPrice * ratio + nqSp;
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
            // Strikes arrive in FUTURES points (ES / NQ). On a cash or ETF chart the spread from the
            // data string is taken off; with no spread known the strikes stay as they are (identity),
            // never shifted by a built-in number. Ratio from R:, per family: NDX→QQQ (else 40)
            // on a QQQ chart, SPX→SPY (else 10) on a SPY one.
            double cv;
            double esSp = EffectiveEsSpread, nqSp = EffectiveNqSpread;
            double ratio = _ndxQqqRatio > 0 ? _ndxQqqRatio : 40.0;
            // Lo stesso campo R: porta il rapporto della famiglia della stringa: ~41 per
            // NDX/QQQ, ~10 per SPX/SPY. La banda evita il caso incrociato — stringa NQ
            // caricata mentre si guarda SPY — in cui si applicherebbe 41 a SPY. Fuori
            // banda si ripiega su 10, che e' quello che c'era scritto fisso fino al
            // 13/10/2026 (valore vero quel giorno: 10,034; errore: 2,6 punti).
            double spyRatio = (_ndxQqqRatio >= 9.0 && _ndxQqqRatio <= 12.0) ? _ndxQqqRatio : 10.0;
            switch (ResolveTicker())
            {
                case "SPX": cv = esSp <= 0 ? rawStrike : rawStrike - esSp; break;
                case "SPY": cv = esSp <= 0 ? rawStrike : (rawStrike - esSp) / spyRatio; break;
                case "NDX": cv = nqSp <= 0 ? rawStrike : rawStrike - nqSp; break;
                case "QQQ": cv = nqSp <= 0 ? rawStrike : (rawStrike - nqSp) / ratio; break;
                default:    cv = rawStrike; break;   // ES / NQ: already in futures space
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
            public bool IsLocal;        // computed from the chart's bars (structure, breakouts), not from the feed
            public bool BoHigherTf;     // M/W/D breakout: solid, heavier
            public int BoWidth;
            public double RawStrike;   // raw SPX or NDX from cloud
            public string Type;
            public string Label;
            public double Magnitude;
            public int    GexSign;     // +1 = GEX pozitiv (dreapta), -1 = negativ (stanga), 0 = necunoscut
        }
    }
}
