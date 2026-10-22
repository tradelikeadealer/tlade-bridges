// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Quantum Field Ladder contribution
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe Quantum Field Ladder — ATAS Edition  v3.0.1 — bundle release 3.6.0 (22 October 2026)
//  ─────────────────────────────────────────────────────────────────────────────
//  Mihai's v3.0.0 with a TLADe-side fix on top: strikes for ES/NQ are passed
//  through raw because the cloud already publishes them in futures space —
//  subtracting the ES↔SPX spread again left levels ~19 points off vs the
//  TLADe terminal. SPX/NDX/SPY/QQQ paths unchanged.
//
//  v3.0.0 (Mihai): REAL OnRender — v2.0 stacked LineSeries didn't render in
//          ATAS. Uses FillRectangle (gradient bands) + DrawLine (center).
//
//  MAPPING (data from TLADe Cloud, only ZG/CW/PW returned):
//    ZG (Zero Gamma) → MAGNET (cyan band)
//    CW (Call Wall)  → WALL   (magenta band)
//    PW (Put Wall)   → WALL   (magenta band)
//
//  Intensity proportional to GEX magnitude (col. 5 from L:).
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
#if ATASX
// ATAS X e' Avalonia, non WPF: i colori delle impostazioni sono System.Drawing.Color.
// Un sorgente solo per le due build, la differenza e' questa costante. (README_ATASX_EN.md)
using Color = System.Drawing.Color;
#else
using System.Windows.Media;
#endif
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Settings;
using OFT.Rendering.Tools;
using DColor = System.Drawing.Color;
using DRect = System.Drawing.Rectangle;
using StringAlignment = System.Drawing.StringAlignment;

namespace ATAS.Indicators.Technical
{
    [DisplayName("TLADe Quantum Field Ladder")]
    [Description("GEX force fields — gradient bands (OnRender native)")]
    public class TLAdeQuantumFieldLadder : Indicator
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
        // 3.6.0 — publication age and catch-up fetch: TLAdePublication.cs (shared with the Dashboard).
        private string _pubDateId = "", _pubSession = "", _pubHhmm = "";   // from T:; empty when the string has none
        private bool _staleDrawn = false;
        private readonly TLAdeCatchUp _catchUp = new TLAdeCatchUp();
        private CancellationTokenSource _schedulerCts;

        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS", "tlade_quantum.log");

#if ATASX
        private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(r, g, b);
        private static class Named
        {
            public static Color White  => Color.White;
            public static Color Black  => Color.Black;
            public static Color Orange => Color.Orange;
        }
#else
        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
        private static class Named
        {
            public static Color White  => Colors.White;
            public static Color Black  => Colors.Black;
            public static Color Orange => Colors.Orange;
        }
#endif

        private readonly CultureInfo _inv = CultureInfo.InvariantCulture;
        private readonly object _lvlLock = new object();
        private readonly List<LevelEntry> _levels = new List<LevelEntry>();

        private string   _rawData            = "";
        private bool     _isDelayedMode      = false;
        private double   _etfRatio           = 0.0;   // da R: — rapporto indice->ETF

        // Quali strike sono MAGNETI: arriva dalla riga B: del profilo, deciso dal motore.
        // La Ladder NON lo ricalcola: ENGINE_CORRECTIONS_LOG #009, "mai inferire".
        private readonly HashSet<int> _magnets = new HashSet<int>();

        // Colori e stili dei livelli di sistema, identici alla GEX Dashboard (sue costanti).
        private static readonly DColor C_MP = DColor.FromArgb(0xef, 0x44, 0x44),
            C_EM    = DColor.FromArgb(0x3b, 0x82, 0xf6),
            C_VB    = DColor.FromArgb(0x9c, 0xa3, 0xaf),
            C_CHARM = DColor.FromArgb(0xf9, 0x73, 0x16),
            C_DFLIP = DColor.FromArgb(0xf5, 0x9e, 0x0b);
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

        [Display(Name = "Manual data string (override)", GroupName = "1. Source", Order = 3)]
        public string GexDataInput { get; set; } = "";

