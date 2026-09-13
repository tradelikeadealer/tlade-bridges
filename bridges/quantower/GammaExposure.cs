// Copyright (c) 2026 Dogan Cile
//
// Licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Community contribution to TLADe (tlade-bridges). Published as delivered by the
// author on 31 August 2026; the header is the only change.
//
// Gamma Exposure for Quantower renders authoritative TLADe gamma levels and
// profile data. The indicator consumes precomputed S/L/P payloads and does not
// acquire option chains or calculate gamma exposure locally.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.Utils;

namespace Dogan.Quantower.Indicators
{
    public enum GammaExposureRefreshMode
    {
        GlobalSixSessions,
        Interval,
        ManualOnly
    }

    public enum GammaExposureUnderlyingSymbol
    {
        Auto,
        NQ,
        ES
    }

    public enum GammaExposureLevelSelection
    {
        Strongest,
        NearestPrice
    }

    public enum GammaExposureMarkerAlignment
    {
        Left,
        Right
    }

    public enum GammaExposureProfileAlignment
    {
        Left,
        Right
    }

    public enum GammaExposureProfileScale
    {
        Linear,
        SquareRoot,
        Logarithmic
    }

    public enum GammaExposureControlPlacement
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public class GammaExposure : Indicator
    {
        private enum TladeLevelKind
        {
            CallWall,
            PutWall,
            ZeroGamma,
            MaxPain,
            DeltaFlip,
            ExpectedMove,
            VolatilityBand,
            PriorStructure,
            Other
        }

        private enum DataState
        {
            Idle,
            Loading,
            Ready,
            Error
        }

        private sealed class TladeLevel
        {
            public double Price;
            public double Exposure;
            public string Code;
            public string Label;
            public string Tooltip;
            public TladeLevelKind Kind;
        }

        private sealed class TladeProfilePoint
        {
            public double Price;
            public double Exposure;
        }

        private sealed class TladeSnapshot
        {
            public string Source;
            public string Ticker;
            public DateTime FetchedUtc;
            public double Spread;
            public TladeLevel StrongestCallWall;
            public TladeLevel StrongestPutWall;
            public TladeLevel ZeroGamma;
            public readonly List<TladeLevel> CallWalls =
                new List<TladeLevel>();
            public readonly List<TladeLevel> PutWalls =
                new List<TladeLevel>();
            public readonly List<TladeLevel> NamedLevels =
                new List<TladeLevel>();
            public readonly List<TladeProfilePoint> Profile =
                new List<TladeProfilePoint>();
        }

        private sealed class LoadRequest
        {
            public bool Live;
            public string ManualData;
            public string ApiKey;
            public string ApiUrl;
            public string Ticker;
        }

        private sealed class RenderLevel
        {
            public double Price;
            public double Exposure;
            public string Label;
            public TladeLevelKind Kind;
            public int Priority;
            public Color LineColor;
            public int LineWidth;
            public TradingPlatform.BusinessLayer.LineStyle LineStyle;
            public Color MarkerColor;
        }

        private sealed class RenderMarker
        {
            public RenderLevel Level;
            public float LineY;
            public float MarkerY;
            public float Width;
            public float Height;
            public string Text;
        }

        private sealed class SessionCheckpoint
        {
            public string Name;
            public DateTime Utc;

            public string Key
            {
                get
                {
                    return (Name ?? string.Empty) + "|" +
                        Utc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        #region Input parameters

        [InputParameter("Live", 0)]
        public bool TladeLive { get; set; } = false;

        [InputParameter("TLADe manual data", 1)]
        public string TladeManualData { get; set; } = string.Empty;

        [InputParameter("TLADe API key", 2)]
        public string TladeApiKey { get; set; } = string.Empty;

        [InputParameter("TLADe API URL", 3)]
        public string TladeApiUrl { get; set; } =
            "https://europe-west1-omggex.cloudfunctions.net/indicatorData";

        public GammaExposureUnderlyingSymbol UnderlyingSymbol { get; set; } =
            GammaExposureUnderlyingSymbol.Auto;

        [InputParameter("Refresh mode", 5)]
        public GammaExposureRefreshMode RefreshMode { get; set; } =
            GammaExposureRefreshMode.GlobalSixSessions;

        [InputParameter("Top N wall levels", 10)]
        public bool ShowTopLevels { get; set; } = true;

        [InputParameter(
            "Top levels per side",
            11,
            minimum: 5,
            maximum: 30,
            increment: 1,
            decimalPlaces: 0)]
        public int TopLevelsPerSide { get; set; } = 10;

        [InputParameter("Level selection", 12)]
        public GammaExposureLevelSelection LevelSelection
        { get; set; } = GammaExposureLevelSelection.Strongest;

        [InputParameter("Limit to price radius", 13)]
        public bool LimitToPriceRadius { get; set; } = false;

        [InputParameter(
            "Price radius (%)",
            14,
            minimum: 0.1,
            maximum: 20.0,
            increment: 0.1,
            decimalPlaces: 1)]
        public double PriceRadiusPercent { get; set; } = 1.0;

        [InputParameter("Zero gamma", 15)]
        public bool ShowZeroGamma { get; set; } = true;

        [InputParameter("Exposure in markers", 16)]
        public bool ShowExposureInMarkers { get; set; } = false;

        [InputParameter("Show GEX profile", 20)]
        public bool ShowGexProfile { get; set; } = true;

        [InputParameter("Alignment", 21)]
        public GammaExposureProfileAlignment ProfileAlignment
        { get; set; } = GammaExposureProfileAlignment.Right;

        [InputParameter(
            "Width (px)",
            22,
            minimum: 20,
            maximum: 1000,
            increment: 5,
            decimalPlaces: 0)]
        public int ProfileWidth { get; set; } = 160;

        [InputParameter(
            "Horizontal offset (px)",
            23,
            minimum: 0,
            maximum: 1000,
            increment: 5,
            decimalPlaces: 0)]
        public int ProfileHorizontalOffset { get; set; } = 0;

        [InputParameter(
            "Bar height (px)",
            24,
            minimum: 1,
            maximum: 20,
            increment: 1,
            decimalPlaces: 0)]
        public int ProfileBarHeight { get; set; } = 2;

        [InputParameter("Bar scale", 25)]
        public GammaExposureProfileScale ProfileScale
        { get; set; } = GammaExposureProfileScale.Linear;

        [InputParameter(
            "Opacity (%)",
            26,
            minimum: 0,
            maximum: 100,
            increment: 5,
            decimalPlaces: 0)]
        public int ProfileOpacity { get; set; } = 90;

        [InputParameter("Positive GEX color", 27)]
        public Color PositiveProfileColor { get; set; } =
            Color.FromArgb(239, 68, 68);

        [InputParameter("Negative GEX color", 28)]
        public Color NegativeProfileColor { get; set; } =
            Color.FromArgb(34, 197, 94);

        [InputParameter("Alignment", 30)]
        public GammaExposureMarkerAlignment MarkerAlignment
        { get; set; } = GammaExposureMarkerAlignment.Left;

        [InputParameter(
            "Padding (px)",
            32,
            minimum: 0,
            maximum: 30,
            increment: 1,
            decimalPlaces: 0)]
        public int MarkerPadding { get; set; } = 4;

        [InputParameter(
            "Background opacity (%)",
            33,
            minimum: 0,
            maximum: 100,
            increment: 5,
            decimalPlaces: 0)]
        public int MarkerBackgroundOpacity { get; set; } = 100;

        [InputParameter("Automatic text color", 34)]
        public bool AutomaticMarkerTextColor { get; set; } = true;

        [InputParameter("Text color", 35)]
        public Color MarkerTextColor { get; set; } = Color.White;

        [InputParameter("Call wall color", 36)]
        public Color CallMarkerColor { get; set; } =
            Color.FromArgb(239, 68, 68);

        [InputParameter("Put wall color", 37)]
        public Color PutMarkerColor { get; set; } =
            Color.FromArgb(34, 197, 94);

        [InputParameter("Zero-gamma color", 38)]
        public Color ZeroGammaMarkerColor { get; set; } = Color.White;

        [InputParameter("Max-pain color", 39)]
        public Color MaxPainMarkerColor { get; set; } =
            Color.FromArgb(239, 68, 68);

        [InputParameter("Delta-flip color", 40)]
        public Color DeltaFlipMarkerColor { get; set; } =
            Color.FromArgb(245, 158, 11);

        [InputParameter("Expected-move color", 41)]
        public Color ExpectedMoveMarkerColor { get; set; } =
            Color.FromArgb(155, 122, 234);

        [InputParameter("Volatility-band color", 42)]
        public Color VolatilityBandMarkerColor { get; set; } =
            Color.FromArgb(148, 163, 184);

        [InputParameter("Prior-level color", 43)]
        public Color PriorStructureMarkerColor { get; set; } =
            Color.FromArgb(56, 189, 248);

        [InputParameter("Other TLADe color", 44)]
        public Color OtherMarkerColor { get; set; } =
            Color.FromArgb(249, 115, 22);

        public LineOptions CallLineOptions { get; set; }
        public LineOptions PutLineOptions { get; set; }
        public LineOptions ZeroGammaLineOptions { get; set; }
        public LineOptions MaxPainLineOptions { get; set; }
        public LineOptions DeltaFlipLineOptions { get; set; }
        public LineOptions ExpectedMoveLineOptions { get; set; }
        public LineOptions VolatilityBandLineOptions { get; set; }
        public LineOptions PriorStructureLineOptions { get; set; }
        public LineOptions OtherLineOptions { get; set; }

        [InputParameter("Show status dot", 80)]
        public bool ShowStatusDot { get; set; } = true;

        [InputParameter("Show refresh button", 81)]
        public bool ShowRefreshButton { get; set; } = true;

        [InputParameter("Placement", 82)]
        public GammaExposureControlPlacement ControlPlacement
        { get; set; } = GammaExposureControlPlacement.TopRight;

        #endregion

        private readonly object stateSync = new object();
        private readonly object fontSync = new object();
        private const string MarkerFontSettingName = "MarkerFont";
        private const string UnderlyingSymbolSettingName = "UnderlyingSymbol";
        private const string CallLineSettingName = "CallLineOptions";
        private const string PutLineSettingName = "PutLineOptions";
        private const string ZeroGammaLineSettingName = "ZeroGammaLineOptions";
        private const string MaxPainLineSettingName = "MaxPainLineOptions";
        private const string DeltaFlipLineSettingName = "DeltaFlipLineOptions";
        private const string ExpectedMoveLineSettingName = "ExpectedMoveLineOptions";
        private const string VolatilityBandLineSettingName = "VolatilityBandLineOptions";
        private const string PriorStructureLineSettingName = "PriorStructureLineOptions";
        private const string OtherLineSettingName = "OtherLineOptions";
        private const int IntervalRefreshSeconds = 900;
        private Font markerFont = new Font(
            "Consolas",
            11.0f,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        private TladeSnapshot snapshot;
        private DataState dataState = DataState.Idle;
        private string statusMessage = "Not loaded";
        private bool disposed;
        private bool loading;
        private int loadGeneration;
        private HttpWebRequest activeWebRequest;
        private DateTime nextIntervalRefreshUtc = DateTime.MinValue;
        private DateTime lastScheduleCheckUtc = DateTime.MinValue;
        private string lastCheckpointKey = string.Empty;
        private double latestChartPrice = double.NaN;
        private Rectangle refreshHitBox = Rectangle.Empty;
        private TradingPlatform.BusinessLayer.Chart.IChart subscribedChart;

        public GammaExposure()
            : base()
        {
            Name = "Gamma Exposure (TLADe)";
            Description =
                "TLADe precomputed gamma walls, named levels, and GEX profile.";
            SeparateWindow = false;
            AllowFitAuto = false;
            OnBackGround = false;

            CallLineOptions = CreateLineOptions(Color.FromArgb(239, 68, 68));
            PutLineOptions = CreateLineOptions(Color.FromArgb(34, 197, 94));
            ZeroGammaLineOptions = CreateLineOptions(Color.White);
            MaxPainLineOptions = CreateLineOptions(Color.FromArgb(239, 68, 68));
            DeltaFlipLineOptions = CreateLineOptions(Color.FromArgb(245, 158, 11));
            ExpectedMoveLineOptions = CreateLineOptions(Color.FromArgb(155, 122, 234));
            VolatilityBandLineOptions = CreateLineOptions(Color.FromArgb(148, 163, 184));
            PriorStructureLineOptions = CreateLineOptions(Color.FromArgb(56, 189, 248));
            OtherLineOptions = CreateLineOptions(Color.FromArgb(249, 115, 22));

            AddLineSeries(
                "TLADe state",
                Color.Transparent,
                1,
                TradingPlatform.BusinessLayer.LineStyle.Solid);
        }

        public override IList<SettingItem> Settings
        {
            get
            {
                IList<SettingItem> settings = base.Settings;
                SettingItemSeparatorGroup dataGroup = CreateSettingsGroup(
                    "Data",
                    0);
                SettingItemSeparatorGroup levelsGroup = CreateSettingsGroup(
                    "Levels",
                    1);
                SettingItemSeparatorGroup profileGroup = CreateSettingsGroup(
                    "GEX Profile",
                    2);
                SettingItemSeparatorGroup markersGroup = CreateSettingsGroup(
                    "Markers",
                    3);
                SettingItemSeparatorGroup linesGroup = CreateSettingsGroup(
                    "Lines",
                    4);
                SettingItemSeparatorGroup controlsGroup = CreateSettingsGroup(
                    "Controls",
                    5);
                SettingItem markerFontSetting = settings.GetItemByName(
                    MarkerFontSettingName);

                settings.Add(new SettingItemSelectorLocalized(
                    UnderlyingSymbolSettingName,
                    new SelectItem(string.Empty, (int)UnderlyingSymbol),
                    new List<SelectItem>
                    {
                        new SelectItem("Auto", (int)GammaExposureUnderlyingSymbol.Auto),
                        new SelectItem("NQ", (int)GammaExposureUnderlyingSymbol.NQ),
                        new SelectItem("ES", (int)GammaExposureUnderlyingSymbol.ES)
                    })
                {
                    Text = "Underlying symbol",
                    SortIndex = 4,
                    SeparatorGroup = dataGroup
                });

                AddLineOptionsSetting(settings, CallLineSettingName,
                    "Call wall", CallLineOptions, 50, linesGroup);
                AddLineOptionsSetting(settings, PutLineSettingName,
                    "Put wall", PutLineOptions, 51, linesGroup);
                AddLineOptionsSetting(settings, ZeroGammaLineSettingName,
                    "Zero gamma", ZeroGammaLineOptions, 52, linesGroup);
                AddLineOptionsSetting(settings, MaxPainLineSettingName,
                    "Max pain", MaxPainLineOptions, 53, linesGroup);
                AddLineOptionsSetting(settings, DeltaFlipLineSettingName,
                    "Delta flip", DeltaFlipLineOptions, 54, linesGroup);
                AddLineOptionsSetting(settings, ExpectedMoveLineSettingName,
                    "Expected move", ExpectedMoveLineOptions, 55, linesGroup);
                AddLineOptionsSetting(settings, VolatilityBandLineSettingName,
                    "Volatility band", VolatilityBandLineOptions, 56, linesGroup);
                AddLineOptionsSetting(settings, PriorStructureLineSettingName,
                    "Prior level", PriorStructureLineOptions, 57, linesGroup);
                AddLineOptionsSetting(settings, OtherLineSettingName,
                    "Other TLADe", OtherLineOptions, 58, linesGroup);

                for (int i = 0; i < settings.Count; i++)
                {
                    SettingItem setting = settings[i];
                    if (!IsInputParameterSetting(setting))
                        continue;

                    int sortIndex = setting.SortIndex;
                    if (sortIndex < 10)
                        setting.SeparatorGroup = dataGroup;
                    else if (sortIndex < 20)
                        setting.SeparatorGroup = levelsGroup;
                    else if (sortIndex < 30)
                        setting.SeparatorGroup = profileGroup;
                    else if (sortIndex < 50)
                        setting.SeparatorGroup = markersGroup;
                    else if (sortIndex < 80)
                        setting.SeparatorGroup = linesGroup;
                    else if (sortIndex < 90)
                        setting.SeparatorGroup = controlsGroup;
                }

                if (markerFontSetting == null)
                {
                    markerFontSetting = new SettingItemFont(
                        MarkerFontSettingName,
                        CloneMarkerFont(),
                        31);
                    settings.Add(markerFontSetting);
                }

                markerFontSetting.Text = "Font";
                markerFontSetting.SortIndex = 31;
                markerFontSetting.SeparatorGroup = markersGroup;

                return settings;
            }
            set
            {
                if (value != null)
                {
                    SettingItemSelectorLocalized underlyingSetting =
                        value.GetItemByName(UnderlyingSymbolSettingName)
                            as SettingItemSelectorLocalized;
                    SelectItem selectedUnderlying = underlyingSetting == null
                        ? null
                        : underlyingSetting.Value as SelectItem;
                    if (selectedUnderlying != null)
                        UnderlyingSymbol =
                            (GammaExposureUnderlyingSymbol)(int)selectedUnderlying.Value;

                    ApplyLineOptions(value, CallLineSettingName,
                        delegate(LineOptions options) { CallLineOptions = options; });
                    ApplyLineOptions(value, PutLineSettingName,
                        delegate(LineOptions options) { PutLineOptions = options; });
                    ApplyLineOptions(value, ZeroGammaLineSettingName,
                        delegate(LineOptions options) { ZeroGammaLineOptions = options; });
                    ApplyLineOptions(value, MaxPainLineSettingName,
                        delegate(LineOptions options) { MaxPainLineOptions = options; });
                    ApplyLineOptions(value, DeltaFlipLineSettingName,
                        delegate(LineOptions options) { DeltaFlipLineOptions = options; });
                    ApplyLineOptions(value, ExpectedMoveLineSettingName,
                        delegate(LineOptions options) { ExpectedMoveLineOptions = options; });
                    ApplyLineOptions(value, VolatilityBandLineSettingName,
                        delegate(LineOptions options) { VolatilityBandLineOptions = options; });
                    ApplyLineOptions(value, PriorStructureLineSettingName,
                        delegate(LineOptions options) { PriorStructureLineOptions = options; });
                    ApplyLineOptions(value, OtherLineSettingName,
                        delegate(LineOptions options) { OtherLineOptions = options; });
                }

                base.Settings = value;

                Font selectedFont;
                if (value != null && value.TryGetValue(
                    MarkerFontSettingName,
                    out selectedFont))
                    ReplaceMarkerFont(selectedFont);

                OnSettingsUpdated();
            }
        }

        private static LineOptions CreateLineOptions(Color color)
        {
            return new LineOptions
            {
                Color = color,
                Width = 1,
                LineStyle = TradingPlatform.BusinessLayer.LineStyle.Dash,
                WithCheckBox = false,
                Enabled = true
            };
        }

        private static void AddLineOptionsSetting(
            IList<SettingItem> settings,
            string name,
            string text,
            LineOptions options,
            int sortIndex,
            SettingItemSeparatorGroup group)
        {
            settings.Add(new SettingItemLineOptions(
                name,
                options,
                sortIndex)
            {
                Text = text,
                SeparatorGroup = group
            });
        }

        private static void ApplyLineOptions(
            IList<SettingItem> settings,
            string name,
            Action<LineOptions> apply)
        {
            SettingItemLineOptions setting =
                settings.GetItemByName(name) as SettingItemLineOptions;
            LineOptions options = setting == null
                ? null
                : setting.Value as LineOptions;
            if (options != null)
                apply(options);
        }

        private static SettingItemSeparatorGroup CreateSettingsGroup(
            string text,
            int sortIndex)
        {
            return new SettingItemSeparatorGroup(text, sortIndex)
            {
                DefaultExpandedState = true
            };
        }

        private static bool IsInputParameterSetting(SettingItem setting)
        {
            return setting != null &&
                setting.SeparatorGroup != null &&
                string.Equals(
                    setting.SeparatorGroup.Text,
                    "Input parameters",
                    StringComparison.OrdinalIgnoreCase);
        }

        private Font CloneMarkerFont()
        {
            lock (fontSync)
            {
                if (markerFont != null)
                    return (Font)markerFont.Clone();
            }

            return new Font(
                "Consolas",
                11.0f,
                FontStyle.Regular,
                GraphicsUnit.Pixel);
        }

        private void ReplaceMarkerFont(Font selectedFont)
        {
            if (selectedFont == null)
                return;

            Font replacement = (Font)selectedFont.Clone();
            Font previous;
            lock (fontSync)
            {
                previous = markerFont;
                markerFont = replacement;
            }

            if (previous != null)
                previous.Dispose();
        }

        protected override void OnInit()
        {
            base.OnInit();

            lock (stateSync)
            {
                disposed = false;
                snapshot = null;
                dataState = DataState.Idle;
                statusMessage = "Not loaded";
                nextIntervalRefreshUtc = DateTime.MinValue;
                lastScheduleCheckUtc = DateTime.MinValue;
                SessionCheckpoint latest = SessionSchedule.Latest(
                    DateTime.UtcNow);
                lastCheckpointKey = latest == null
                    ? string.Empty
                    : latest.Key;
            }

            SubscribeChartMouse();
            CaptureLatestChartPrice();
            StartLoad();
        }

        protected override void OnUpdate(UpdateArgs args)
        {
            CaptureLatestChartPrice();
            CheckAutomaticRefresh();
        }

        protected override void OnClear()
        {
            CancelPendingLoad(true);

            lock (stateSync)
            {
                snapshot = null;
                dataState = DataState.Idle;
                statusMessage = "Not loaded";
                refreshHitBox = Rectangle.Empty;
            }

            base.OnClear();
        }

        public override void Dispose()
        {
            CancelPendingLoad(true);
            UnsubscribeChartMouse();

            Font fontToDispose;
            lock (fontSync)
            {
                fontToDispose = markerFont;
                markerFont = null;
            }

            if (fontToDispose != null)
                fontToDispose.Dispose();

            base.Dispose();
        }

        private void SubscribeChartMouse()
        {
            if (CurrentChart == null || subscribedChart == CurrentChart)
                return;

            UnsubscribeChartMouse();
            subscribedChart = CurrentChart;
            subscribedChart.MouseDown += CurrentChart_MouseDown;
        }

        private void UnsubscribeChartMouse()
        {
            if (subscribedChart == null)
                return;

            try
            {
                subscribedChart.MouseDown -= CurrentChart_MouseDown;
            }
            catch
            {
            }

            subscribedChart = null;
        }

        private void CurrentChart_MouseDown(
            object sender,
            TradingPlatform.BusinessLayer.Chart.ChartMouseNativeEventArgs e)
        {
            if (!ShowRefreshButton ||
                e.Button !=
                    TradingPlatform.BusinessLayer.Native.NativeMouseButtons.Left)
                return;

            Rectangle hitBox;
            lock (stateSync)
                hitBox = refreshHitBox;

            if (!hitBox.Contains(e.Location))
                return;

            StartLoad();
            e.Handled = true;
        }

        private void CaptureLatestChartPrice()
        {
            double price = double.NaN;

            try
            {
                if (Symbol != null && IsUsablePrice(Symbol.Last))
                    price = Symbol.Last;
            }
            catch
            {
            }

            if (!IsUsablePrice(price))
            {
                try
                {
                    double close = Close();
                    if (IsUsablePrice(close))
                        price = close;
                }
                catch
                {
                }
            }

            if (IsUsablePrice(price))
                latestChartPrice = price;
        }

        private double GetReferencePrice()
        {
            if (IsUsablePrice(latestChartPrice))
                return latestChartPrice;

            try
            {
                if (Symbol != null && IsUsablePrice(Symbol.Last))
                    return Symbol.Last;
            }
            catch
            {
            }

            return 0.0;
        }

        private void CheckAutomaticRefresh()
        {
            if (!TladeLive ||
                RefreshMode == GammaExposureRefreshMode.ManualOnly)
                return;

            DateTime now = DateTime.UtcNow;
            if (lastScheduleCheckUtc != DateTime.MinValue &&
                (now - lastScheduleCheckUtc).TotalSeconds < 1.0)
                return;
            lastScheduleCheckUtc = now;

            if (RefreshMode == GammaExposureRefreshMode.Interval)
            {
                if (nextIntervalRefreshUtc != DateTime.MinValue &&
                    now >= nextIntervalRefreshUtc)
                    StartLoad();
                return;
            }

            SessionCheckpoint latest = SessionSchedule.Latest(now);
            if (latest == null || string.Equals(
                latest.Key,
                lastCheckpointKey,
                StringComparison.Ordinal))
                return;

            lastCheckpointKey = latest.Key;
            StartLoad();
        }

        private void StartLoad()
        {
            LoadRequest request;
            int generation;

            lock (stateSync)
            {
                if (disposed || loading)
                    return;

                request = BuildLoadRequest();
                loading = true;
                dataState = DataState.Loading;
                statusMessage = "Loading TLADe data";
                generation = ++loadGeneration;
            }

            RequestChartRedraw();

            Task.Run(delegate
            {
                try
                {
                    string payload = request.Live
                        ? DownloadPayload(request, generation)
                        : request.ManualData;
                    TladeSnapshot loaded = ParsePayload(
                        payload,
                        request.Live
                            ? "TLADe indicatorData API"
                            : "manual precomputed payload",
                        request.Ticker);
                    PublishLoadSuccess(generation, loaded);
                }
                catch (Exception ex)
                {
                    PublishLoadFailure(generation, ex);
                }
            });
        }

        private LoadRequest BuildLoadRequest()
        {
            return new LoadRequest
            {
                Live = TladeLive,
                ManualData = TladeManualData ?? string.Empty,
                ApiKey = TladeApiKey ?? string.Empty,
                ApiUrl = TladeApiUrl ?? string.Empty,
                Ticker = ResolveTladeTicker(TladeLive)
            };
        }

        private string ResolveTladeTicker(bool required)
        {
            string root;
            switch (UnderlyingSymbol)
            {
                case GammaExposureUnderlyingSymbol.NQ:
                    root = "NQ";
                    break;
                case GammaExposureUnderlyingSymbol.ES:
                    root = "ES";
                    break;
                default:
                    root = string.Empty;
                    try
                    {
                        if (Symbol != null)
                        {
                            root = Symbol.Root;
                            if (string.IsNullOrWhiteSpace(root))
                                root = Symbol.Name;
                        }
                    }
                    catch
                    {
                    }
                    break;
            }

            string normalized = NormalizeInstrumentRoot(root);
            if (normalized == "ES" || normalized == "MES" ||
                normalized.StartsWith("ES", StringComparison.Ordinal) ||
                normalized.StartsWith("MES", StringComparison.Ordinal))
                return "SPX";

            if (normalized == "NQ" || normalized == "MNQ" ||
                normalized.StartsWith("NQ", StringComparison.Ordinal) ||
                normalized.StartsWith("MNQ", StringComparison.Ordinal))
                return "NDX";

            if (string.Equals(normalized, "SPX", StringComparison.Ordinal))
                return "SPX";
            if (string.Equals(normalized, "NDX", StringComparison.Ordinal))
                return "NDX";

            if (required)
                throw new InvalidOperationException(
                    "TLADe live mode supports ES/MES and NQ/MNQ charts. " +
                    "Set Underlying symbol to ES or NQ when auto-detection is unavailable.");

            return normalized;
        }

        private static string NormalizeInstrumentRoot(string value)
        {
            string normalized = (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .TrimStart('@', '/', '.', ':', '-', '_', ' ');
            return normalized.Replace(" ", string.Empty);
        }

        private string DownloadPayload(LoadRequest request, int generation)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
                throw new InvalidOperationException(
                    "TLADe live mode requires an API key.");
            if (string.IsNullOrWhiteSpace(request.ApiUrl))
                throw new InvalidOperationException(
                    "TLADe live mode requires an API URL.");

            Uri endpoint;
            if (!Uri.TryCreate(request.ApiUrl.Trim(), UriKind.Absolute, out endpoint) ||
                !string.Equals(
                    endpoint.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "TLADe API URL must be an absolute HTTPS URL.");

            string separator = request.ApiUrl.IndexOf('?') >= 0 ? "&" : "?";
            string url = request.ApiUrl.Trim() + separator +
                "ticker=" + Uri.EscapeDataString(request.Ticker);

            HttpWebRequest webRequest =
                (HttpWebRequest)WebRequest.Create(url);
            webRequest.Method = "GET";
            webRequest.Accept = "text/plain, application/json";
            webRequest.UserAgent = "Dogan-GammaExposure-Quantower/1.0";
            webRequest.Timeout = 20000;
            webRequest.ReadWriteTimeout = 20000;
            webRequest.AllowAutoRedirect = false;
            webRequest.AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate;
            webRequest.Headers["X-API-Key"] = request.ApiKey.Trim();

            lock (stateSync)
            {
                if (disposed || generation != loadGeneration)
                    throw new OperationCanceledException();
                activeWebRequest = webRequest;
            }

            try
            {
                using (HttpWebResponse response =
                    (HttpWebResponse)webRequest.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode < 200 || statusCode >= 300)
                        throw new WebException(
                            "TLADe returned HTTP " +
                            statusCode.ToString(CultureInfo.InvariantCulture) + ".");

                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream))
                        return reader.ReadToEnd();
                }
            }
            finally
            {
                lock (stateSync)
                {
                    if (ReferenceEquals(activeWebRequest, webRequest))
                        activeWebRequest = null;
                }
            }
        }

        private void PublishLoadSuccess(
            int generation,
            TladeSnapshot loaded)
        {
            lock (stateSync)
            {
                if (disposed || generation != loadGeneration)
                    return;

                snapshot = loaded;
                loading = false;
                dataState = DataState.Ready;
                statusMessage = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}; {1} levels, {2} profile points; spread {3:0.####}",
                    loaded.Source,
                    loaded.CallWalls.Count + loaded.PutWalls.Count +
                        loaded.NamedLevels.Count +
                        (loaded.ZeroGamma == null ? 0 : 1),
                    loaded.Profile.Count,
                    loaded.Spread);
                nextIntervalRefreshUtc = DateTime.UtcNow.AddSeconds(
                    IntervalRefreshSeconds);
            }

            RequestChartRedraw();
        }

        private void PublishLoadFailure(int generation, Exception exception)
        {
            bool publish;
            lock (stateSync)
            {
                publish = !disposed && generation == loadGeneration;
                if (publish)
                {
                    loading = false;
                    dataState = DataState.Error;
                    statusMessage = exception == null
                        ? "TLADe load failed"
                        : exception.Message;
                    nextIntervalRefreshUtc = DateTime.UtcNow.AddSeconds(
                    IntervalRefreshSeconds);
                }
            }

            if (!publish)
                return;

            if (!(exception is OperationCanceledException))
            {
                try
                {
                    Core.Instance.Loggers.Log(
                        new Exception(
                            "Gamma Exposure (TLADe): " +
                            (exception == null
                                ? "load failed"
                                : exception.Message),
                            exception));
                }
                catch
                {
                }
            }

            RequestChartRedraw();
        }

        private void CancelPendingLoad(bool disposeInstance)
        {
            HttpWebRequest request = null;

            lock (stateSync)
            {
                disposed = disposeInstance;
                loadGeneration++;
                loading = false;
                request = activeWebRequest;
                activeWebRequest = null;
            }

            if (request != null)
            {
                try { request.Abort(); }
                catch { }
            }
        }

        private void RequestChartRedraw()
        {
            try
            {
                if (CurrentChart != null)
                    CurrentChart.RedrawBuffer();
            }
            catch
            {
            }
        }

        private static TladeSnapshot ParsePayload(
            string payload,
            string source,
            string ticker)
        {
            string text = (payload ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(
                    "TLADe returned an empty precomputed payload.");

            int levelsStart = text.IndexOf(
                "L:",
                StringComparison.OrdinalIgnoreCase);
            int profileStart = text.IndexOf(
                "|P:",
                StringComparison.OrdinalIgnoreCase);
            if (levelsStart < 0)
                throw new FormatException(
                    "TLADe payload is missing the L: level section.");

            TladeSnapshot result = new TladeSnapshot
            {
                Source = source ?? string.Empty,
                Ticker = ticker ?? string.Empty,
                FetchedUtc = DateTime.UtcNow,
                Spread = 0.0
            };

            int spreadStart = text.IndexOf(
                "S:",
                StringComparison.OrdinalIgnoreCase);
            if (spreadStart >= 0 && spreadStart < levelsStart)
            {
                int spreadEnd = text.IndexOf('|', spreadStart);
                string spreadText = spreadEnd > spreadStart
                    ? text.Substring(
                        spreadStart + 2,
                        spreadEnd - spreadStart - 2)
                    : string.Empty;
                result.Spread = ParseNumber(spreadText, 0.0);
            }

            string levelsText = text.Substring(
                levelsStart + 2,
                (profileStart >= 0 ? profileStart : text.Length) -
                    (levelsStart + 2));
            string[] levelRows = levelsText.Split(
                new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < levelRows.Length; i++)
            {
                string[] fields = levelRows[i].Split(new[] { ',' }, 5);
                if (fields.Length < 3)
                    continue;

                double price = ParseNumber(fields[0], double.NaN);
                if (!IsUsablePrice(price))
                    continue;

                string code = fields[1].Trim().ToUpperInvariant();
                string label = fields[2].Trim();
                string tooltip = fields.Length > 3
                    ? fields[3].Replace("~", Environment.NewLine).Trim()
                    : string.Empty;
                double magnitudeMillions = fields.Length > 4
                    ? Math.Abs(ParseNumber(fields[4], 0.0))
                    : 0.0;
                double exposure = magnitudeMillions * 1000000.0;

                TladeLevel level = new TladeLevel
                {
                    Price = price,
                    Exposure = exposure,
                    Code = code,
                    Label = string.IsNullOrWhiteSpace(label) ? code : label,
                    Tooltip = tooltip,
                    Kind = ResolveNamedLevelKind(code, label)
                };

                if (code == "CW")
                {
                    level.Kind = TladeLevelKind.CallWall;
                    result.CallWalls.Add(level);
                    if (result.StrongestCallWall == null ||
                        exposure > Math.Abs(
                            result.StrongestCallWall.Exposure))
                        result.StrongestCallWall = level;
                }
                else if (code == "PW")
                {
                    level.Kind = TladeLevelKind.PutWall;
                    level.Exposure = -exposure;
                    result.PutWalls.Add(level);
                    if (result.StrongestPutWall == null ||
                        exposure > Math.Abs(
                            result.StrongestPutWall.Exposure))
                        result.StrongestPutWall = level;
                }
                else if (code == "ZG")
                {
                    level.Kind = TladeLevelKind.ZeroGamma;
                    level.Label = string.IsNullOrWhiteSpace(label)
                        ? "ZERO GAMMA"
                        : label;
                    result.ZeroGamma = level;
                }
                else
                {
                    result.NamedLevels.Add(level);
                }
            }

            if (profileStart >= 0)
            {
                string profileText = text.Substring(profileStart + 3);
                string[] profileRows = profileText.Split(
                    new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries);

                for (int i = 0; i < profileRows.Length; i++)
                {
                    string[] fields = profileRows[i].Split(',');
                    if (fields.Length < 2)
                        continue;

                    double price = ParseNumber(fields[0], double.NaN);
                    double magnitude = Math.Abs(
                        ParseNumber(fields[1], 0.0));
                    double sign = fields.Length > 2
                        ? ParseNumber(fields[2], 0.0)
                        : ParseNumber(fields[1], 0.0);

                    if (!IsUsablePrice(price) || magnitude <= 0.0)
                        continue;

                    result.Profile.Add(new TladeProfilePoint
                    {
                        Price = price,
                        Exposure = sign < 0.0 ? -magnitude : magnitude
                    });
                }
            }

            if (result.CallWalls.Count == 0 &&
                result.PutWalls.Count == 0 &&
                result.NamedLevels.Count == 0 &&
                result.ZeroGamma == null &&
                result.Profile.Count == 0)
                throw new FormatException(
                    "TLADe payload contained no usable levels or profile points.");

            return result;
        }

        private static TladeLevelKind ResolveNamedLevelKind(
            string code,
            string label)
        {
            string normalizedCode = (code ?? string.Empty)
                .Trim()
                .ToUpperInvariant();
            string normalizedLabel = (label ?? string.Empty)
                .Trim()
                .ToUpperInvariant();

            if (normalizedCode == "MP" ||
                normalizedLabel.IndexOf(
                    "MAX PAIN",
                    StringComparison.Ordinal) >= 0)
                return TladeLevelKind.MaxPain;

            if (normalizedCode == "DF" ||
                normalizedLabel.IndexOf(
                    "DELTA FLIP",
                    StringComparison.Ordinal) >= 0)
                return TladeLevelKind.DeltaFlip;

            if (normalizedCode == "EH" || normalizedCode == "EL" ||
                normalizedLabel.IndexOf(
                    "EXPECTED MOVE",
                    StringComparison.Ordinal) >= 0 ||
                normalizedLabel.IndexOf(
                    "EM HIGH",
                    StringComparison.Ordinal) >= 0 ||
                normalizedLabel.IndexOf(
                    "EM LOW",
                    StringComparison.Ordinal) >= 0)
                return TladeLevelKind.ExpectedMove;

            if (normalizedCode == "VH" || normalizedCode == "VL" ||
                normalizedLabel.IndexOf(
                    "VOL HIGH",
                    StringComparison.Ordinal) >= 0 ||
                normalizedLabel.IndexOf(
                    "VOL LOW",
                    StringComparison.Ordinal) >= 0)
                return TladeLevelKind.VolatilityBand;

            if (normalizedCode == "PDH" || normalizedCode == "PDL" ||
                normalizedCode == "PWH" || normalizedCode == "PWL" ||
                normalizedLabel.IndexOf(
                    "PRIOR DAY",
                    StringComparison.Ordinal) >= 0 ||
                normalizedLabel.IndexOf(
                    "PRIOR WEEK",
                    StringComparison.Ordinal) >= 0)
                return TladeLevelKind.PriorStructure;

            return TladeLevelKind.Other;
        }

        private static double ParseNumber(string text, double fallback)
        {
            double value;
            return double.TryParse(
                (text ?? string.Empty).Trim(),
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out value)
                    ? value
                    : fallback;
        }

        private static bool IsUsablePrice(double price)
        {
            return !double.IsNaN(price) &&
                !double.IsInfinity(price) &&
                price > 0.0;
        }

        private List<RenderLevel> BuildRenderLevels(
            TladeSnapshot current,
            double referencePrice)
        {
            List<RenderLevel> levels = new List<RenderLevel>();
            if (current == null)
                return levels;

            if (ShowTopLevels)
            {
                IEnumerable<TladeLevel> calls = current.CallWalls;
                IEnumerable<TladeLevel> puts = current.PutWalls;

                if (IsUsablePrice(referencePrice))
                {
                    calls = calls.Where(level =>
                        level != null && level.Price >= referencePrice);
                    puts = puts.Where(level =>
                        level != null && level.Price <= referencePrice);
                }

                if (LevelSelection ==
                        GammaExposureLevelSelection.NearestPrice &&
                    LimitToPriceRadius &&
                    IsUsablePrice(referencePrice))
                {
                    double radius = referencePrice *
                        Math.Max(0.0, PriceRadiusPercent) / 100.0;
                    calls = calls.Where(level =>
                        Math.Abs(level.Price - referencePrice) <= radius);
                    puts = puts.Where(level =>
                        Math.Abs(level.Price - referencePrice) <= radius);
                }

                List<TladeLevel> rankedCalls = RankWalls(
                    calls,
                    referencePrice,
                    true);
                List<TladeLevel> rankedPuts = RankWalls(
                    puts,
                    referencePrice,
                    false);
                int count = Math.Max(5, Math.Min(30, TopLevelsPerSide));

                AddDistinctWalls(levels, rankedCalls, count);
                AddDistinctWalls(levels, rankedPuts, count);
            }
            else
            {
                if (current.StrongestCallWall != null)
                    levels.Add(CreateRenderLevel(
                        current.StrongestCallWall,
                        "Call Wall",
                        60));
                if (current.StrongestPutWall != null)
                    levels.Add(CreateRenderLevel(
                        current.StrongestPutWall,
                        "Put Wall",
                        60));
            }

            if (ShowZeroGamma && current.ZeroGamma != null)
            {
                levels.Add(CreateRenderLevel(
                    current.ZeroGamma,
                    string.IsNullOrWhiteSpace(current.ZeroGamma.Label)
                        ? "ZERO GAMMA"
                        : current.ZeroGamma.Label,
                    55));
            }

            for (int i = 0; i < current.NamedLevels.Count; i++)
            {
                TladeLevel named = current.NamedLevels[i];
                if (named == null)
                    continue;
                levels.Add(CreateRenderLevel(
                    named,
                    string.IsNullOrWhiteSpace(named.Label)
                        ? named.Code
                        : named.Label,
                    50));
            }

            MergeDuplicateLevels(levels);
            levels.Sort(delegate(RenderLevel a, RenderLevel b)
            {
                int byPriority = b.Priority.CompareTo(a.Priority);
                if (byPriority != 0)
                    return byPriority;
                return b.Price.CompareTo(a.Price);
            });
            return levels;
        }

        private List<TladeLevel> RankWalls(
            IEnumerable<TladeLevel> candidates,
            double referencePrice,
            bool calls)
        {
            if (candidates == null)
                return new List<TladeLevel>();

            if (LevelSelection ==
                    GammaExposureLevelSelection.NearestPrice &&
                IsUsablePrice(referencePrice))
            {
                return candidates
                    .Where(level => level != null)
                    .OrderBy(level =>
                        Math.Abs(level.Price - referencePrice))
                    .ThenByDescending(level =>
                        Math.Abs(level.Exposure))
                    .ToList();
            }

            IOrderedEnumerable<TladeLevel> ranked = candidates
                .Where(level => level != null)
                .OrderByDescending(level => Math.Abs(level.Exposure));
            return calls
                ? ranked.ThenByDescending(level => level.Price).ToList()
                : ranked.ThenBy(level => level.Price).ToList();
        }

        private void AddDistinctWalls(
            List<RenderLevel> output,
            List<TladeLevel> ranked,
            int count)
        {
            if (output == null || ranked == null || count <= 0)
                return;

            double groupingSize = GetGroupingSize();
            HashSet<long> prices = new HashSet<long>();

            for (int i = 0; i < ranked.Count && prices.Count < count; i++)
            {
                TladeLevel level = ranked[i];
                long key = (long)Math.Round(level.Price / groupingSize);
                if (!prices.Add(key))
                    continue;

                output.Add(CreateRenderLevel(
                    level,
                    level.Kind == TladeLevelKind.CallWall
                        ? "Call Wall"
                        : "Put Wall",
                    20));
            }
        }

        private RenderLevel CreateRenderLevel(
            TladeLevel source,
            string label,
            int priority)
        {
            RenderLevel result = new RenderLevel
            {
                Price = source.Price,
                Exposure = source.Exposure,
                Label = label ?? string.Empty,
                Kind = source.Kind,
                Priority = priority
            };

            switch (source.Kind)
            {
                case TladeLevelKind.CallWall:
                    ApplyLineOptions(result, CallLineOptions);
                    result.MarkerColor = CallMarkerColor;
                    break;

                case TladeLevelKind.PutWall:
                    ApplyLineOptions(result, PutLineOptions);
                    result.MarkerColor = PutMarkerColor;
                    break;

                case TladeLevelKind.ZeroGamma:
                    ApplyLineOptions(result, ZeroGammaLineOptions);
                    result.MarkerColor = ZeroGammaMarkerColor;
                    break;

                case TladeLevelKind.MaxPain:
                    ApplyLineOptions(result, MaxPainLineOptions);
                    result.MarkerColor = MaxPainMarkerColor;
                    break;

                case TladeLevelKind.DeltaFlip:
                    ApplyLineOptions(result, DeltaFlipLineOptions);
                    result.MarkerColor = DeltaFlipMarkerColor;
                    break;

                case TladeLevelKind.ExpectedMove:
                    ApplyLineOptions(result, ExpectedMoveLineOptions);
                    result.MarkerColor = ExpectedMoveMarkerColor;
                    break;

                case TladeLevelKind.VolatilityBand:
                    ApplyLineOptions(result, VolatilityBandLineOptions);
                    result.MarkerColor = VolatilityBandMarkerColor;
                    break;

                case TladeLevelKind.PriorStructure:
                    ApplyLineOptions(result, PriorStructureLineOptions);
                    result.MarkerColor = PriorStructureMarkerColor;
                    break;

                default:
                    ApplyLineOptions(result, OtherLineOptions);
                    result.MarkerColor = OtherMarkerColor;
                    break;
            }

            result.LineWidth = Math.Max(1, result.LineWidth);
            return result;
        }

        private static void ApplyLineOptions(
            RenderLevel target,
            LineOptions options)
        {
            LineOptions effective = options ?? CreateLineOptions(Color.White);
            target.LineColor = effective.Color;
            target.LineWidth = Math.Max(1, effective.Width);
            target.LineStyle = effective.LineStyle;
        }

        private void MergeDuplicateLevels(List<RenderLevel> levels)
        {
            if (levels == null || levels.Count < 2)
                return;

            double groupingSize = GetGroupingSize();
            Dictionary<string, RenderLevel> merged =
                new Dictionary<string, RenderLevel>(
                    StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < levels.Count; i++)
            {
                RenderLevel level = levels[i];
                long priceKey = (long)Math.Round(
                    level.Price / groupingSize);
                string key = priceKey.ToString(
                    CultureInfo.InvariantCulture) + "|" +
                    (level.Label ?? string.Empty);

                RenderLevel existing;
                if (!merged.TryGetValue(key, out existing))
                {
                    merged.Add(key, level);
                    continue;
                }

                bool stronger = Math.Abs(level.Exposure) >
                    Math.Abs(existing.Exposure);
                if (stronger)
                    existing.Exposure = level.Exposure;
                if (level.Priority > existing.Priority ||
                    (level.Priority == existing.Priority && stronger))
                {
                    existing.Priority = level.Priority;
                    existing.LineColor = level.LineColor;
                    existing.LineWidth = level.LineWidth;
                    existing.LineStyle = level.LineStyle;
                    existing.MarkerColor = level.MarkerColor;
                    existing.Kind = level.Kind;
                }
            }

            levels.Clear();
            levels.AddRange(merged.Values);
        }

        private double GetGroupingSize()
        {
            try
            {
                if (Symbol != null && Symbol.TickSize > 0.0)
                    return Symbol.TickSize;
                if (CurrentChart != null && CurrentChart.TickSize > 0.0)
                    return CurrentChart.TickSize;
            }
            catch
            {
            }

            return 0.000001;
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            base.OnPaintChart(args);

            if (args == null || args.Graphics == null ||
                CurrentChart == null || CurrentChart.MainWindow == null)
                return;

            TladeSnapshot current;
            DataState state;
            string message;
            lock (stateSync)
            {
                current = snapshot;
                state = dataState;
                message = statusMessage;
            }

            Graphics graphics = args.Graphics;
            Rectangle chartRectangle =
                CurrentChart.MainWindow.ClientRectangle;
            GraphicsState graphicsState = graphics.Save();

            try
            {
                graphics.SetClip(chartRectangle);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (current != null)
                {
                    if (ShowGexProfile)
                        DrawGexProfile(
                            graphics,
                            chartRectangle,
                            current.Profile);

                    List<RenderLevel> levels = BuildRenderLevels(
                        current,
                        GetReferencePrice());
                    DrawLevels(graphics, chartRectangle, levels);
                }

                DrawStatusControls(
                    graphics,
                    chartRectangle,
                    state,
                    message);
            }
            finally
            {
                graphics.Restore(graphicsState);
            }
        }

        private void DrawGexProfile(
            Graphics graphics,
            Rectangle chartRectangle,
            List<TladeProfilePoint> points)
        {
            if (points == null || points.Count == 0 ||
                ProfileOpacity <= 0 || ProfileWidth <= 0)
                return;

            double maximum = points.Max(point =>
                point == null ? 0.0 : Math.Abs(point.Exposure));
            if (maximum <= 0.0)
                return;

            int widthLimit = Math.Max(20, ProfileWidth);
            int offset = Math.Max(0, ProfileHorizontalOffset);
            int barHeight = Math.Max(1, ProfileBarHeight);
            Color positive = ApplyOpacity(
                PositiveProfileColor,
                ProfileOpacity);
            Color negative = ApplyOpacity(
                NegativeProfileColor,
                ProfileOpacity);

            using (SolidBrush positiveBrush = new SolidBrush(positive))
            using (SolidBrush negativeBrush = new SolidBrush(negative))
            {
                for (int i = 0; i < points.Count; i++)
                {
                    TladeProfilePoint point = points[i];
                    if (point == null || !IsUsablePrice(point.Price) ||
                        point.Exposure == 0.0)
                        continue;

                    float y;
                    try
                    {
                        y = (float)CurrentChart.MainWindow
                            .CoordinatesConverter.GetChartY(point.Price);
                    }
                    catch
                    {
                        continue;
                    }

                    if (y < chartRectangle.Top - barHeight ||
                        y > chartRectangle.Bottom + barHeight)
                        continue;

                    double ratio = Math.Abs(point.Exposure) / maximum;
                    int width = Math.Max(
                        1,
                        (int)Math.Round(
                            widthLimit * ScaleProfileRatio(ratio)));
                    float top = y - barHeight * 0.5f;
                    RectangleF bar;

                    if (ProfileAlignment ==
                        GammaExposureProfileAlignment.Right)
                    {
                        float right = Math.Max(
                            chartRectangle.Left,
                            chartRectangle.Right - offset);
                        float left = Math.Max(
                            chartRectangle.Left,
                            right - width);
                        bar = new RectangleF(
                            left,
                            top,
                            right - left,
                            barHeight);
                    }
                    else
                    {
                        float left = Math.Min(
                            chartRectangle.Right,
                            chartRectangle.Left + offset);
                        float right = Math.Min(
                            chartRectangle.Right,
                            left + width);
                        bar = new RectangleF(
                            left,
                            top,
                            right - left,
                            barHeight);
                    }

                    graphics.FillRectangle(
                        point.Exposure < 0.0
                            ? negativeBrush
                            : positiveBrush,
                        bar);
                }
            }
        }

        private double ScaleProfileRatio(double ratio)
        {
            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            switch (ProfileScale)
            {
                case GammaExposureProfileScale.SquareRoot:
                    return Math.Sqrt(ratio);
                case GammaExposureProfileScale.Logarithmic:
                    return Math.Log(1.0 + 9.0 * ratio) / Math.Log(10.0);
                default:
                    return ratio;
            }
        }

        private void DrawLevels(
            Graphics graphics,
            Rectangle chartRectangle,
            List<RenderLevel> levels)
        {
            if (levels == null || levels.Count == 0)
                return;

            using (Font font = CloneMarkerFont())
            {
                List<RenderMarker> markers = BuildMarkerLayout(
                    graphics,
                    font,
                    chartRectangle,
                    levels);
                DrawHorizontalLines(graphics, chartRectangle, markers);

                for (int i = 0; i < markers.Count; i++)
                    DrawMarker(
                        graphics,
                        font,
                        chartRectangle,
                        markers[i]);
            }
        }

        private List<RenderMarker> BuildMarkerLayout(
            Graphics graphics,
            Font font,
            Rectangle chartRectangle,
            List<RenderLevel> levels)
        {
            List<RenderMarker> markers = new List<RenderMarker>();
            float horizontalPadding = Math.Max(2, MarkerPadding);
            float verticalPadding = 2.0f;
            float height = Math.Max(
                1.0f,
                font.GetHeight(graphics) + verticalPadding * 2.0f);
            float markerTop = chartRectangle.Top + height * 0.5f + 1.0f;
            float markerBottom = chartRectangle.Bottom -
                height * 0.5f - 1.0f;

            if (markerBottom < markerTop)
                return markers;

            for (int i = 0; i < levels.Count; i++)
            {
                RenderLevel level = levels[i];
                if (level == null || !IsUsablePrice(level.Price))
                    continue;

                float lineY;
                try
                {
                    lineY = (float)CurrentChart.MainWindow
                        .CoordinatesConverter.GetChartY(level.Price);
                }
                catch
                {
                    continue;
                }

                if (float.IsNaN(lineY) || float.IsInfinity(lineY) ||
                    lineY < chartRectangle.Top - 4.0f ||
                    lineY > chartRectangle.Bottom + 4.0f)
                    continue;

                string markerText = BuildMarkerText(level);
                SizeF measured = graphics.MeasureString(markerText, font);
                markers.Add(new RenderMarker
                {
                    Level = level,
                    LineY = lineY,
                    MarkerY = Math.Max(
                        markerTop,
                        Math.Min(markerBottom, lineY)),
                    Width = Math.Max(
                        1.0f,
                        measured.Width + horizontalPadding * 2.0f),
                    Height = height,
                    Text = markerText
                });
            }

            markers.Sort(delegate(RenderMarker a, RenderMarker b)
            {
                int byY = a.LineY.CompareTo(b.LineY);
                if (byY != 0)
                    return byY;

                bool aCall = a.Level.Kind == TladeLevelKind.CallWall;
                bool bCall = b.Level.Kind == TladeLevelKind.CallWall;
                if (aCall != bCall)
                    return aCall ? -1 : 1;
                return b.Level.Priority.CompareTo(a.Level.Priority);
            });

            List<float> occupied = new List<float>();
            for (int i = 0; i < markers.Count; i++)
            {
                RenderMarker marker = markers[i];
                marker.MarkerY = ResolveMarkerY(
                    marker.MarkerY,
                    markerTop,
                    markerBottom,
                    height,
                    occupied);
                occupied.Add(marker.MarkerY);
            }

            return markers;
        }

        private void DrawHorizontalLines(
            Graphics graphics,
            Rectangle chartRectangle,
            List<RenderMarker> markers)
        {
            double groupingSize = GetGroupingSize();
            Dictionary<long, RenderMarker> lineByPrice =
                new Dictionary<long, RenderMarker>();

            for (int i = 0; i < markers.Count; i++)
            {
                RenderMarker marker = markers[i];
                long key = (long)Math.Round(
                    marker.Level.Price / groupingSize);
                RenderMarker existing;
                if (!lineByPrice.TryGetValue(key, out existing) ||
                    Math.Abs(marker.Level.Exposure) >
                        Math.Abs(existing.Level.Exposure) ||
                    (Math.Abs(marker.Level.Exposure) ==
                        Math.Abs(existing.Level.Exposure) &&
                     marker.Level.Priority > existing.Level.Priority))
                    lineByPrice[key] = marker;
            }

            foreach (KeyValuePair<long, RenderMarker> pair in lineByPrice)
            {
                RenderMarker marker = pair.Value;
                using (Pen pen = new Pen(
                    marker.Level.LineColor,
                    Math.Max(1, marker.Level.LineWidth)))
                {
                    pen.DashStyle = ToDashStyle(
                        marker.Level.LineStyle);
                    graphics.DrawLine(
                        pen,
                        chartRectangle.Left,
                        marker.LineY,
                        chartRectangle.Right,
                        marker.LineY);
                }
            }
        }

        private void DrawMarker(
            Graphics graphics,
            Font font,
            Rectangle chartRectangle,
            RenderMarker marker)
        {
            if (marker == null || marker.Level == null)
                return;

            bool right = MarkerAlignment ==
                GammaExposureMarkerAlignment.Right;
            float x = right
                ? chartRectangle.Right - 4.0f - marker.Width
                : chartRectangle.Left + 4.0f;
            float y = marker.MarkerY - marker.Height * 0.5f;
            Color background = ApplyOpacity(
                marker.Level.MarkerColor,
                MarkerBackgroundOpacity);
            Color textColor = AutomaticMarkerTextColor
                ? GetContrastingTextColor(marker.Level.MarkerColor)
                : MarkerTextColor;

            using (SolidBrush backgroundBrush = new SolidBrush(background))
            using (SolidBrush textBrush = new SolidBrush(textColor))
            using (Pen elbowPen = new Pen(background, 1.0f))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoWrap;

                float markerEdgeX = right ? x : x + marker.Width;
                DrawElbowLeader(
                    graphics,
                    elbowPen,
                    markerEdgeX,
                    marker.LineY,
                    marker.MarkerY,
                    right);

                graphics.FillRectangle(
                    backgroundBrush,
                    x,
                    y,
                    marker.Width,
                    marker.Height);
                graphics.DrawString(
                    marker.Text,
                    font,
                    textBrush,
                    new RectangleF(
                        x + Math.Max(2, MarkerPadding),
                        y,
                        Math.Max(
                            1.0f,
                            marker.Width -
                                Math.Max(2, MarkerPadding) * 2.0f),
                        marker.Height),
                    format);
            }
        }

        private static void DrawElbowLeader(
            Graphics graphics,
            Pen pen,
            float markerEdgeX,
            float lineY,
            float markerY,
            bool markerOnRight)
        {
            if (Math.Abs(markerY - lineY) < 0.5f)
                return;

            const float elbowLength = 12.0f;
            float elbowX = markerEdgeX +
                (markerOnRight ? -elbowLength : elbowLength);

            graphics.DrawLine(
                pen,
                markerEdgeX,
                markerY,
                elbowX,
                markerY);
            graphics.DrawLine(
                pen,
                elbowX,
                markerY,
                elbowX,
                lineY);
        }

        private static float ResolveMarkerY(
            float desiredY,
            float minimum,
            float maximum,
            float markerSpacing,
            List<float> occupiedMarkerYs)
        {
            float candidate = Math.Max(
                minimum,
                Math.Min(maximum, desiredY));
            if (IsMarkerSlotAvailable(
                candidate,
                markerSpacing,
                occupiedMarkerYs))
                return candidate;

            bool preferBelow = maximum - candidate >= candidate - minimum;
            float best = float.NaN;
            float bestDistance = float.MaxValue;
            const float tieTolerance = 0.0001f;

            for (int i = 0; i < occupiedMarkerYs.Count; i++)
            {
                float occupied = occupiedMarkerYs[i];
                for (int direction = -1; direction <= 1; direction += 2)
                {
                    float option = occupied + direction * markerSpacing;
                    if (option < minimum || option > maximum ||
                        !IsMarkerSlotAvailable(
                            option,
                            markerSpacing,
                            occupiedMarkerYs))
                        continue;

                    float distance = Math.Abs(option - candidate);
                    bool replace = float.IsNaN(best) ||
                        distance < bestDistance - tieTolerance;
                    if (!replace &&
                        Math.Abs(distance - bestDistance) <= tieTolerance)
                    {
                        bool optionPreferred = preferBelow
                            ? option > candidate
                            : option < candidate;
                        bool bestPreferred = preferBelow
                            ? best > candidate
                            : best < candidate;
                        replace = optionPreferred && !bestPreferred;
                    }

                    if (replace)
                    {
                        best = option;
                        bestDistance = distance;
                    }
                }
            }

            return float.IsNaN(best) ? candidate : best;
        }

        private static bool IsMarkerSlotAvailable(
            float candidate,
            float markerSpacing,
            List<float> occupiedMarkerYs)
        {
            for (int i = 0; i < occupiedMarkerYs.Count; i++)
            {
                if (Math.Abs(occupiedMarkerYs[i] - candidate) <
                    markerSpacing)
                    return false;
            }

            return true;
        }

        private string BuildMarkerText(RenderLevel level)
        {
            bool isWall =
                level.Kind == TladeLevelKind.CallWall ||
                level.Kind == TladeLevelKind.PutWall;
            string exposure = ShowExposureInMarkers &&
                isWall &&
                Math.Abs(level.Exposure) > 0.0
                    ? " | " + FormatExposure(level.Exposure)
                    : string.Empty;
            return FormatPrice(level.Price) + " | " +
                level.Label + exposure;
        }

        private string FormatPrice(double price)
        {
            try
            {
                if (Symbol != null)
                    return Symbol.FormatPrice(price);
            }
            catch
            {
            }

            return price.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string FormatExposure(double value)
        {
            double magnitude = Math.Abs(value);
            string sign = value < 0.0 ? "-" : string.Empty;

            if (magnitude >= 1000000000.0)
                return sign + "$" +
                    (magnitude / 1000000000.0).ToString(
                        "0.##",
                        CultureInfo.InvariantCulture) + "B";
            if (magnitude >= 1000000.0)
                return sign + "$" +
                    (magnitude / 1000000.0).ToString(
                        "0.##",
                        CultureInfo.InvariantCulture) + "M";
            if (magnitude >= 1000.0)
                return sign + "$" +
                    (magnitude / 1000.0).ToString(
                        "0.##",
                        CultureInfo.InvariantCulture) + "K";
            return sign + "$" + magnitude.ToString(
                "0.##",
                CultureInfo.InvariantCulture);
        }

        private static DashStyle ToDashStyle(
            TradingPlatform.BusinessLayer.LineStyle style)
        {
            switch (style)
            {
                case TradingPlatform.BusinessLayer.LineStyle.Dash:
                    return DashStyle.Dash;
                case TradingPlatform.BusinessLayer.LineStyle.Dot:
                    return DashStyle.Dot;
                case TradingPlatform.BusinessLayer.LineStyle.DashDot:
                    return DashStyle.DashDot;
                default:
                    return DashStyle.Solid;
            }
        }

        private static Color ApplyOpacity(Color color, int opacityPercent)
        {
            int opacity = Math.Max(0, Math.Min(100, opacityPercent));
            int alpha = (int)Math.Round(color.A * opacity / 100.0);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static Color GetContrastingTextColor(Color color)
        {
            double red = ToLinearColor(color.R / 255.0);
            double green = ToLinearColor(color.G / 255.0);
            double blue = ToLinearColor(color.B / 255.0);
            double luminance =
                0.2126 * red + 0.7152 * green + 0.0722 * blue;
            return luminance > 0.42 ? Color.Black : Color.White;
        }

        private static double ToLinearColor(double component)
        {
            return component <= 0.04045
                ? component / 12.92
                : Math.Pow((component + 0.055) / 1.055, 2.4);
        }

        private void DrawStatusControls(
            Graphics graphics,
            Rectangle chartRectangle,
            DataState state,
            string message)
        {
            if (!ShowStatusDot && !ShowRefreshButton)
            {
                lock (stateSync)
                    refreshHitBox = Rectangle.Empty;
                return;
            }

            const int margin = 8;
            const int dotSize = 9;
            const int buttonSize = 18;
            const int gap = 4;
            int width = (ShowStatusDot ? dotSize : 0) +
                (ShowStatusDot && ShowRefreshButton ? gap : 0) +
                (ShowRefreshButton ? buttonSize : 0);
            int height = Math.Max(dotSize, buttonSize);
            int x;
            int y;

            switch (ControlPlacement)
            {
                case GammaExposureControlPlacement.TopLeft:
                    x = chartRectangle.Left + margin;
                    y = chartRectangle.Top + margin;
                    break;
                case GammaExposureControlPlacement.BottomLeft:
                    x = chartRectangle.Left + margin;
                    y = chartRectangle.Bottom - margin - height;
                    break;
                case GammaExposureControlPlacement.BottomRight:
                    x = chartRectangle.Right - margin - width;
                    y = chartRectangle.Bottom - margin - height;
                    break;
                default:
                    x = chartRectangle.Right - margin - width;
                    y = chartRectangle.Top + margin;
                    break;
            }

            int cursorX = x;
            if (ShowStatusDot)
            {
                Color statusColor = ResolveStatusColor(state);
                float dotY = y + (height - dotSize) * 0.5f;
                using (SolidBrush brush = new SolidBrush(statusColor))
                using (Pen outline = new Pen(
                    Color.FromArgb(180, 0, 0, 0),
                    1.0f))
                {
                    graphics.FillEllipse(
                        brush,
                        cursorX,
                        dotY,
                        dotSize,
                        dotSize);
                    graphics.DrawEllipse(
                        outline,
                        cursorX,
                        dotY,
                        dotSize,
                        dotSize);
                }

                cursorX += dotSize;
                if (ShowRefreshButton)
                    cursorX += gap;
            }

            Rectangle nextHitBox = Rectangle.Empty;
            if (ShowRefreshButton)
            {
                nextHitBox = new Rectangle(
                    cursorX,
                    y,
                    buttonSize,
                    buttonSize);
                DrawRefreshGlyph(
                    graphics,
                    nextHitBox,
                    state == DataState.Loading
                        ? Color.FromArgb(140, 148, 163, 184)
                        : Color.White);
            }

            lock (stateSync)
                refreshHitBox = nextHitBox;
        }

        private static Color ResolveStatusColor(DataState state)
        {
            switch (state)
            {
                case DataState.Loading:
                    return Color.FromArgb(245, 158, 11);
                case DataState.Ready:
                    return Color.FromArgb(34, 197, 94);
                case DataState.Error:
                    return Color.FromArgb(239, 68, 68);
                default:
                    return Color.FromArgb(148, 163, 184);
            }
        }

        private static void DrawRefreshGlyph(
            Graphics graphics,
            Rectangle bounds,
            Color color)
        {
            using (Pen pen = new Pen(color, 1.5f))
            using (SolidBrush brush = new SolidBrush(color))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                RectangleF arc = new RectangleF(
                    bounds.Left + 3.5f,
                    bounds.Top + 3.5f,
                    bounds.Width - 7.0f,
                    bounds.Height - 7.0f);
                graphics.DrawArc(pen, arc, 35.0f, 285.0f);

                float arrowX = bounds.Right - 3.5f;
                float arrowY = bounds.Top + 6.0f;
                graphics.FillPolygon(
                    brush,
                    new[]
                    {
                        new PointF(arrowX, arrowY),
                        new PointF(arrowX - 5.0f, arrowY - 1.0f),
                        new PointF(arrowX - 2.0f, arrowY + 3.5f)
                    });
            }
        }

        private static class SessionSchedule
        {
            private static readonly TimeZoneInfo eastern = FindTimeZone(
                "Eastern Standard Time",
                TimeSpan.FromHours(-5));
            private static readonly TimeZoneInfo tokyo = FindTimeZone(
                "Tokyo Standard Time",
                TimeSpan.FromHours(9));
            private static readonly TimeZoneInfo london = FindTimeZone(
                "GMT Standard Time",
                TimeSpan.Zero);

            public static SessionCheckpoint Latest(DateTime utcNow)
            {
                List<SessionCheckpoint> checkpoints = Build(
                    utcNow,
                    -8,
                    1);
                SessionCheckpoint latest = null;

                for (int i = 0; i < checkpoints.Count; i++)
                {
                    if (checkpoints[i].Utc > utcNow)
                        break;
                    latest = checkpoints[i];
                }

                return latest;
            }

            private static List<SessionCheckpoint> Build(
                DateTime utcNow,
                int firstDayOffset,
                int lastDayOffset)
            {
                Dictionary<string, SessionCheckpoint> unique =
                    new Dictionary<string, SessionCheckpoint>(
                        StringComparer.Ordinal);

                AddSeries(
                    unique,
                    utcNow,
                    eastern,
                    "Globex reopen",
                    new TimeSpan(18, 0, 0),
                    firstDayOffset,
                    lastDayOffset,
                    true);
                AddSeries(
                    unique,
                    utcNow,
                    tokyo,
                    "Tokyo open",
                    new TimeSpan(9, 0, 0),
                    firstDayOffset,
                    lastDayOffset,
                    false);
                AddSeries(
                    unique,
                    utcNow,
                    london,
                    "London open",
                    new TimeSpan(8, 0, 0),
                    firstDayOffset,
                    lastDayOffset,
                    false);
                AddSeries(
                    unique,
                    utcNow,
                    eastern,
                    "US data window",
                    new TimeSpan(8, 30, 0),
                    firstDayOffset,
                    lastDayOffset,
                    false);
                AddSeries(
                    unique,
                    utcNow,
                    eastern,
                    "NY cash open",
                    new TimeSpan(9, 30, 0),
                    firstDayOffset,
                    lastDayOffset,
                    false);
                AddSeries(
                    unique,
                    utcNow,
                    eastern,
                    "NY cash close",
                    new TimeSpan(16, 0, 0),
                    firstDayOffset,
                    lastDayOffset,
                    false);

                List<SessionCheckpoint> result =
                    new List<SessionCheckpoint>(unique.Values);
                result.Sort(delegate(
                    SessionCheckpoint a,
                    SessionCheckpoint b)
                {
                    return a.Utc.CompareTo(b.Utc);
                });
                return result;
            }

            private static void AddSeries(
                Dictionary<string, SessionCheckpoint> output,
                DateTime utcNow,
                TimeZoneInfo timeZone,
                string name,
                TimeSpan localTime,
                int firstDayOffset,
                int lastDayOffset,
                bool globexDays)
            {
                DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(
                    utcNow.Kind == DateTimeKind.Utc
                        ? utcNow
                        : DateTime.SpecifyKind(
                            utcNow,
                            DateTimeKind.Utc),
                    timeZone);

                for (int offset = firstDayOffset;
                    offset <= lastDayOffset;
                    offset++)
                {
                    DateTime localDate = localNow.Date.AddDays(offset);
                    if (globexDays)
                    {
                        if (localDate.DayOfWeek == DayOfWeek.Friday ||
                            localDate.DayOfWeek == DayOfWeek.Saturday)
                            continue;
                    }
                    else if (localDate.DayOfWeek == DayOfWeek.Saturday ||
                        localDate.DayOfWeek == DayOfWeek.Sunday)
                    {
                        continue;
                    }

                    DateTime local = DateTime.SpecifyKind(
                        localDate.Add(localTime),
                        DateTimeKind.Unspecified);
                    if (timeZone.IsInvalidTime(local))
                        continue;

                    DateTime utc;
                    try
                    {
                        utc = TimeZoneInfo.ConvertTimeToUtc(
                            local,
                            timeZone);
                    }
                    catch
                    {
                        continue;
                    }

                    SessionCheckpoint checkpoint =
                        new SessionCheckpoint
                        {
                            Name = name,
                            Utc = utc
                        };
                    output[checkpoint.Key] = checkpoint;
                }
            }

            private static TimeZoneInfo FindTimeZone(
                string id,
                TimeSpan fallbackOffset)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch
                {
                    return TimeZoneInfo.CreateCustomTimeZone(
                        id + " fallback",
                        fallbackOffset,
                        id,
                        id);
                }
            }
        }
    }
}
