// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Bridge contribution
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe Quantum Field Ladder — ATAS Edition  v3.0.1
//  ─────────────────────────────────────────────────────────────────────────────
//  v3.0.1: bugfix — strike normalization aligned with the GEX Dashboard. The
//          cloud `indicatorData` endpoint returns strikes already in ES/NQ
//          futures space, so no spread subtraction is needed.
//  v3.0.0: real OnRender pipeline (the v2.0 stacked LineSeries approach does
//          not render correctly in ATAS). Uses FillRectangle (gradient bands)
//          + DrawLine (centre line).
//
//  MAPPING (data from TLADe Cloud; only ZG / CW / PW are surfaced here):
//    ZG (Zero Gamma) → MAGNET (cyan band)
//    CW (Call Wall)  → WALL   (magenta band)
//    PW (Put Wall)   → WALL   (magenta band)
//
//  Intensity is proportional to GEX magnitude (column 5 of L:).
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net.Http;
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
        private static readonly int[] FETCH_MINUTES_ET = { 1085, 125, 485, 575, 635, 785 };

        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ATAS", "tlade_quantum.log");

        private readonly CultureInfo _inv = CultureInfo.InvariantCulture;
        private readonly object _lvlLock = new object();
        private readonly List<LevelEntry> _levels = new List<LevelEntry>();

        private string   _rawData            = "";
        private bool     _isDelayedMode      = false;
        private int      _lastFetchMinuteEt  = -1;
        private DateTime _lastFetchUtc       = DateTime.MinValue;
        private bool     _initialFetchTried  = false;

        private RenderFont _fn;
        private readonly RenderStringFormat _sfL =
            new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

        // ──────────────────────────────────────────────────────────────────────
        //  PROPERTIES
        // ──────────────────────────────────────────────────────────────────────

        [Display(Name = "API Key (gol = mode FREE)", GroupName = "1. Source", Order = 1)]
        public string ApiKey { get; set; } = "";

        [Display(Name = "Auto-fetch din TLADe Cloud", GroupName = "1. Source", Order = 2)]
        public bool AutoFetch { get; set; } = true;

        [Display(Name = "Manual data string (override)", GroupName = "1. Source", Order = 3)]
        public string GexDataInput { get; set; } = "";

        [Display(Name = "Ticker display (auto/ES/SPX/SPY/NQ/NDX/QQQ)", GroupName = "2. Layout", Order = 1)]
        public string DisplayTicker { get; set; } = "auto";

        [Display(Name = "ES↔SPX spread (auto from S:)", GroupName = "2. Layout", Order = 2)]
        [Range(0, 200)]
        public double EsSpxSpread { get; set; } = 24.0;

        [Display(Name = "NQ↔NDX spread", GroupName = "2. Layout", Order = 3)]
        [Range(0, 200)]
        public double NqNdxSpread { get; set; } = 40.0;

        [Display(Name = "Manual spread override (0 = auto)", GroupName = "2. Layout", Order = 4,
                 Description = "If > 0, forces the ES↔SPX spread to this value instead of the cloud S: header. Use a tiny positive value (e.g. 0.001) to bypass spread conversion entirely.")]
        [Range(0, 200)]
        public double ManualSpreadOverride { get; set; } = 0.0;

        [Display(Name = "Snap la tick-ul instrumentului", GroupName = "2. Layout", Order = 5)]
        public bool SnapToTick { get; set; } = true;

        [Display(Name = "Max levels (5..20)", GroupName = "3. Render", Order = 1)]
        [Range(2, 40)]
        public int MaxLevels { get; set; } = 8;

        [Display(Name = "Band width (ticks)", GroupName = "3. Render", Order = 2,
                 Description = "Latimea totala a benzii in jurul strike-ului")]
        [Range(2, 200)]
        public int BandWidthTicks { get; set; } = 12;

        [Display(Name = "Gradient layers (3..11)", GroupName = "3. Render", Order = 3,
                 Description = "How many concentric strips form the gradient")]
        [Range(3, 11)]
        public int GradientLayers { get; set; } = 5;

        [Display(Name = "Center line width", GroupName = "3. Render", Order = 4)]
        [Range(1, 5)]
        public int CenterLineWidth { get; set; } = 2;

        [Display(Name = "Benzi sub lumanari (chart in prim-plan)", GroupName = "3. Render", Order = 4,
                 Description = "Default ON: gradient bands render BEHIND candles, so price stays readable. OFF: bands cover candles (Quantum in foreground). Centre line + label always stay on top.")]
        public bool BandsBehindCandles { get; set; } = true;

        [Display(Name = "Show labels", GroupName = "3. Render", Order = 5)]
        public bool ShowLabels { get; set; } = true;

        [Display(Name = "Show status badge", GroupName = "3. Render", Order = 6)]
        public bool ShowStatus { get; set; } = true;

        [Display(Name = "Font size label", GroupName = "3. Render", Order = 7)]
        [Range(7, 16)]
        public int LabelFontSize { get; set; } = 9;

        [Display(Name = "Magnet color (ZG)", GroupName = "4. Colors", Order = 1)]
        public Color MagnetColor { get; set; } = Color.FromRgb(0x22, 0xd3, 0xee); // cyan

        [Display(Name = "Wall color (CW/PW)", GroupName = "4. Colors", Order = 2)]
        public Color WallColor { get; set; } = Color.FromRgb(0xec, 0x4d, 0xc4); // magenta

        [Display(Name = "Label text inherits band color", GroupName = "4. Colors", Order = 3,
                 Description = "Default ON. Uncheck to force a custom label text color.")]
        public bool LabelInheritBandColor { get; set; } = true;

        [Display(Name = "Label text color (when not inheriting)", GroupName = "4. Colors", Order = 4)]
        public Color LabelTextColor { get; set; } = Colors.White;

        [Display(Name = "Label background opacity (0-255)", GroupName = "4. Colors", Order = 5)]
        [Range(0, 255)]
        public int LabelBgAlpha { get; set; } = 170;

        [Display(Name = "Fundal label diferentiat above/below spot", GroupName = "4. Colors", Order = 6)]
        public bool LabelBgPositional { get; set; } = false;

        [Display(Name = "Fundal label - above spot", GroupName = "4. Colors", Order = 7)]
        public Color LabelBgAbove { get; set; } = Color.FromRgb(0xdc, 0x26, 0x26);

        [Display(Name = "Fundal label - below spot", GroupName = "4. Colors", Order = 8)]
        public Color LabelBgBelow { get; set; } = Color.FromRgb(0x16, 0xa3, 0x4a);

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
                    Log($"OnCalc #{_ocCalls} (live) bar={bar} CurrentBar={CurrentBar} initialFetched={_initialFetchTried}");

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

                if (!AutoFetch) return;

                if (!_initialFetchTried)
                {
                    _initialFetchTried = true;
                    Log($"OnCalc triggering initial fetch on bar={bar}");
                    Task.Run(() => FetchOnce());
                    return;
                }
                TryScheduledFetch();
            }
            catch (Exception ex)
            {
                Log($"OnCalculate EX: {ex.GetType().Name}: {ex.Message}");
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
            int pPos = raw.IndexOf("|P:", StringComparison.Ordinal);
            if (pPos >= 0)
            {
                var before = raw.Substring(0, pPos);
                if (before.StartsWith("L:", StringComparison.Ordinal))
                    levelsData = before.Substring(2);
            }
            else if (raw.StartsWith("L:", StringComparison.Ordinal))
            {
                levelsData = raw.Substring(2);
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
            lock (_lvlLock) { _levels.Clear(); _levels.AddRange(fresh); }
            Log($"PARSED levels={fresh.Count}");
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

                // Rank by distance from spot, take top N
                var ranked = new List<RankedLevel>(snap.Count);
                foreach (var lvl in snap)
                {
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
                    bool isMagnet = lvl.Type == "ZG";
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
            }
            catch (Exception ex)
            {
                Log($"OnRender EX: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void DrawStatus(RenderContext ctx, int count)
        {
            try
            {
                string mode = _isDelayedMode ? "FREE" : "LIVE";
                string txt = $" TLADe Quantum [{mode}] levels={count}";
                var sz = ctx.MeasureString(txt, _fn);
                int x = 4, y = 22;
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
            // TLADe cloud `indicatorData` returns strikes already in the futures
            // price space (ES for SPX family, NQ for NDX family) — the spread is
            // applied upstream when the snapshot is built. ETF proxies (SPY/QQQ)
            // still need the conventional multiplier divide.
            double cv;
            switch (ResolveTicker())
            {
                case "SPX": cv = rawStrike; break;
                case "SPY": cv = rawStrike / 10.0; break;
                case "ES":  cv = rawStrike; break;        // already ES futures space
                case "NDX": cv = rawStrike; break;
                case "QQQ": cv = rawStrike / 40.0; break;
                case "NQ":  cv = rawStrike; break;        // already NQ futures space
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