        [Display(Name = "Ticker display (auto/ES/SPX/SPY/NQ/NDX/QQQ)", GroupName = "2. Layout", Order = 1)]
        public string DisplayTicker { get; set; } = "auto";

        [Display(Name = "ES↔SPX spread (auto from S:)", GroupName = "2. Layout", Order = 2)]
        [Range(0, 200)]
        public double EsSpxSpread { get; set; } = 0.0;   // 0 = non noto: strike come sono finché non arriva S:

        [Display(Name = "NQ↔NDX spread", GroupName = "2. Layout", Order = 3)]
        [Range(0, 200)]
        public double NqNdxSpread { get; set; } = 40.0;

        [Display(Name = "Manual spread override (0 = auto)", GroupName = "2. Layout", Order = 4,
                 Description = "If > 0, forces the ES↔SPX spread to this value instead of S: from cloud.")]
        [Range(0, 200)]
        public double ManualSpreadOverride { get; set; } = 0.0;

        [Display(Name = "Snap to instrument tick", GroupName = "2. Layout", Order = 5)]
        public bool SnapToTick { get; set; } = true;

        [Display(Name = "Max levels (5..20)", GroupName = "3. Render", Order = 1)]
        [Range(2, 40)]
        public int MaxLevels { get; set; } = 8;

        [Display(Name = "Band width (ticks)", GroupName = "3. Render", Order = 2,
                 Description = "Total band width around the strike")]
        [Range(2, 200)]
        public int BandWidthTicks { get; set; } = 12;

        [Display(Name = "Gradient layers (3..11)", GroupName = "3. Render", Order = 3,
                 Description = "How many concentric strips form the gradient")]
        [Range(3, 11)]
        public int GradientLayers { get; set; } = 5;

        [Display(Name = "Center line width", GroupName = "3. Render", Order = 4)]
        [Range(1, 5)]
        public int CenterLineWidth { get; set; } = 2;

        [Display(Name = "Bands behind candles (chart in foreground)", GroupName = "3. Render", Order = 4,
                 Description = "Default ON: gradient bands render BEHIND candles, so you see price clearly. OFF: bands cover candles (Quantum in foreground). Center line + label always stay on top.")]
        public bool BandsBehindCandles { get; set; } = true;

