// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 Mihai Ostafe — TLADe ATAS Bridge contribution
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe GEX Dashboard — ATAS Edition  v3.0.1
//  ─────────────────────────────────────────────────────────────────────────────
//  v3.0.1: bugfix — ES/NQ strike normalization. The cloud `indicatorData`
//          endpoint already returns strikes in ES/NQ futures space (post-spread),
//          so the previous SPX→ES subtraction was double-applying the spread.
//          Levels are now used as-is from the cloud.
//  v3.0.0: real OnRender pipeline (the v2.0 dynamic LineSeries approach does not
//          render correctly inside ATAS). Pattern: EnableCustomDrawing +
//          SubscribeToDrawingEvents in the constructor, then
//          OnRender(RenderContext, DrawingLayouts).
//
//  ARCHITECTURE:
//    1. Auto-fetch from TLADe Cloud Functions (X-API-Key header, mode=free fallback).
//    2. 6 fetch windows per ET day.
//    3. Manual paste fallback (GexDataInput).
//    4. Parse "S:|L:|P:" string -> _levels.
//    5. OnRender draws horizontal lines + labels + axis markers.
//
//  TICKER AUTO-DETECT:
//    If DisplayTicker="ES" but the chart is NQ/MNQ -> automatic NDX fetch.
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
    [Description("GEX Walls + System Levels (auto-fetched from TLADe Cloud, native OnRender)")]
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
        private static readonly int[] FETCH_MINUTES_ET = { 1085, 125, 485, 575, 635, 785 };

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

        private RenderFont _fn;
        private readonly RenderStringFormat _sfL =
            new() { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near };

        // ──────────────────────────────────────────────────────────────────────
        //  PROPERTIES
        // ──────────────────────────────────────────────────────────────────────

        [Display(Name = "API Key (gol = mode FREE)", GroupName = "1. Source", Order = 1)]
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
                 Description = "Read automatically from the cloud S: header. Can be overridden via Manual spread.")]
        [Range(0, 200)]
        public double EsSpxSpread { get; set; } = 24.0;

        [Display(Name = "NQ↔NDX spread", GroupName = "2. Layout", Order = 3)]
        [Range(0, 200)]
        public double NqNdxSpread { get; set; } = 40.0;

        [Display(Name = "Manual spread override (0 = auto)", GroupName = "2. Layout", Order = 4,
                 Description = "If > 0, forces the ES↔SPX spread to this value instead of the cloud S: header. Use a tiny positive value (e.g. 0.001) to bypass spread conversion entirely if you see a constant offset vs the TLADe Terminal.")]
        [Range(0, 200)]
        public double ManualSpreadOverride { get; set; } = 0.0;

        [Display(Name = "Snap to instrument tick", GroupName = "2. Layout", Order = 5,
                 Description = "Round prices to the nearest tick (0.25 on ES). Line + label snap exactly to the ATAS grid.")]
        public bool SnapToTick { get; set; } = true;

        [Display(Name = "Show GEX walls (CW/PW)", GroupName = "3. Visibility", Order = 1)]
        public bool ShowGexLevels { get; set; } = true;

        [Display(Name = "Show System (ZG/MP/EM/VB)", GroupName = "3. Visibility", Order = 2)]
        public bool ShowSystemLevels { get; set; } = true;

        [Display(Name = "Show Structure (PDH/PDL/PWH/PWL)", GroupName = "3. Visibility", Order = 3)]
        public bool ShowStructureLevels { get; set; } = true;

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
                 Description = "Default ON. Uncheck to force a custom text color (useful when red/green don't read well on your background).")]
        public bool LabelInheritLineColor { get; set; } = true;

        [Display(Name = "Label text color (when not inheriting)", GroupName = "5. Style", Order = 8)]
        public Color LabelTextColor { get; set; } = Colors.White;

        [Display(Name = "Label background opacity (0-255)", GroupName = "5. Style", Order = 9)]
        [Range(0, 255)]
        public int LabelBgAlpha { get; set; } = 140;

        [Display(Name = "Fundal label diferentiat above/below spot", GroupName = "5. Style", Order = 10,
                 Description = "Default OFF (background derived from line color). When ON, levels above spot use the Above color, those below use Below.")]
        public bool LabelBgPositional { get; set; } = false;

        [Display(Name = "Fundal label - above spot", GroupName = "5. Style", Order = 11)]
        public Color LabelBgAbove { get; set; } = Color.FromRgb(0xdc, 0x26, 0x26); // brighter red

        [Display(Name = "Fundal label - below spot", GroupName = "5. Style", Order = 12)]
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

                double maxWallPx = regionW * (WallMaxPct / 100.0);
                double minWallPx = regionW * (WallMinPct / 100.0);

                int aboveDrawn = 0, belowDrawn = 0;
                int drawn = 0;

                foreach (var lvl in snap)
                {
                    double y = ConvertPrice(lvl.RawStrike);
                    bool isGex       = IsGex(lvl.Type);
                    bool isSystem    = IsSystem(lvl.Type);
                    bool isStructure = IsStructure(lvl.Type);
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

                    HL(ctx, (decimal)y, $"{FormatPrice(lvl.RawStrike)} {lvl.Label}", color, width, lineX1, x2, isAbove);
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

        private void HL(RenderContext ctx, decimal price, string label, DColor color, int width, int x1, int x2, bool isAbove)
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
                ctx.FillRectangle(bg, new DRect(lx - 2, ly - 1, labW + 6, labH + 2));
                ctx.DrawString(label, _fn, textCol,
                    new DRect(lx, ly, labW + 4, labH), _sfL);
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

        private double ConvertPrice(double rawStrike)
        {
            // TLADe cloud `indicatorData` returns strikes already in the futures
            // price space (ES for the SPX family, NQ for the NDX family) — the
            // spread is applied upstream when the snapshot is built. For ETF
            // proxies (SPY/QQQ) we still divide by the conventional multipliers.
            // Snap to the chart's tick grid at the end so labels align cleanly.
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
            // 2 decimals so user sees snapped value (e.g. 7382.50 not 7383)
            return cv.ToString("0.00", _inv);
        }

        private static void Log(string msg)
        {
            try
            {
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
        }
    }
}