        [Display(Name = "Show labels", GroupName = "3. Render", Order = 5)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Show status badge", GroupName = "3. Render", Order = 6)]
        public bool ShowStatus { get; set; } = true;

        [Display(Name = "Font size label", GroupName = "3. Render", Order = 7)]
        [Range(7, 16)]
        public int LabelFontSize { get; set; } = 9;

        // Lo Zero Gamma e' bianco di default su ogni superficie, e il colore si cambia da qui:
        // chi lavora su sfondo chiaro se lo mette grigio da se'. (Stefano, 14/10/2026)
        [Display(Name = "Zero Gamma color", GroupName = "4. Colors", Order = 0)]
        public Color ZeroGammaColor { get; set; } = Rgb(0xff, 0xff, 0xff);

        // MP / EM / Vol Band / DF / CM non sono ne' magneti ne' muri: non sono campo. Se accesi,
        // si disegnano come linee con gli stessi colori e stili della GEX Dashboard. Spenti di
        // default, perche' la Ladder e' il campo quantistico e basta. (Stefano, 14/10/2026)
        [Display(Name = "Show system levels as lines (MP/EM/VB/DF/CM)", GroupName = "3. Render", Order = 8)]
        public bool ShowSystemLines { get; set; } = false;

        [Display(Name = "Magnet color (ZG)", GroupName = "4. Colors", Order = 1)]
        public Color MagnetColor { get; set; } = Rgb(0x22, 0xd3, 0xee); // cyan

        [Display(Name = "Wall color (CW/PW)", GroupName = "4. Colors", Order = 2)]
        public Color WallColor { get; set; } = Rgb(0xec, 0x4d, 0xc4); // magenta

        [Display(Name = "Label text inherits band color", GroupName = "4. Colors", Order = 3,
                 Description = "Default ON. Uncheck to force a custom label text color.")]
        public bool LabelInheritBandColor { get; set; } = true;

        [Display(Name = "Label text color (when not inheriting)", GroupName = "4. Colors", Order = 4)]
        public Color LabelTextColor { get; set; } = Named.White;

        [Display(Name = "Label background opacity (0-255)", GroupName = "4. Colors", Order = 5)]
        [Range(0, 255)]
        public int LabelBgAlpha { get; set; } = 170;

        [Display(Name = "Label background differs above/below spot", GroupName = "4. Colors", Order = 6)]
        public bool LabelBgPositional { get; set; } = false;

        [Display(Name = "Label background - above spot", GroupName = "4. Colors", Order = 7)]
        public Color LabelBgAbove { get; set; } = Rgb(0xdc, 0x26, 0x26);

        [Display(Name = "Label background - below spot", GroupName = "4. Colors", Order = 8)]
        public Color LabelBgBelow { get; set; } = Rgb(0x16, 0xa3, 0x4a);

        // ──────────────────────────────────────────────────────────────────────
        //  CONSTRUCTOR
        // ──────────────────────────────────────────────────────────────────────

        public TLAdeQuantumFieldLadder() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
            // Subscribe to BOTH layers so bands can render under candles (Historical) AND
            // center lines/labels can render on top (Final).
            SubscribeToDrawingEvents(DrawingLayouts.Historical | DrawingLayouts.Final);
            Panel = IndicatorDataProvider.CandlesPanel;
            DataSeries[0].IsHidden = true;
            TryLoadDefaultApiKey();
            Log($"CTOR 3.5.3 instance hash={GetHashCode()} apiKeySet={!string.IsNullOrEmpty(ApiKey)}");
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
        //  ONCALCULATE — drives fetch only
        // ──────────────────────────────────────────────────────────────────────

        private int _ocCalls = 0;
        protected override void OnCalculate(int bar, decimal value)
        {
            try
            {
                _ocCalls++;
                if (bar != CurrentBar - 1) return;

                if (_ocCalls <= 3 || _ocCalls % 2000 == 0)
                    Log($"OnCalc #{_ocCalls} (live) bar={bar} CurrentBar={CurrentBar}");

                if (!string.IsNullOrEmpty(GexDataInput))
                {
                    if (GexDataInput != _rawData)
                    {
                        _rawData = GexDataInput;
                        ParseData(_rawData);
                        RedrawChart();
                    }
                    return;
                }

                // Fetch lifecycle is owned by the scheduler (OnInitialize → RunScheduler), as in
                // the Dashboard since v3.1. OnCalculate only handles the manual-paste path above.
            }
            catch (Exception ex)
            {
                Log($"OnCalculate EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  LIFECYCLE — wall-clock scheduler (3.6.0, = the Dashboard's)
        // ──────────────────────────────────────────────────────────────────────

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Log($"OnInitialize: starting scheduler (autoFetch={AutoFetch})");
            if (!AutoFetch) return;
            _schedulerCts?.Cancel();
            _schedulerCts = new CancellationTokenSource();
            // 1 immediate fetch at mount, then a wall-clock check every minute. Independent from
            // the OnCalculate tick rate — fires even when the chart receives zero ticks (cash
            // index out of RTH, futures quiet hours, weekend, PC back from sleep). Until 3.5 the
            // Ladder fetched from OnCalculate inside six fixed ET slots: a quiet chart missed them.
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
                Log("Scheduler: mount fetch");
                await FetchOnce().ConfigureAwait(false);
                _catchUp.MarkFetched(TLAdePublication.ExpectedKey(TLAdePublication.EtNow()));

                while (!ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                    if (ct.IsCancellationRequested) return;
                    if (!AutoFetch) continue;
                    // A pasted string owns the chart (OnCalculate re-parses it over any fetch):
                    // fetching behind it would only make the levels flip between the two.
                    bool manual = !string.IsNullOrEmpty(GexDataInput);
                    bool live = !string.IsNullOrEmpty(ApiKey);
                    long expected = TLAdePublication.ExpectedKey(TLAdePublication.EtNow());
                    long held = TLAdePublication.Key(_pubDateId, _pubSession);
                    if (!manual && _catchUp.Due(live, held, expected))
                    {
                        Log($"Scheduler: catch-up fetch (held {_pubDateId} {_pubSession}, expected key {expected})");
                        await FetchOnce().ConfigureAwait(false);
                    }
                    else if (TLAdePublication.IsStale(_isDelayedMode, _pubDateId, _pubSession, out _, out _) != _staleDrawn)
                    {
                        // Nothing new, but the clock moved: the STALE badge must appear or go.
                        RedrawChart();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Scheduler EX: {ex.GetType().Name}: {ex.Message}");
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
                req.Headers.Add("User-Agent", "TLADe-ATAS-Quantum/3.0");
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
            }
            catch (Exception ex)
            {
                Log($"FetchOnce ERR: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  PARSE
        // ──────────────────────────────────────────────────────────────────────

        private void ParseData(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                lock (_lvlLock) { _levels.Clear(); _magnets.Clear(); }
                _pubDateId = ""; _pubSession = ""; _pubHhmm = "";
                Log("PARSED cleared (empty input)");
                return;
            }
            var fresh = new List<LevelEntry>();

            raw = raw.Replace("\r", "").Replace("\n", "").Trim();
            TLAdePublication.ParseMarker(raw, out _pubDateId, out _pubSession, out _pubHhmm);

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

            // R: — rapporto indice->ETF (SPX->SPY ~10, NDX->QQQ ~41). Va tolto QUI, prima
            // del test su "L:". Dal 12/10/2026 il payload lo porta sempre: senza questo
            // blocco la sezione dei livelli comincia con "R:...", StartsWith("L:") fallisce
            // e la Ladder resta vuota senza un errore. La Dashboard lo fa dal 3.5.0, qui
            // era rimasto fuori. (14/10/2026)
            if (raw.StartsWith("R:", StringComparison.Ordinal))
            {
                int rEnd = raw.IndexOf('|');
                if (rEnd > 2)
                {
                    if (double.TryParse(raw.Substring(2, rEnd - 2), NumberStyles.Float, _inv, out var rr) && rr > 0)
                        _etfRatio = rr;
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
                profileData = raw.Substring(pPos + 3);
            }
            else if (raw.StartsWith("L:", StringComparison.Ordinal))
            {
                levelsData = raw.Substring(2);
            }

            // B: — gli strike che il motore ha dichiarato MAGNETI. Viaggia come riga del
            // profilo (come T:) perche' in testa alla stringa le piattaforme pretendono "S:",
            // e ogni parser scarta una riga di profilo il cui primo campo non e' un numero.
            // La Ladder non decide: legge. (14/10/2026)
            var freshMagnets = new HashSet<int>();
            if (profileData.Length > 0)
            {
                foreach (var row in profileData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!row.StartsWith("B:", StringComparison.Ordinal)) continue;
                    int comma = row.IndexOf(',');
                    var list = comma > 2 ? row.Substring(2, comma - 2) : row.Substring(2);
                    foreach (var tok in list.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(tok, NumberStyles.Integer, _inv, out var mk)) freshMagnets.Add(mk);
                    break;
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
                    fresh.Add(new LevelEntry
                    {
                        RawStrike = strike,
                        Type      = type,
                        Label     = label,
                        Magnitude = Math.Abs(mag),
                    });
                }
            }
            if (fresh.Count == 0)
            {
                Log($"PARSED skipped: 0 levels from {raw.Length} chars (input incomplete/invalid) — keeping existing");
                return;
            }
            lock (_lvlLock)
            {
                _levels.Clear(); _levels.AddRange(fresh);
                _magnets.Clear(); foreach (var mk in freshMagnets) _magnets.Add(mk);
            }
            Log($"PARSED levels={fresh.Count} magnets={freshMagnets.Count}");
        }

        // ──────────────────────────────────────────────────────────────────────
        //  ONRENDER — gradient bands via FillRectangle
        // ──────────────────────────────────────────────────────────────────────

        protected override void OnRender(RenderContext ctx, DrawingLayouts layout)
        {
            try
            {
                if (layout != DrawingLayouts.Historical && layout != DrawingLayouts.Final) return;
                if (ChartInfo == null) return;

                // Determine which pass we're doing:
                //   bandsPass = the layer where we draw filled gradient bands
                //   topPass   = the layer where we draw center line + label (always Final)
                bool wantBandsHere = (BandsBehindCandles && layout == DrawingLayouts.Historical)
                                  || (!BandsBehindCandles && layout == DrawingLayouts.Final);
                bool wantTopHere   = layout == DrawingLayouts.Final;
                if (!wantBandsHere && !wantTopHere) return;

                EnsureFont();

                List<LevelEntry> snap;
                lock (_lvlLock) snap = new List<LevelEntry>(_levels);

                // Status badge only on Final pass
                if (ShowStatus && layout == DrawingLayouts.Final)
                    DrawStatus(ctx, snap.Count);

                if (snap.Count == 0) return;

                int regionW = ChartInfo.PriceChartContainer.Region.Width;
                int x1 = 0, x2 = regionW;

                double spot = GetLiveSpot();
                if (spot <= 0) return;

                // Tre secchi. Il campo quantistico sono i MURI: sono loro a contare per
                // "Max levels", che altrimenti veniva consumato da Max Pain e vol band.
                // Lo ZG non e' un muro ed e' sempre visibile, i livelli di riferimento
                // dipendono dal flag. (14/10/2026)
                LevelEntry zgLvl = default; bool hasZg = false;
                var sysLvls = new List<LevelEntry>();
                var ranked  = new List<RankedLevel>(snap.Count);
                foreach (var lvl in snap)
                {
                    var t = lvl.Type ?? "";
                    if (IsNotAPrice(t)) continue;
                    if (t == "ZG") { zgLvl = lvl; hasZg = true; continue; }
                    if (IsSystemLine(t)) { sysLvls.Add(lvl); continue; }
                    var y = ConvertPrice(lvl.RawStrike);
                    ranked.Add(new RankedLevel { Lvl = lvl, ConvertedY = y, DistToSpot = Math.Abs(y - spot) });
                }
                ranked.Sort((a, b) => a.DistToSpot.CompareTo(b.DistToSpot));

                int take = Math.Min(MaxLevels, ranked.Count);
                double maxMag = 1e-9;
                for (int i = 0; i < take; i++)
                    if (ranked[i].Lvl.Magnitude > maxMag) maxMag = ranked[i].Lvl.Magnitude;

                decimal tickSize = InstrumentInfo?.TickSize ?? 0.25m;
                double halfBandPrice = (double)tickSize * BandWidthTicks / 2.0;

                int layers = GradientLayers;
                if (layers < 3) layers = 3;
                if (layers > 11) layers = 11;
                if (layers % 2 == 0) layers++;

                for (int i = 0; i < take; i++)
                {
                    var rl = ranked[i];
                    var lvl = rl.Lvl;
                    bool isMagnet;
                    lock (_lvlLock) isMagnet = _magnets.Contains((int)Math.Round(lvl.RawStrike));
                    Color baseWpf = isMagnet ? MagnetColor : WallColor;
                    DColor baseC = DColor.FromArgb(baseWpf.A, baseWpf.R, baseWpf.G, baseWpf.B);

                    double intensity = lvl.Magnitude > 0
                        ? Math.Min(1.0, lvl.Magnitude / maxMag)
                        : 0.6;

                    // ── BANDS pass (Historical or Final depending on BandsBehindCandles) ──
                    if (wantBandsHere)
                    {
                        int halfLayers = layers / 2;
                        for (int k = halfLayers; k >= 0; k--)
                        {
                            double layerFrac = 1.0 - (double)k / halfLayers; // center=1.0, edge=0.0
                            double alphaFraction = isMagnet
                                ? Math.Max(0.05, layerFrac * 0.55)
                                : Math.Max(0.05, layerFrac * 0.65 * (0.4 + 0.6 * intensity));
                            byte alpha = (byte)Math.Round(255 * Math.Min(0.85, alphaFraction));
                            var col = DColor.FromArgb(alpha, baseC.R, baseC.G, baseC.B);

                            double layerHalf = halfBandPrice * ((double)(k + 1) / (halfLayers + 1));
                            decimal yPriceHi = (decimal)(rl.ConvertedY + layerHalf);
                            decimal yPriceLo = (decimal)(rl.ConvertedY - layerHalf);
                            int yHi = ChartInfo.GetYByPrice(yPriceHi, false);
                            int yLo = ChartInfo.GetYByPrice(yPriceLo, false);
                            int top = Math.Min(yHi, yLo);
                            int h = Math.Max(1, Math.Abs(yLo - yHi));

                            ctx.FillRectangle(col, new DRect(x1, top, x2 - x1, h));
                        }
                    }

                    // ── TOP pass (always Final): center line + label ──
                    if (!wantTopHere) continue;

                    int yCenter = ChartInfo.GetYByPrice((decimal)rl.ConvertedY, false);
                    var centerPen = new RenderPen(baseC, CenterLineWidth);
                    ctx.DrawLine(centerPen, x1, yCenter, x2, yCenter);

                    if (ShowLabels)
                    {
                        string txt = $" {FormatPrice(lvl.RawStrike)} {(isMagnet ? "MAGNET" : "WALL")} ";
                        var sz = ctx.MeasureString(txt, _fn);
                        int lx = x2 - (int)sz.Width - 6;
                        int ly = yCenter - (int)sz.Height - 1;
                        byte ba = (byte)Math.Clamp(LabelBgAlpha, 0, 255);
                        bool bandAbove = rl.ConvertedY > spot;
                        DColor bg;
                        if (LabelBgPositional)
                        {
                            var src = bandAbove ? LabelBgAbove : LabelBgBelow;
                            bg = DColor.FromArgb(ba, src.R, src.G, src.B);
                        }
                        else
                        {
                            bg = DColor.FromArgb(ba,
                                Math.Min(baseC.R / 4 + 6, 255),
                                Math.Min(baseC.G / 4 + 6, 255),
                                Math.Min(baseC.B / 4 + 6, 255));
                        }
                        var textCol = LabelInheritBandColor
                            ? baseC
                            : DColor.FromArgb(LabelTextColor.A, LabelTextColor.R, LabelTextColor.G, LabelTextColor.B);
                        ctx.FillRectangle(bg, new DRect(lx - 2, ly - 1, (int)sz.Width + 6, (int)sz.Height + 2));
                        ctx.DrawString(txt, _fn, textCol,
                            new DRect(lx, ly, (int)sz.Width + 4, (int)sz.Height), _sfL);
                    }
                }

                // ── Linee di riferimento (solo passata Final) ──
                // Lo Zero Gamma si disegna SEMPRE: e' il perno del campo, non un livello fra
                // gli altri, e non sta dietro un interruttore. Il resto dipende dal flag.
                if (wantTopHere)
                {
                    if (hasZg)
                    {
                        var zgCol = DColor.FromArgb(ZeroGammaColor.A, ZeroGammaColor.R, ZeroGammaColor.G, ZeroGammaColor.B);
                        DrawRefLine(ctx, x1, x2, ConvertPrice(zgLvl.RawStrike), zgCol,
                                    UserLineStyleOrSolid(), SystemName("ZG"), zgLvl.RawStrike);
                    }
                    if (ShowSystemLines)
                    {
                        foreach (var lvl in sysLvls)
                        {
                            var t = lvl.Type ?? "";
                            DrawRefLine(ctx, x1, x2, ConvertPrice(lvl.RawStrike), SystemColor(t),
                                        SystemDash(t), SystemName(t), lvl.RawStrike);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OnRender EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Lo stile della linea dello ZG segue quello scelto dall'utente per le linee centrali;
        // in mancanza, continua. Le linee di sistema hanno i loro stili fissi, come la Dashboard.
        private System.Drawing.Drawing2D.DashStyle UserLineStyleOrSolid()
            => System.Drawing.Drawing2D.DashStyle.Solid;

        // Una linea di riferimento: riga piena sul grafico + etichetta nel colore della riga,
        // come fa la GEX Dashboard. Non e' una banda: questi livelli non sono campo.
        private void DrawRefLine(RenderContext ctx, int x1, int x2, double convertedY,
                                 DColor col, System.Drawing.Drawing2D.DashStyle dash,
                                 string name, double rawStrike)
        {
            int y = ChartInfo.GetYByPrice((decimal)convertedY, false);
            ctx.DrawLine(new RenderPen(col, CenterLineWidth, dash), x1, y, x2, y);
            if (!ShowLabels) return;
            string txt = $" {FormatPrice(rawStrike)} {name} ";
            var sz = ctx.MeasureString(txt, _fn);
            int lx = x2 - (int)sz.Width - 6;
            int ly = y - (int)sz.Height - 1;
            byte ba = (byte)Math.Clamp(LabelBgAlpha, 0, 255);
            var bg = DColor.FromArgb(ba,
                Math.Min(col.R / 4 + 6, 255), Math.Min(col.G / 4 + 6, 255), Math.Min(col.B / 4 + 6, 255));
            ctx.FillRectangle(bg, new DRect(lx - 2, ly - 1, (int)sz.Width + 6, (int)sz.Height + 2));
            ctx.DrawString(txt, _fn, col, new DRect(lx, ly, (int)sz.Width + 4, (int)sz.Height), _sfL);
        }

        private void DrawStatus(RenderContext ctx, int count)
        {
            try
            {
                string mode = _isDelayedMode ? "FREE" : "LIVE";
                bool stale = false;
                string age = string.IsNullOrEmpty(_rawData)
                    ? "no data yet"
                    : TLAdePublication.Label(_isDelayedMode, _pubDateId, _pubSession, _pubHhmm,
                        AutoFetch && !string.IsNullOrEmpty(ApiKey) && string.IsNullOrEmpty(GexDataInput), out stale);
                _staleDrawn = stale;
                string txt = $" TLADe Quantum [{mode}] levels={count} · {age}";
                var sz = ctx.MeasureString(txt, _fn);
                int x = 4, y = 22;
                var bg = stale ? DColor.FromArgb(220, 60, 10, 10) : DColor.FromArgb(180, 12, 14, 22);
                ctx.FillRectangle(bg, new DRect(x, y, (int)sz.Width + 8, (int)sz.Height + 4));
                ctx.DrawString(txt, _fn, stale ? DColor.FromArgb(0xef, 0x44, 0x44) : DColor.Gainsboro,
                    new DRect(x + 4, y + 2, (int)sz.Width + 4, (int)sz.Height + 2), _sfL);
            }
            catch { }
        }

        private void EnsureFont()
        {
            if (_fn == null || _fn.Size != LabelFontSize)
                _fn = new RenderFont("Arial", LabelFontSize);
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

        // Livelli di riferimento: non sono campo quantistico. Si disegnano come linee, con i
        // colori e gli stili della GEX Dashboard, solo se ShowSystemLines e' acceso.
        private static bool IsSystemLine(string t) =>
            t == "MP" || t == "EH" || t == "EL" || t == "EHR" || t == "ELR" ||
            t == "VH" || t == "VL" || t == "DF" || t == "CM";

        // EMR non e' un prezzo: e' la LARGHEZZA dell'Expected Move RTH, e va ancorata
        // all'apertura delle 09:30 dall'utente. Disegnarla come livello mette una riga
        // a sessanta punti dal nulla. Fuori da tutto. (14/10/2026)
        private static bool IsNotAPrice(string t) => t == "EMR";

        private static DColor SystemColor(string t)
        {
            if (t == "MP") return C_MP;
            if (t == "EH" || t == "EL" || t == "EHR" || t == "ELR") return C_EM;
            if (t == "VH" || t == "VL") return C_VB;
            if (t == "CM") return C_CHARM;
            if (t == "DF") return C_DFLIP;
            return C_VB;
        }

        // Stessi stili della Dashboard (suo StyleFor): CM continuo, MP e vol band punteggiati,
        // EM e Delta Flip tratteggiati.
        private static System.Drawing.Drawing2D.DashStyle SystemDash(string t)
        {
            if (t == "CM") return System.Drawing.Drawing2D.DashStyle.Solid;
            if (t == "MP" || t == "VH" || t == "VL") return System.Drawing.Drawing2D.DashStyle.Dot;
            if (t == "EH" || t == "EL" || t == "EHR" || t == "ELR" || t == "DF") return System.Drawing.Drawing2D.DashStyle.Dash;
            return System.Drawing.Drawing2D.DashStyle.Solid;
        }

        // Nomi identici a quelli della Dashboard (suo NameFor).
        private static string SystemName(string t)
        {
            switch (t)
            {
                case "ZG":  return "ZERO GAMMA";
                case "MP":  return "Max Pain";
                case "EH":  return "EM High Globex";
                case "EL":  return "EM Low Globex";
                case "EHR": return "EM High RTH";
                case "ELR": return "EM Low RTH";
                case "VH":  return "Vol High";
                case "VL":  return "Vol Low";
                case "CM":  return "Charm Magnet";
                case "DF":  return "Delta Flip";
                default:    return t;
            }
        }

        private bool IsNqFamily()
        {
            var t = (DisplayTicker ?? "").ToUpperInvariant();
            if (t == "NQ" || t == "NDX" || t == "QQQ") return true;
            if (t == "ES" || t == "SPX" || t == "SPY") return false;
            var inst = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
            return inst.Contains("NQ") || inst.Contains("NDX") || inst.Contains("MNQ");
        }

        private string ResolveTicker()
        {
            var t = (DisplayTicker ?? "").ToUpperInvariant();
            if (t == "ES" || t == "SPX" || t == "SPY" || t == "NQ" || t == "NDX" || t == "QQQ")
                return t;
            var inst = (InstrumentInfo?.Instrument ?? "").ToUpperInvariant();
            if (inst.Contains("NQ") || inst.Contains("NDX") || inst.Contains("MNQ")) return "NQ";
            return "ES";
        }

        private double EffectiveEsSpread =>
            ManualSpreadOverride > 0 ? ManualSpreadOverride : EsSpxSpread;

        private double ConvertPrice(double rawStrike)
        {
            // The TLADe cloud publishes ES/NQ strikes ALREADY in futures space
            // (the ES↔SPX spread is applied server-side before the snapshot is
            // written). Pass the raw strike through for futures tickers; subtracting
            // the spread again would offset levels by ~19 points vs the TLADe terminal.
            double cv;
            // Lo spread arriva da S: e porta la base della famiglia della stringa
            // (ES-SPX o NQ-NDX). Il rapporto arriva da R:. Fino al 14/10/2026 qui
            // c'erano 10 e 40 scritti nel codice e lo spread non veniva sottratto:
            // SPX e NDX uscivano di uno spread intero, SPY di 7,5 punti e QQQ di 26.
            double sp       = EffectiveEsSpread;
            double ratio    = _etfRatio > 0 ? _etfRatio : 40.0;
            // La banda evita il caso incrociato - stringa NQ caricata mentre si guarda
            // SPY, dove si applicherebbe 41 a SPY. Fuori banda si ripiega su 10.
            double spyRatio = (_etfRatio >= 9.0 && _etfRatio <= 12.0) ? _etfRatio : 10.0;
            switch (ResolveTicker())
            {
                case "SPX": cv = sp <= 0 ? rawStrike : rawStrike - sp; break;
                case "SPY": cv = sp <= 0 ? rawStrike : (rawStrike - sp) / spyRatio; break;
                case "ES":  cv = rawStrike; break;                          // already in ES futures space
                case "NDX": cv = sp <= 0 ? rawStrike : rawStrike - sp; break;
                case "QQQ": cv = sp <= 0 ? rawStrike : (rawStrike - sp) / ratio; break;
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
            return cv.ToString("0.00", _inv);
        }

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [TLAdeQuantum] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        // ──────────────────────────────────────────────────────────────────────
        //  STRUCTS
        // ──────────────────────────────────────────────────────────────────────

        private struct LevelEntry
        {
            public double RawStrike;
            public string Type;
            public string Label;
            public double Magnitude;
        }

        private class RankedLevel
        {
            public LevelEntry Lvl;
            public double ConvertedY;
            public double DistToSpot;
        }
    }
}
