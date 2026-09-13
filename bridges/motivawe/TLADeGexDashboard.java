// TLADe GEX Levels — MotiveWave — release 3.5.0 (13 September 2026)
// Canonical feature set = the TradingView ES indicator: same names, colours, line styles, wall-flip
// rule (two 5-minute closes), breakout rule (closed bars, opposite-bar invalidation, keep last N),
// confluence zones (% of EM), silent above 1H, level-cross signals. Layout (right-edge chips,
// profile) is MotiveWave's own.
package study_examples;

import java.awt.BasicStroke;
import java.awt.Color;
import java.awt.Font;
import java.awt.FontMetrics;
import java.awt.Graphics2D;
import java.awt.Rectangle;
import java.awt.RenderingHints;
import java.awt.Stroke;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

import com.motivewave.platform.sdk.common.DataContext;
import com.motivewave.platform.sdk.common.DataSeries;
import com.motivewave.platform.sdk.common.Defaults;
import com.motivewave.platform.sdk.common.DrawContext;
import com.motivewave.platform.sdk.common.Instrument;
import com.motivewave.platform.sdk.common.NVP;
import com.motivewave.platform.sdk.common.desc.BooleanDescriptor;
import com.motivewave.platform.sdk.common.desc.ColorDescriptor;
import com.motivewave.platform.sdk.common.desc.DiscreteDescriptor;
import com.motivewave.platform.sdk.common.desc.DoubleDescriptor;
import com.motivewave.platform.sdk.common.desc.IntegerDescriptor;
import com.motivewave.platform.sdk.common.desc.SettingGroup;
import com.motivewave.platform.sdk.common.desc.SettingTab;
import com.motivewave.platform.sdk.common.desc.SettingsDescriptor;
import com.motivewave.platform.sdk.common.desc.StringDescriptor;
import com.motivewave.platform.sdk.draw.Figure;
import com.motivewave.platform.sdk.study.RuntimeDescriptor;
import com.motivewave.platform.sdk.study.Study;
import com.motivewave.platform.sdk.study.StudyHeader;

/**
 * TLADe GEX Dashboard — MotiveWave port of the NinjaTrader 8 {@code TLADeGexDashboardNT}
 * indicator.
 *
 * <p>Renders a Gamma-Exposure (GEX) "dashboard" from a single data string exported by the
 * TLADe Terminal. The string carries three optional sections, pipe-separated:</p>
 *
 * <pre>  S:&lt;spread&gt; | L:&lt;levels&gt; | P:&lt;profile&gt;</pre>
 *
 * <ul>
 *   <li><b>S:</b> real-time ES&ndash;SPX spread (overrides the manual spread setting).</li>
 *   <li><b>L:</b> {@code ;}-separated level records {@code strike,type,label,tooltip,magnitude}.
 *       Types: GEX walls {@code CW/PW/GL}, system {@code ZG/MP/EH/EL/VH/VL}, structure
 *       {@code PDH/PDL/PWH/PWL}.</li>
 *   <li><b>P:</b> {@code ;}-separated profile rows {@code strike,value,sign} drawn as a
 *       right-edge histogram (call/put colored).</li>
 * </ul>
 *
 * <p>All strikes are quoted in <b>ES points</b>; {@link #convertPrice} maps them to the chart's
 * display instrument (ES/SPX/SPY/NQ/NDX/QQQ) using the configured spreads.</p>
 *
 * <p><b>Layout differs from NinjaTrader.</b> NT8 anchors every element in bar/offset space
 * ({@code RightOffsetBars}, {@code LabelPaddingBars}, {@code ProfileOffsetBars}). MotiveWave
 * Figures paint in pixel space, so this port draws level lines across the full plot width,
 * places labels at the right edge, and renders the profile histogram as right-aligned pixel
 * bars whose length is scaled by {@code ProfileWidthBars * barWidth}. The behaviour (which
 * levels show, colors, filtering, profile semantics, auto-fetch) is faithful; only the
 * coordinate model is adapted. Drawing is done in a single custom {@link Figure} because the
 * SDK's bundled {@code Enums} nested types do not resolve at compile time in this build.</p>
 *
 * <p><b>Auto-fetch.</b> When enabled, the study pulls the latest levels from the TLADe Cloud
 * Function on load and at the six daily ET windows, on a background thread (the SDK draw/calc
 * threads are never blocked on network I/O). With an API key it fetches live data; without one
 * it fetches free delayed (3-business-day) data and shows a banner.</p>
 */
@StudyHeader(
    namespace = "com.custom",
    id = "TLADE_GEX_DASHBOARD",
    name = "TLADe GEX Dashboard",
    label = "TLADe GEX",
    desc = "Gamma-Exposure dashboard from a TLADe Terminal data string (S:/L:/P:). Draws GEX, "
         + "system and structure levels with labels plus a right-edge GEX profile histogram. "
         + "Strikes are in ES points and mapped to the chart instrument via configurable spreads. "
         + "Optional background auto-fetch from the TLADe API (live with key, delayed without).",
    menu = "My Studies",
    overlay = true,
    studyOverlay = true,
    supportsBarUpdates = true)
public class TLADeGexDashboard extends Study
{
  // ---- Setting keys ----------------------------------------------------------------------------
  final static String DISPLAY_TICKER = "displayTicker";
  final static String ES_SPX_SPREAD  = "esSpxSpread";
  final static String NQ_NDX_SPREAD  = "nqNdxSpread";

  final static String GEX_DATA = "gexData";

  final static String SHOW_GEX       = "showGex";
  final static String SHOW_SYSTEM    = "showSystem";
  final static String SHOW_STRUCTURE = "showStructure";
  final static String SHOW_BREAKOUT  = "showBreakout";
  final static String SHOW_CHARM_MAGNET = "showCharmMagnet";
  // v1.3 — Session AVWAP local compute (= mirrors the Pine indicator)
  final static String SHOW_AVWAP_ASIA   = "showAvwapAsia";
  final static String SHOW_AVWAP_EU     = "showAvwapEU";
  final static String SHOW_AVWAP_US     = "showAvwapUS";
  final static String SHOW_AVWAP_PD     = "showAvwapPD";
  final static String SHOW_AVWAP_HIST   = "showAvwapHistorical";
  final static String AVWAP_LINE_WIDTH  = "avwapLineWidth";
  final static String SHOW_AVWAP_LABELS = "showAvwapLabels";
  final static String MAX_GEX        = "maxGex";
  final static String SHOW_ONLY_NEAR = "showOnlyNear";
  final static String NEAR_PCT       = "nearPct";
  final static String ENABLE_THRESH  = "enableThreshold";
  final static String GEX_THRESHOLD  = "gexThreshold";

  final static String THEME          = "theme";
  final static String BAR_COLOR      = "barColorStyle";
  final static String CUSTOM_CALL    = "customCallColor";
  final static String CUSTOM_PUT     = "customPutColor";

  final static String SHOW_LABELS    = "showLabels";
  final static String LABEL_SIZE     = "labelFontSize";

  final static String SHOW_PROFILE   = "showProfile";
  final static String PROFILE_WIDTH  = "profileWidthBars";
  final static String PROFILE_HEIGHT = "profileBarHeightTicks";
  final static String PROFILE_SCALE  = "profileScaleMax";
  final static String AUTO_SCALE     = "autoScaleProfileMax";
  final static String MAX_PROFILE    = "maxProfileRows";

  final static String AUTO_FETCH     = "autoFetch";
  final static String API_KEY        = "apiKey";

  final static String SHOW_DELTA_FLIP = "showDeltaFlip";
  final static String LINE_STYLE      = "lineStyle";
  final static String SHOW_ABOVE_H1   = "showAboveH1";
  final static String SHOW_BOS_M = "showBosM", SHOW_BOS_W = "showBosW", SHOW_BOS_D = "showBosD",
                      SHOW_BOS_H4 = "showBosH4", SHOW_BOS_H1 = "showBosH1", BOS_KEEP = "bosKeepPerTf";
  final static String SHOW_CONFLUENCE = "showConfluence", CONF_MIN_SIZE = "confluenceMinSize", CONF_EM_PCT = "confluenceEmPct";
  final static String ENABLE_ALERTS = "enableAlerts", ALERT_WALLS = "alertWalls", ALERT_SYSTEM = "alertSystem", ALERT_BOS = "alertBos";
  final static String SIG_WALL = "tladeWall", SIG_SYSTEM = "tladeSystem", SIG_BO = "tladeBreakout";
  final static String SHOW_SESSIONS = "showSessions", SHOW_BOX_ASIA = "showBoxAsia", SHOW_BOX_EU = "showBoxEU",
                      SHOW_BOX_PRE = "showBoxPre", SHOW_BOX_US = "showBoxUS", SHOW_SESSIONS_HIST = "showSessionsHist";
  final static String SHOW_STATUS    = "showStatus";
  final static String STATUS_POS     = "statusPos";   // TL / TR / BL / BR

  // ---- Auto-fetch constants (mirrors NT8) ------------------------------------------------------
  private static final String API_URL = "https://europe-west1-omggex.cloudfunctions.net/indicatorData";
  // 6 fetch times per day (ET), as minutes-of-day.
  // ORDERED ASCENDING (= v1.3 scheduler scans for the smallest slot > nowMins).
  // 02:05 EU, 08:05 PRE, 09:35 RTH, 10:35 OPRANGE, 13:05 PWRHOUR, 18:05 ASIA.
  private static final int[] FETCH_MINUTES_ET = {125, 485, 575, 635, 785, 1085};

  // ---- Parsed model ----------------------------------------------------------------------------
  private static class LevelEntry
  {
    double esStrike;
    String type;
    String label;
    double magnitude;
  }

  private static class ProfileEntry
  {
    double esStrike;
    double value;
    double sign; // -1 put, +1 call (color only)
  }

  private final List<LevelEntry> levels = new ArrayList<>();
  private final List<ProfileEntry> profile = new ArrayList<>();

  // ---- Precomputed draw model (built in calculateValues, painted by the figure) ----------------
  // The figure is fully self-contained: it reads only these lists plus DrawContext bounds/translate,
  // never DataContext at draw time (mirrors the working SigmaZones figures).
  private static class DrawLevel
  {
    double price;       // converted to display-instrument price
    Color color;
    int style;          // 0 solid, 1 dashed, 2 dotted
    int lineWidth;
    String label;       // "" when labels hidden
    String kind;        // "wall" | "system" | "bo" | "" — what the cross alerts watch
    String alertName;
  }

  private static class DrawBox
  {
    long t0, t1;        // first and last bar time of the session run
    double hi, lo;
    Color color;
  }
  private volatile List<DrawBox> drawBoxes = new ArrayList<>();

  private static class DrawZone
  {
    double top, bottom;
    Color color;
    String label;
  }

  private static class DrawProf
  {
    double price;       // converted price (bar vertical center)
    double frac;        // |value| / maxAbs  (length fraction; pixels resolved at draw time)
    Color color;        // already alpha-blended
  }

  // Published draw model. buildDrawModel fills fresh local lists and swaps them
  // in via a single volatile reference assignment, so the draw thread (EDT)
  // never observes a half-cleared/half-filled list while calc rebuilds it.
  private volatile List<DrawLevel> drawLevels = new ArrayList<>();
  private volatile List<DrawProf> drawProfile = new ArrayList<>();
  // v1.3 — Session AVWAP arrays (one entry per series bar). Populated in
  // calculateValues, consumed by DashboardFigure.draw. NaN at indices where
  // the session hadn't started yet (= no accumulation possible).
  private volatile double[] avwapAsia = new double[0];
  private volatile double[] avwapEU   = new double[0];
  private volatile double[] avwapUS   = new double[0];
  private volatile double[] avwapPD   = new double[0];
  private volatile long[]   avwapTimes = new long[0]; // millis epoch per bar
  // Current futures-day Asia anchor in ET millis-of-epoch (= used to clip the
  // polyline when "Show Historical AVWAP" is off).
  private volatile long currentAsiaAnchorMs = 0L;
  private int profWidthBars = 70;
  private int profHeightTicks = 8;

  // Last DataContext seen, so the background fetch thread can request a recalc on completion.
  private volatile DataContext lastCtx = null;

  // Diagnostic snapshot (shown by the status banner when SHOW_STATUS is on).
  private volatile String statusText = "TLADe GEX: initializing…";

  // Last successful fetch time as ET HH:mm, shown in the status banner.
  private volatile String lastFetchEt = "—";

  // Last fetch error (HTTP code or exception class) — surfaced in the status
  // banner instead of "updated — ET" when a fetch does not yield a payload.
  // Cleared on the next successful fetch.
  private volatile String lastFetchError = null;

  /** Guard: fires only once per Study instance. calculateValues is our
   *  reliable "first render" hook — MotiveWave rebuilds studies from
   *  saved settings on workspace open WITHOUT calling onLoad again, so
   *  a workspace with the TLADe study saved would come up empty until
   *  the next scheduled slot fired. Kicking startFetch(true) here gives
   *  the same behaviour a fresh drop-on-chart already has. */
  private volatile boolean initialFetchKicked = false;

  // Theme / bar brushes resolved from settings (recomputed on each rebuild).
  private Color posColor = new Color(0x22, 0xc5, 0x5e);
  private Color negColor = new Color(0xef, 0x44, 0x44);
  private Color profCallColor = negColor;
  private Color profPutColor  = posColor;

  // Auto-fetch bookkeeping.
  // v1.3 — absolute-time scheduler. Replaces the legacy tick-driven
  // maybeScheduleFetch() that was wired into calculateValues/onBarUpdate
  // and silently skipped slots when no tick arrived in the 5-minute
  // matching window (= cash-index charts pre-RTH, futures quiet hours).
  // The new path schedules exactly 7 fetches per trading day (1 mount + 6
  // absolute ET slots) regardless of tick activity.
  private volatile java.util.concurrent.ScheduledExecutorService scheduler;
  private volatile String fetchedData = null; // set by background thread, consumed on next calc
  private int lastFetchMinuteET = -1;
  private long lastFetchTimeMs = 0L;
  private boolean delayedMode = false;
  private volatile boolean fetchInFlight = false;

  // Effective GEX data string actually parsed: the latest fetched payload when
  // present, otherwise the manually pasted GEX_DATA setting. Held in a field so a
  // fetch result survives later recalcs WITHOUT writing it back into settings
  // from inside calc (which can re-enter onSettingsUpdated → recalculate).
  private String lastData = null;
  // ES-SPX spread carried by an "S:" data prefix. Overrides the manual spread for
  // price conversion, again without mutating settings from inside calc.
  private double spreadOverride = Double.NaN;
  private double ndxQqqRatio = Double.NaN;     // NDX→QQQ ratio from the data string (R:)
  private volatile List<DrawZone> drawZones = new ArrayList<>();
  private final java.util.Map<Double, Boolean> wallFlipped = new java.util.HashMap<>();
  private final java.util.Map<String, Integer> alertedAt = new java.util.HashMap<>();

  // ================================================================================================
  // Initialization
  // ================================================================================================

  @Override
  public void initialize(Defaults defaults)
  {
    SettingsDescriptor sd = createSD();

    // --- Ticker ---------------------------------------------------------------------------------
    SettingTab tickerTab = sd.addTab("Ticker");
    SettingGroup tickerGrp = tickerTab.addGroup("Ticker Settings");
    List<NVP> tickers = new ArrayList<>();
    for (String t : new String[] {"ES", "SPX", "SPY", "NQ", "NDX", "QQQ"})
      tickers.add(new NVP(t, t));
    tickerGrp.addRow(new DiscreteDescriptor(DISPLAY_TICKER, "Display Ticker", "ES", tickers)
        .setDescription("Strikes are quoted in ES points; this maps them to the chart instrument."));
    tickerGrp.addRow(new DoubleDescriptor(ES_SPX_SPREAD, "ES-SPX Spread (0 = from data string S:)", 0.0, 0.0, 1000.0, 0.01));
    tickerGrp.addRow(new DoubleDescriptor(NQ_NDX_SPREAD, "NQ-NDX Spread (0 = from data string S:)", 0.0, 0.0, 1000.0, 0.01));
    tickerGrp.addRow(new BooleanDescriptor(SHOW_ABOVE_H1, "Draw on timeframes above 1H", false)
        .setDescription("Off: on 4H, Daily, Weekly and Monthly charts the indicator stays silent."));

    // --- Data -----------------------------------------------------------------------------------
    SettingTab dataTab = sd.addTab("Data");
    SettingGroup dataGrp = dataTab.addGroup("GEX Data");
    dataGrp.addRow(new StringDescriptor(GEX_DATA, "GEX Data (paste from TLADe)", "")
        .setHeight(90));
    SettingGroup fetchGrp = dataTab.addGroup("Auto-Fetch");
    fetchGrp.addRow(new BooleanDescriptor(AUTO_FETCH, "Auto-Fetch from TLADe API", true)
        .setDescription("Fetch levels on load and at the 6 daily ET windows, on a background "
            + "thread. Replaces the pasted data."));
    fetchGrp.addRow(new StringDescriptor(API_KEY, "API Key (from TLADe Terminal)", "")
        .setDescription("With a key: live data. Without: free delayed (3 business days)."));

    // --- Level Visibility -----------------------------------------------------------------------
    SettingTab visTab = sd.addTab("Levels");
    SettingGroup visGrp = visTab.addGroup("Visibility");
    visGrp.addRow(new BooleanDescriptor(SHOW_GEX, "Show GEX Levels (CW/PW/GL)", true));
    visGrp.addRow(new BooleanDescriptor(SHOW_SYSTEM, "Show System Levels (ZG/MP/EH/EL/VH/VL)", true));
    visGrp.addRow(new BooleanDescriptor(SHOW_STRUCTURE, "Show Structure Levels (PDH/PDL/PWH/PWL)", false));
    visGrp.addRow(new BooleanDescriptor(SHOW_CHARM_MAGNET, "Show Charm Magnet (CM)", false));
    visGrp.addRow(new BooleanDescriptor(SHOW_DELTA_FLIP, "Show Delta Flip (DF)", true));

    SettingGroup bosGrp = visTab.addGroup("Breakout Structure");
    bosGrp.addRow(new BooleanDescriptor(SHOW_BREAKOUT, "Show Breakout Structure", false)
        .setDescription("Breakouts from the chart's own bars, on closed bars of each timeframe. A breakout dies when a later bar of the same timeframe, in the opposite direction, closes back through it."));
    bosGrp.addRow(new BooleanDescriptor(SHOW_BOS_M, "Monthly", false));
    bosGrp.addRow(new BooleanDescriptor(SHOW_BOS_W, "Weekly", false));
    bosGrp.addRow(new BooleanDescriptor(SHOW_BOS_D, "Daily", true));
    bosGrp.addRow(new BooleanDescriptor(SHOW_BOS_H4, "4H", true));
    bosGrp.addRow(new BooleanDescriptor(SHOW_BOS_H1, "1H", true));
    bosGrp.addRow(new IntegerDescriptor(BOS_KEEP, "Keep last N per timeframe", 2, 1, 10, 1));

    SettingGroup confGrp = visTab.addGroup("Confluence Zones");
    confGrp.addRow(new BooleanDescriptor(SHOW_CONFLUENCE, "Show Confluence Zones", false));
    confGrp.addRow(new IntegerDescriptor(CONF_MIN_SIZE, "Min cluster size for box", 3, 2, 5, 1));
    confGrp.addRow(new DoubleDescriptor(CONF_EM_PCT, "Band Width (% of EM)", 7.0, 1.0, 30.0, 0.5)
        .setDescription("Total band as a percentage of the EM range (EM High − EM Low), split half above and half below (7% = ±3.5%). No EM in the data → no zones."));

    SettingGroup sessGrp = visTab.addGroup("Session Boxes");
    sessGrp.addRow(new BooleanDescriptor(SHOW_SESSIONS, "Show Session Boxes", false));
    sessGrp.addRow(new BooleanDescriptor(SHOW_BOX_ASIA, "Asia (18:00-03:00 ET)", true));
    sessGrp.addRow(new BooleanDescriptor(SHOW_BOX_EU, "Europe (03:00-08:00 ET)", true));
    sessGrp.addRow(new BooleanDescriptor(SHOW_BOX_PRE, "Pre-Market (08:00-09:30 ET)", true));
    sessGrp.addRow(new BooleanDescriptor(SHOW_BOX_US, "US RTH (09:30-16:00 ET)", true));
    sessGrp.addRow(new BooleanDescriptor(SHOW_SESSIONS_HIST, "Show Historical (prev days)", false));

    SettingGroup alertGrp = visTab.addGroup("Alerts");
    alertGrp.addRow(new BooleanDescriptor(ENABLE_ALERTS, "Enable Level Cross Alerts", false));
    alertGrp.addRow(new BooleanDescriptor(ALERT_WALLS, "Call/Put Walls", true));
    alertGrp.addRow(new BooleanDescriptor(ALERT_SYSTEM, "ZG / Max Pain / EM / Vol Bands", true));
    alertGrp.addRow(new BooleanDescriptor(ALERT_BOS, "Breakouts", true));

    SettingGroup avwapGrp = dataTab.addGroup("Session AVWAP");
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_ASIA, "Show Session AVWAP — Asia", true));
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_EU,   "Show Session AVWAP — EU",   true));
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_US,   "Show Session AVWAP — US",   true));
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_PD,   "Show Session AVWAP — Prev Day US", true));
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_HIST, "Show Historical AVWAP (prev days)", false));
    avwapGrp.addRow(new IntegerDescriptor(AVWAP_LINE_WIDTH, "AVWAP line width", 2, 1, 4, 1));
    avwapGrp.addRow(new BooleanDescriptor(SHOW_AVWAP_LABELS, "Show AVWAP labels", true));
    visGrp.addRow(new IntegerDescriptor(MAX_GEX, "Max GEX Levels (999=All)", 10, 1, 999, 1)
        .setDescription("Caps GEX walls shown, split above/below price. Nearest above & below "
            + "are always kept."));
    visGrp.addRow(new BooleanDescriptor(SHOW_ONLY_NEAR, "Show only levels near price", false));
    visGrp.addRow(new DoubleDescriptor(NEAR_PCT, "  Radius (%)", 3.0, 0.1, 25.0, 0.1));
    visGrp.addRow(new BooleanDescriptor(ENABLE_THRESH, "Enable GEX Threshold Filter", false));
    visGrp.addRow(new DoubleDescriptor(GEX_THRESHOLD, "  Min GEX Magnitude (M)", 50.0, 0.0, 5000.0, 1.0));

    // --- Colors ---------------------------------------------------------------------------------
    SettingTab colorTab = sd.addTab("Colors");
    SettingGroup themeGrp = colorTab.addGroup("Theme");
    List<NVP> themes = new ArrayList<>();
    themes.add(new NVP("Wall Street Classic", "Wall Street Classic"));
    themes.add(new NVP("Boreal", "Boreal"));
    themes.add(new NVP("Lady Trader", "Lady Trader"));
    themeGrp.addRow(new DiscreteDescriptor(THEME, "Theme", "Wall Street Classic", themes));
    List<NVP> barStyles = new ArrayList<>();
    barStyles.add(new NVP("Theme Colors", "Theme Colors"));
    barStyles.add(new NVP("Greyscale", "Greyscale"));
    barStyles.add(new NVP("Custom", "Custom"));
    themeGrp.addRow(new DiscreteDescriptor(BAR_COLOR, "Bar Color Style", "Theme Colors", barStyles));
    themeGrp.addRow(new ColorDescriptor(CUSTOM_CALL, "  Custom Call Bar Color", new Color(0xef, 0x44, 0x44)));
    themeGrp.addRow(new ColorDescriptor(CUSTOM_PUT, "  Custom Put Bar Color", new Color(0x22, 0xc5, 0x5e)));

    // --- Labels ---------------------------------------------------------------------------------
    SettingGroup labelGrp = colorTab.addGroup("Labels");
    labelGrp.addRow(new BooleanDescriptor(SHOW_LABELS, "Show Labels", true));
    List<NVP> lineStyles = new ArrayList<>();
    lineStyles.add(new NVP("Solid", "Solid"));
    lineStyles.add(new NVP("Dashed", "Dashed"));
    lineStyles.add(new NVP("Dotted", "Dotted"));
    labelGrp.addRow(new DiscreteDescriptor(LINE_STYLE, "Line Style (walls, Zero Gamma)", "Dotted", lineStyles));
    labelGrp.addRow(new IntegerDescriptor(LABEL_SIZE, "Label Font Size", 11, 6, 50, 1));

    // --- Profile --------------------------------------------------------------------------------
    SettingTab profTab = sd.addTab("Profile");
    SettingGroup profGrp = profTab.addGroup("GEX Profile (P:)");
    profGrp.addRow(new BooleanDescriptor(SHOW_PROFILE, "Show Profile Bars", true));
    profGrp.addRow(new IntegerDescriptor(PROFILE_WIDTH, "Profile Width (bars)", 70, 1, 500, 1)
        .setDescription("Max histogram length, in bar-widths, anchored at the right edge."));
    profGrp.addRow(new IntegerDescriptor(PROFILE_HEIGHT, "Profile Bar Height (ticks)", 8, 1, 50, 1));
    profGrp.addRow(new DoubleDescriptor(PROFILE_SCALE, "Profile Scale Max", 10.0, 0.1, 1000.0, 0.1));
    profGrp.addRow(new BooleanDescriptor(AUTO_SCALE, "Auto Scale Profile Max", false));
    profGrp.addRow(new IntegerDescriptor(MAX_PROFILE, "Max Profile Rows", 1500, 10, 5000, 1));

    SettingGroup devGrp = profTab.addGroup("Developer");
    devGrp.addRow(new BooleanDescriptor(SHOW_STATUS, "Show Status Banner", true)
        .setDescription("Compact overlay: ticker, data mode (live/delayed), visible level "
            + "count and the last auto-refresh time (ET)."));
    java.util.List<NVP> statusPositions = new java.util.ArrayList<>();
    statusPositions.add(new NVP("Top Left", "TL"));
    statusPositions.add(new NVP("Top Right", "TR"));
    statusPositions.add(new NVP("Bottom Left", "BL"));
    statusPositions.add(new NVP("Bottom Right", "BR"));
    devGrp.addRow(new DiscreteDescriptor(STATUS_POS, "Status Banner Position", "BL", statusPositions)
        .setDescription("Corner where the banner is drawn. Default Bottom Left to clear the "
            + "MotiveWave indicator list (top-left) and the right-edge GEX profile."));

    // --- Runtime --------------------------------------------------------------------------------
    RuntimeDescriptor rd = createRD();
    rd.setLabelSettings(DISPLAY_TICKER);
    rd.declareSignal(SIG_WALL, "Call/Put Wall crossed");
    rd.declareSignal(SIG_SYSTEM, "ZG / Max Pain / EM / Vol Band crossed");
    rd.declareSignal(SIG_BO, "Breakout crossed");
  }

  @Override
  public void clearState()
  {
    super.clearState();
    levels.clear();
    profile.clear();
    fetchedData = null;
    lastFetchMinuteET = -1;
    lastFetchTimeMs = 0L;
    fetchInFlight = false;
    initialFetchKicked = false;
    lastFetchError = null;
  }

  /** Force a full rebuild whenever the user edits any setting. */
  @Override
  public void onSettingsUpdated(DataContext ctx)
  {
    super.onSettingsUpdated(ctx);
    if (ctx != null) recalculate(ctx);
  }

  /** Kick off the initial fetch when the study is loaded onto a chart. */
  @Override
  public void onLoad(Defaults defaults)
  {
    super.onLoad(defaults);
    if (getSettings().getBoolean(AUTO_FETCH, true)) {
      startFetch(true);
      startScheduler();
    }
  }

  /** v1.3 — daemon-thread scheduler. Fires exactly at the next ET slot,
   * chains itself, lives until the JVM exits (= MotiveWave SDK has no
   * teardown callback for studies, so we lean on daemon threads). */
  private synchronized void startScheduler()
  {
    if (scheduler != null) return;
    java.util.concurrent.ScheduledExecutorService s =
        java.util.concurrent.Executors.newSingleThreadScheduledExecutor(r -> {
          Thread t = new Thread(r, "TLADe-MW-scheduler");
          t.setDaemon(true);
          return t;
        });
    scheduler = s;
    scheduleNextFetch();
  }

  private void scheduleNextFetch()
  {
    java.util.concurrent.ScheduledExecutorService s = scheduler;
    if (s == null) return;
    long delayMs = msUntilNextSlot();
    s.schedule(() -> {
      try {
        if (getSettings().getBoolean(AUTO_FETCH, true)) {
          // Resolve which slot just fired. On some macOS Java 25 runtimes
          // ZoneId.of("America/New_York") throws (tzdata missing/sandboxed),
          // the previous swallow left lastFetchMinuteET unwritten and the
          // banner rendered "updated — ET" indefinitely (Prince, 2026-07-01).
          int etMins = nowETMinutesWithFallback();
          if (etMins >= 0) lastFetchMinuteET = etMins;
          lastFetchTimeMs = System.currentTimeMillis();
          startFetch(false);
        }
      } finally {
        scheduleNextFetch(); // chain to next absolute slot
      }
    }, delayMs, java.util.concurrent.TimeUnit.MILLISECONDS);
  }

  /** Return current ET minute-of-day, falling back to a manual UTC offset
   *  when the JVM's tz database can't resolve America/New_York (observed on
   *  macOS Java 25). Prints the underlying exception the first time it
   *  happens so we can chase the root cause. Returns -1 only if even
   *  Instant.now() throws, which is effectively never. */
  private static boolean _tzWarningPrinted = false;
  private static int nowETMinutesWithFallback()
  {
    try {
      java.time.ZonedDateTime et = java.time.ZonedDateTime.now(java.time.ZoneId.of("America/New_York"));
      return et.getHour() * 60 + et.getMinute();
    } catch (Throwable t) {
      if (!_tzWarningPrinted) {
        _tzWarningPrinted = true;
        System.err.println("[TLADe] ET zone resolution failed, falling back to UTC offset. Cause: " + t);
      }
    }
    // Fallback: DST rule of thumb — EDT (UTC-4) from second Sunday of March
    // to first Sunday of November; EST (UTC-5) otherwise. Month-boundary
    // approximation is fine for a banner timestamp (worst case: one hour
    // off around the two transition Sundays).
    try {
      java.time.OffsetDateTime nowUtc = java.time.OffsetDateTime.now(java.time.ZoneOffset.UTC);
      int m = nowUtc.getMonthValue();
      int offsetHours = (m >= 3 && m <= 10) ? -4 : -5;
      int etHour = (nowUtc.getHour() + offsetHours + 24) % 24;
      return etHour * 60 + nowUtc.getMinute();
    } catch (Throwable t) {
      return -1;
    }
  }

  private static long msUntilNextSlot()
  {
    java.time.ZonedDateTime et;
    try { et = java.time.ZonedDateTime.now(java.time.ZoneId.of("America/New_York")); }
    catch (Exception e) { return 60_000L; } // fallback: try again in 1 min
    int nowMins = et.getHour() * 60 + et.getMinute();
    int nextDiffMins = -1;
    for (int slot : FETCH_MINUTES_ET) {
      if (slot > nowMins) { nextDiffMins = slot - nowMins; break; }
    }
    if (nextDiffMins < 0) {
      // No more slots today → wrap to first slot tomorrow.
      nextDiffMins = (1440 - nowMins) + FETCH_MINUTES_ET[0];
    }
    long ms = (long) nextDiffMins * 60_000L
            - (long) et.getSecond() * 1000L
            + 1000L; // 1s buffer so we land just after the slot boundary
    return Math.max(1000L, ms);
  }

  // ================================================================================================
  // Calculation / figure build
  // ================================================================================================

  @Override
  protected void calculateValues(DataContext ctx)
  {
    DataSeries series = ctx.getDataSeries();
    Instrument instr = ctx.getInstrument();
    if (series == null || instr == null || series.size() == 0) return;

    lastCtx = ctx;

    // Kick the initial fetch here (workspace-open path — onLoad is not
    // fired when MW rebuilds a saved study). See initialFetchKicked doc.
    //
    // v1.3.4 — the fetch is *delayed 1.5s* via the scheduler executor so it
    // does not run inline with the first render pass. On macOS Java 25 the
    // synchronous kick from calculateValues (v1.3.3) appeared to hang the
    // fetch thread in the HTTPS layer with fetchInFlight stuck at true.
    // onLoad (drop-fresh path) stays synchronous — that path never showed
    // the problem.
    if (!initialFetchKicked && getSettings().getBoolean(AUTO_FETCH, true)) {
      initialFetchKicked = true;
      startScheduler();
      java.util.concurrent.ScheduledExecutorService s = scheduler;
      if (s != null) {
        s.schedule(() -> startFetch(true), 1500, java.util.concurrent.TimeUnit.MILLISECONDS);
      } else {
        // Should not happen (startScheduler just set it), but keep the fallback.
        startFetch(true);
      }
    }

    // Consume any data delivered by the background fetch thread. Kept in a field
    // (not written back into settings) to avoid mutating settings from inside
    // calc, which can re-enter onSettingsUpdated → recalculate.
    String pending = fetchedData;
    if (pending != null) {
      fetchedData = null;
      lastData = pending;
    }

    // v1.3 — Fetch lifecycle is now owned by the absolute-time scheduler
    // (onLoad → startScheduler). The legacy maybeScheduleFetch() below is
    // kept as dead code; calculateValues no longer drives fetches.

    // Effective data: fetched payload if we have one, else the manual paste.
    String dataStr = (lastData != null) ? lastData : getSettings().getString(GEX_DATA, "");

    resolveColors();
    parse(dataStr);

    double spot = lastClose(series);
    buildDrawModel(spot, ctx);
    if (getSettings().getBoolean(ENABLE_ALERTS, false)) checkLevelAlerts(ctx, series);

    buildStatus(series, instr, spot, dataStr);

    clearFigures();
    beginFigureUpdate();
    addFigure(new DashboardFigure());
    endFigureUpdate();
  }

  // A CLOSE that crosses a drawn level: the close before on one side, the last closed bar on the
  // other. Once per level per bar, so a recalculation does not re-fire.
  private void checkLevelAlerts(DataContext ctx, DataSeries series)
  {
    int i = series.size() - 2;            // last closed bar
    if (i < 1) return;
    double c1 = series.getClose(i), c2 = series.getClose(i - 1);
    boolean wWalls = getSettings().getBoolean(ALERT_WALLS, true), wSys = getSettings().getBoolean(ALERT_SYSTEM, true), wBo = getSettings().getBoolean(ALERT_BOS, true);
    for (DrawLevel d : drawLevels) {
      if (d.kind == null || d.kind.isEmpty()) continue;
      boolean wanted = d.kind.equals("wall") ? wWalls : d.kind.equals("system") ? wSys : wBo;
      if (!wanted) continue;
      boolean up = c2 < d.price && c1 >= d.price, down = c2 > d.price && c1 <= d.price;
      if (!up && !down) continue;
      String key = d.kind + "@" + d.price;
      Integer at = alertedAt.get(key);
      if (at != null && at == i) continue;
      alertedAt.put(key, i);
      String sig = d.kind.equals("wall") ? SIG_WALL : d.kind.equals("system") ? SIG_SYSTEM : SIG_BO;
      try { ctx.signal(i, sig, "TLADe " + d.alertName + " crossed " + (up ? "UP" : "DOWN"), d.price); } catch (Exception ignore) { }
    }
  }

  /** Compose the diagnostic snapshot shown by the status banner. */
  private void buildStatus(DataSeries series, Instrument instr, double spot, String dataStr)
  {
    String ticker = getSettings().getString(DISPLAY_TICKER, "ES");
    boolean fetching = fetchInFlight;
    boolean hasKey = !getSettings().getString(API_KEY, "").trim().isEmpty();
    String mode = hasKey ? "LIVE" : "DELAYED (free)";

    // User-facing banner: ticker, data mode, visible level count, last update (ET).
    // v1.3.4 — bottom line switches to the last fetch error when the fetch is
    // not producing a payload (HTTP code, TLS/network exception). Cleared on
    // the next successful fetch.
    String bottom = lastFetchError != null
        ? "ERR: " + lastFetchError
        : "updated " + lastFetchEt + " ET";
    statusText = String.format(Locale.ROOT,
        "TLADe GEX · %s\n"
      + "%s%s · %d levels\n"
      + "%s",
        ticker,
        mode, fetching ? " · fetching…" : "",
        drawLevels.size(),
        bottom);
  }

  /** Latest non-NaN close. */
  private static double lastClose(DataSeries series)
  {
    for (int i = series.size() - 1; i >= 0; i--) {
      double c = series.getClose(i);
      if (!Double.isNaN(c)) return c;
    }
    return Double.NaN;
  }

  /**
   * Build the precomputed lists the figure paints from. Runs here (not in draw) because the
   * filtering needs {@code spot} and the level caps must be applied once, deterministically.
   */
  private void buildDrawModel(double spot, DataContext ctx)
  {
    List<DrawLevel> newLevels = new ArrayList<>();
    List<DrawProf> newProfile = new ArrayList<>();
    List<DrawZone> newZones = new ArrayList<>();
    boolean active = tfActive(ctx);
    if (!active) {
      drawLevels = newLevels; drawProfile = newProfile; drawZones = newZones;
      avwapAsia = avwapEU = avwapUS = avwapPD = new double[0];
      return;
    }
    computeWallFlips(ctx);
    // Session AVWAP arrays first: the confluence zones read their last values.
    try { computeAvwapForSeries(ctx.getDataSeries()); }
    catch (Exception ignore) { /* keep the previous AVWAP arrays on failure */ }
    profWidthBars = Math.max(1, getSettings().getInteger(PROFILE_WIDTH, 70));
    profHeightTicks = Math.max(1, getSettings().getInteger(PROFILE_HEIGHT, 8));

    boolean showLabels = getSettings().getBoolean(SHOW_LABELS, true);

    // ---- Levels ----
    if (!levels.isEmpty() && !Double.isNaN(spot)) {
      int maxVisible = getSettings().getInteger(MAX_GEX, 10);
      if (maxVisible <= 0) maxVisible = 10;
      int halfMax = (int) Math.ceil(maxVisible / 2.0);
      boolean maxIsAll = maxVisible >= 999;

      boolean showGex = getSettings().getBoolean(SHOW_GEX, true);
      boolean showSystem = getSettings().getBoolean(SHOW_SYSTEM, true);
      boolean showStructure = getSettings().getBoolean(SHOW_STRUCTURE, true);
      boolean onlyNear = getSettings().getBoolean(SHOW_ONLY_NEAR, false);
      double nearPct = getSettings().getDouble(NEAR_PCT, 3.0);
      boolean enableThresh = getSettings().getBoolean(ENABLE_THRESH, false);
      double threshold = getSettings().getDouble(GEX_THRESHOLD, 50.0);

      double closestAboveEs = Double.NaN, closestBelowEs = Double.NaN;
      double closestAboveDist = Double.MAX_VALUE, closestBelowDist = Double.MAX_VALUE;
      for (LevelEntry lvl : levels) {
        if (!isGex(lvl.type)) continue;
        double y0 = convertPrice(lvl.esStrike);
        double dist = Math.abs(y0 - spot);
        if (y0 > spot && dist < closestAboveDist) { closestAboveDist = dist; closestAboveEs = lvl.esStrike; }
        else if (y0 <= spot && dist < closestBelowDist) { closestBelowDist = dist; closestBelowEs = lvl.esStrike; }
      }

      int gexAboveDrawn = 0, gexBelowDrawn = 0;
      for (LevelEntry lvl : levels) {
        double y = convertPrice(lvl.esStrike);
        boolean inRange = !onlyNear
            || (Math.abs(spot - y) / Math.max(1e-9, spot) * 100.0 <= nearPct);

        boolean gex = isGex(lvl.type);
        boolean sys = isSystem(lvl.type);
        boolean struct = isStructure(lvl.type);
        boolean breakout = isBreakout(lvl.type);
        boolean charm    = isCharmMagnet(lvl.type);
        boolean dflip    = isDeltaFlip(lvl.type);

        boolean passesThreshold = !enableThresh || !gex || lvl.magnitude == 0.0
            || lvl.magnitude >= threshold;
        boolean isProtected = gex
            && (nearlyEqual(lvl.esStrike, closestAboveEs) || nearlyEqual(lvl.esStrike, closestBelowEs));
        boolean isAbove = y > spot;

        boolean shouldShow = false;
        if (gex && showGex && passesThreshold) {
          if (maxIsAll) shouldShow = true;
          else if (isProtected) shouldShow = true;
          else if (isAbove && gexAboveDrawn < halfMax) shouldShow = true;
          else if (!isAbove && gexBelowDrawn < halfMax) shouldShow = true;
        } else if (sys && showSystem) shouldShow = true;
        // structure and breakouts from the feed are ignored: both are computed from the chart's bars
        else if (charm    && getSettings().getBoolean(SHOW_CHARM_MAGNET, false)) shouldShow = true;
        else if (dflip    && getSettings().getBoolean(SHOW_DELTA_FLIP, true)) shouldShow = true;

        if (!shouldShow || !inRange) continue;

        if ((lvl.type.equals("CW") || lvl.type.equals("PW")) && !maxIsAll && !isProtected) {
          if (isAbove) gexAboveDrawn++; else gexBelowDrawn++;
        }

        boolean flipped = gex && Boolean.TRUE.equals(wallFlipped.get(lvl.esStrike));
        DrawLevel d = new DrawLevel();
        d.price = y;
        d.color = colorFor(lvl, y, spot);
        d.style = styleFor(lvl.type);
        d.lineWidth = (lvl.type.equals("ZG") || lvl.type.equals("MP")) ? 2 : 1;
        if (flipped) d.lineWidth = Math.max(1, d.lineWidth - 1);
        String nm = formatPrice(lvl.esStrike) + " " + levelName(lvl.type, lvl.label) + (flipped ? " \u21BA" : "");
        d.label = showLabels ? nm : "";
        d.kind = gex ? "wall" : (sys ? "system" : "");
        d.alertName = nm;
        newLevels.add(d);
      }

      // v3.x — local Structure (PDH/PDL/PWH/PWL) from chart bars (Mother is GEX-only;
      // price-dependent levels computed front-end-side, no longer shipped in the feed).
      if (showStructure) {
        String[] paLabels = { "PDH", "PDL", "PWH", "PWL" };
        java.awt.Color paColor = C_STRUCT;
        for (double[] pa : computeLocalPA(ctx.getDataSeries())) {
          double yy = pa[0];
          boolean inR = !onlyNear || (Math.abs(spot - yy) / Math.max(1e-9, spot) * 100.0 <= nearPct);
          if (!inR) continue;
          DrawLevel dl = new DrawLevel();
          dl.price = yy;
          dl.color = paColor;
          dl.style = 2;
          dl.lineWidth = 1;
          dl.label = showLabels ? (formatPrice(yy) + " " + levelName(paLabels[(int) pa[1]], null)) : "";
          dl.kind = "";
          newLevels.add(dl);
        }
      }

      // v3.x — local Breakout (BOS H4/H1) from chart bars (Mother is GEX-only; price-dependent
      // levels computed front-end-side, 1:1 with NT8/ATAS/terminal).
      if (getSettings().getBoolean(SHOW_BREAKOUT, false)) {
        for (Object[] b : computeLocalBOS(ctx.getDataSeries())) {
          double yb = (Double) b[0];
          boolean inR = !onlyNear || (Math.abs(spot - yb) / Math.max(1e-9, spot) * 100.0 <= nearPct);
          if (!inR) continue;
          boolean bull = (Boolean) b[2];
          String tf = (String) b[3];
          boolean higherTf = tf.equals("M") || tf.equals("W") || tf.equals("D");
          DrawLevel db = new DrawLevel();
          db.price = yb;
          db.color = bull ? C_BO_BULL : C_BO_BEAR;
          db.style = higherTf ? 0 : 1;
          db.lineWidth = tf.equals("M") ? 3 : tf.equals("W") ? 2 : 1;
          String nm = (String) b[1] + " " + String.format(Locale.ROOT, "%.2f", yb);
          db.label = showLabels ? nm : "";
          db.kind = "bo";
          db.alertName = nm;
          newLevels.add(db);
        }
      }

      // ---- Confluence zones: every level on the chart, clustered within a band sized on the EM range ----
      if (getSettings().getBoolean(SHOW_CONFLUENCE, false)) {
        double emH = Double.NaN, emL = Double.NaN;
        for (LevelEntry lvl : levels) {
          if (lvl.type.equals("EH")) emH = convertPrice(lvl.esStrike);
          else if (lvl.type.equals("EL")) emL = convertPrice(lvl.esStrike);
        }
        if (!Double.isNaN(emH) && !Double.isNaN(emL) && emH - emL > 0) {
          double band = (emH - emL) * (getSettings().getDouble(CONF_EM_PCT, 7.0) / 100.0) / 2.0;
          java.util.List<double[]> pts = new java.util.ArrayList<>();   // {price}
          java.util.List<String> names = new java.util.ArrayList<>();
          for (LevelEntry lvl : levels) {
            if (!(isGex(lvl.type) || isSystem(lvl.type))) continue;
            pts.add(new double[] { convertPrice(lvl.esStrike) });
            names.add(formatPrice(lvl.esStrike) + " " + levelName(lvl.type, lvl.label));
          }
          for (DrawLevel d : newLevels) {
            if ("bo".equals(d.kind) || (d.kind != null && d.kind.isEmpty() && !d.label.isEmpty())) {
              pts.add(new double[] { d.price }); names.add(d.alertName != null ? d.alertName : d.label);
            }
          }
          double[][] av = { avwapAsia, avwapEU, avwapUS, avwapPD };
          String[] avn = { "AVWAP Asia", "AVWAP EU", "AVWAP US", "AVWAP US Prev Day" };
          for (int i = 0; i < 4; i++) {
            if (av[i] == null || av[i].length == 0) continue;
            double v = av[i][av[i].length - 1];
            if (Double.isNaN(v) || v <= 0) continue;
            pts.add(new double[] { v }); names.add(String.format(Locale.ROOT, "%.0f", v) + " " + avn[i]);
          }
          int n = pts.size();
          Integer[] idx = new Integer[n];
          for (int i = 0; i < n; i++) idx[i] = i;
          java.util.Arrays.sort(idx, (a, b2) -> Double.compare(pts.get(a)[0], pts.get(b2)[0]));
          int minSize = Math.max(2, getSettings().getInteger(CONF_MIN_SIZE, 3));
          int i0 = 0;
          while (i0 < n) {
            double cMin = pts.get(idx[i0])[0], cMax = cMin;
            int size = 1, j = i0 + 1;
            while (j < n) {
              double next = pts.get(idx[j])[0];
              if (next - cMin <= 2 * band) { cMax = next; size++; j++; } else break;
            }
            if (size >= minSize) {
              int capped = Math.min(size, 5);
              Color base = capped >= 5 ? new Color(0xef, 0x44, 0x44) : capped >= 4 ? new Color(0xfb, 0x92, 0x3c) : new Color(0xfa, 0xcc, 0x15);
              int alpha = capped >= 5 ? 89 : capped >= 4 ? 77 : 64;
              DrawZone z = new DrawZone();
              z.top = cMax; z.bottom = cMin;
              z.color = new Color(base.getRed(), base.getGreen(), base.getBlue(), alpha);
              z.label = capped >= 5 ? "UBER" : capped + "x";
              newZones.add(z);
            }
            i0 = j;
          }
        }
      }
    }

    // ---- Profile ----
    if (getSettings().getBoolean(SHOW_PROFILE, true) && !profile.isEmpty()) {
      double scaleMax = Math.max(1e-9, getSettings().getDouble(PROFILE_SCALE, 10.0));
      double maxAbs = scaleMax;
      if (getSettings().getBoolean(AUTO_SCALE, false)) {
        double dataMax = 0.0;
        for (ProfileEntry pr : profile) dataMax = Math.max(dataMax, Math.abs(pr.value));
        maxAbs = Math.max(maxAbs, dataMax);
      }
      for (ProfileEntry pr : profile) {
        double lenValue = Math.abs(pr.value);
        double frac = lenValue / maxAbs;
        if (frac <= 0) continue;
        Color base = (pr.sign >= 0) ? profCallColor : profPutColor;
        double intensity = Math.min(1.0, frac);
        int alpha = clamp((int) Math.round(255.0 * (0.60 + intensity * 0.40)), 0, 255);
        DrawProf dp = new DrawProf();
        dp.price = convertPrice(pr.esStrike);
        dp.frac = Math.min(1.0, frac);
        dp.color = new Color(base.getRed(), base.getGreen(), base.getBlue(), alpha);
        newProfile.add(dp);
      }
    }

    // Publish atomically: a single volatile ref swap each, so the draw thread
    // always reads a fully-built model (or the previous one), never a partial.
    drawLevels = newLevels;
    drawProfile = newProfile;
    drawZones = newZones;
    drawBoxes = getSettings().getBoolean(SHOW_SESSIONS, false) ? computeSessionBoxes(ctx.getDataSeries()) : new ArrayList<>();

  }

  /** Live bars: nudge a redraw so the right-edge anchored figure stays put. */
  @Override
  public void onBarUpdate(DataContext ctx)
  {
    super.onBarUpdate(ctx);
    lastCtx = ctx;
    if (fetchedData != null) {
      recalculate(ctx);
      return;
    }
    // v1.3 — fetch scheduling is now owned by the absolute-time scheduler.
    // onBarUpdate only nudges the redraw so the figure stays right-edge-anchored.
    notifyRedraw();
  }

  // ================================================================================================
  // Data-string parsing (faithful to NT8 ParseIfChanged)
  // ================================================================================================

  private void parse(String input)
  {
    levels.clear();
    profile.clear();

    String raw = (input == null ? "" : input).replace("\r", "").replace("\n", "").trim();
    if (raw.isEmpty()) return;

    // v7.1: optional "S:<spread>|..." prefix overrides the ES-SPX spread setting.
    if (raw.startsWith("S:")) {
      int sEnd = raw.indexOf('|');
      if (sEnd > 2) {
        String spreadStr = raw.substring(2, sEnd);
        Double parsed = tryParse(spreadStr);
        if (parsed != null && parsed > 0)
          spreadOverride = parsed;
        raw = raw.substring(sEnd + 1);
      }
    }
    // R: NDX→QQQ ratio (NQ family strings only)
    if (raw.startsWith("R:")) {
      int rEnd = raw.indexOf('|');
      if (rEnd > 2) {
        Double parsed = tryParse(raw.substring(2, rEnd));
        if (parsed != null && parsed > 0) ndxQqqRatio = parsed;
        raw = raw.substring(rEnd + 1);
      }
    }

    String levelsData = "";
    String profileData = "";

    int pipePos = raw.indexOf("|P:");
    if (pipePos >= 0) {
      String beforePipe = raw.substring(0, pipePos);
      if (beforePipe.startsWith("L:")) levelsData = beforePipe.substring(2);
      profileData = raw.substring(pipePos + 3);
    } else if (raw.startsWith("L:")) {
      levelsData = raw.substring(2);
    }

    if (!levelsData.isEmpty()) {
      for (String p : levelsData.split(";")) {
        if (p.isEmpty()) continue;
        String[] parts = p.split(",", -1);
        if (parts.length < 3) continue;

        Double strike = tryParse(parts[0]);
        if (strike == null) continue;

        LevelEntry e = new LevelEntry();
        e.esStrike = strike;
        e.type = parts[1].trim();
        e.label = parts[2].trim();
        e.magnitude = 0.0;
        if (parts.length >= 5) {
          Double mag = tryParse(parts[4]);
          if (mag != null) e.magnitude = Math.abs(mag);
        }
        levels.add(e);
      }
    }

    if (!profileData.isEmpty()) {
      int maxRows = getSettings().getInteger(MAX_PROFILE, 1500);
      int n = 0;
      for (String r : profileData.split(";")) {
        if (n >= maxRows) break;
        if (r.isEmpty()) continue;
        String[] parts = r.split(",", -1);
        if (parts.length != 3) continue;
        Double strike = tryParse(parts[0]);
        Double value = tryParse(parts[1]);
        Double sign = tryParse(parts[2]);
        if (strike == null || value == null || sign == null) continue;
        ProfileEntry pe = new ProfileEntry();
        pe.esStrike = strike;
        pe.value = value;
        pe.sign = sign;
        profile.add(pe);
        n++;
      }
    }
  }

  private static Double tryParse(String s)
  {
    if (s == null) return null;
    try { return Double.parseDouble(s.trim()); }
    catch (NumberFormatException e) { return null; }
  }

  // ================================================================================================
  // Price mapping (ES points -> display instrument)
  // ================================================================================================

  private boolean isNqFamily()
  {
    String t = getSettings().getString(DISPLAY_TICKER, "ES").toUpperCase(Locale.ROOT);
    return t.equals("NQ") || t.equals("NDX") || t.equals("QQQ");
  }

  private double convertPrice(double esPrice)
  {
    String t = getSettings().getString(DISPLAY_TICKER, "ES").toUpperCase(Locale.ROOT);
    // No built-in spread: the S: of the data string wins; without it the strikes stay in futures
    // points (identity) instead of being shifted by a stale number.
    boolean nqFamily = t.equals("NQ") || t.equals("NDX") || t.equals("QQQ");
    double esSpx = (!nqFamily && !Double.isNaN(spreadOverride)) ? spreadOverride : getSettings().getDouble(ES_SPX_SPREAD, 0.0);
    double nqNdx = (nqFamily && !Double.isNaN(spreadOverride)) ? spreadOverride : getSettings().getDouble(NQ_NDX_SPREAD, 0.0);
    double ratio = (!Double.isNaN(ndxQqqRatio) && ndxQqqRatio > 0) ? ndxQqqRatio : 40.0;
    switch (t) {
      case "SPX": return esSpx <= 0 ? esPrice : esPrice - esSpx;
      case "SPY": return esSpx <= 0 ? esPrice : (esPrice - esSpx) / 10.0;
      case "NQ":  return esPrice;
      case "NDX": return nqNdx <= 0 ? esPrice : esPrice - nqNdx;
      case "QQQ": return nqNdx <= 0 ? esPrice : (esPrice - nqNdx) / ratio;
      default:    return esPrice; // ES
    }
  }

  // Local Structure: PDH/PDL = prior futures-day (18:00 ET) high/low; PWH/PWL = prior week high/low.
  // Computed from the chart's own bars (display price) — Mother is GEX-only, so price-dependent
  // levels live in each front-end, no longer shipped in the feed. Returns {price, kind} where
  // kind = 0:PDH 1:PDL 2:PWH 3:PWL. java.time is fully-qualified to avoid touching imports.
  private java.util.List<double[]> computeLocalPA(DataSeries series)
  {
    java.util.List<double[]> out = new java.util.ArrayList<>();
    if (series == null) return out;
    int n = series.size();
    if (n < 2) return out;
    java.time.ZoneId etZone = java.time.ZoneId.of("America/New_York");
    java.util.TreeMap<Long, double[]> day = new java.util.TreeMap<>();
    for (int i = 0; i < n; i++) {
      long t = series.getStartTime(i);
      java.time.ZonedDateTime et = java.time.Instant.ofEpochMilli(t).atZone(etZone);
      java.time.LocalDate fd = (et.getHour() >= 18) ? et.toLocalDate().plusDays(1) : et.toLocalDate();
      long key = fd.toEpochDay();
      double h = series.getHigh(i), l = series.getLow(i);
      double[] hl = day.get(key);
      if (hl == null) day.put(key, new double[] { h, l });
      else { if (h > hl[0]) hl[0] = h; if (l < hl[1]) hl[1] = l; }
    }
    java.util.List<Long> dk = new java.util.ArrayList<>(day.keySet());
    if (dk.size() >= 2) {
      double[] pd = day.get(dk.get(dk.size() - 2)); // prior completed day (last = current)
      out.add(new double[] { pd[0], 0 });
      out.add(new double[] { pd[1], 1 });
    }
    java.util.TreeMap<Long, double[]> week = new java.util.TreeMap<>();
    for (Long d : dk) {
      java.time.LocalDate ld = java.time.LocalDate.ofEpochDay(d);
      java.time.LocalDate mon = ld.minusDays((ld.getDayOfWeek().getValue() + 6) % 7); // Monday of week
      long wk = mon.toEpochDay();
      double[] dhl = day.get(d);
      double[] whl = week.get(wk);
      if (whl == null) week.put(wk, new double[] { dhl[0], dhl[1] });
      else { if (dhl[0] > whl[0]) whl[0] = dhl[0]; if (dhl[1] < whl[1]) whl[1] = dhl[1]; }
    }
    java.util.List<Long> wk2 = new java.util.ArrayList<>(week.keySet());
    if (wk2.size() >= 2) {
      double[] pw = week.get(wk2.get(wk2.size() - 2));
      out.add(new double[] { pw[0], 2 });
      out.add(new double[] { pw[1], 3 });
    }
    return out;
  }

  // Local Breakout (BOS): H4/H1 from the chart's own bars, 18:00 ET anchor — 1:1 with the NT8/ATAS
  // bridges and the terminal (most recent valid bull + bear per timeframe). No feed dependency.
  // Bars aggregated as {high, low, close}. java.time fully-qualified so imports stay untouched.
  private java.util.List<double[]> aggregateByEtPeriod(DataSeries series, int barCount, int periodMinutes)
  {
    java.util.List<double[]> out = new java.util.ArrayList<>();
    java.time.ZoneId etZone = java.time.ZoneId.of("America/New_York");
    boolean has = false; long curSlot = Long.MIN_VALUE;
    double curO = 0, curH = 0, curL = 0, curC = 0;
    final long anchor = 18L * 60L; // 18:00 ET
    for (int i = 0; i < barCount; i++) {
      long t = series.getStartTime(i);
      java.time.ZonedDateTime et = java.time.Instant.ofEpochMilli(t).atZone(etZone);
      long etMin = et.toLocalDate().toEpochDay() * 1440L + et.getHour() * 60L + et.getMinute();
      long slot = Math.floorDiv(etMin - anchor, periodMinutes) * periodMinutes + anchor;
      double o = series.getOpen(i), h = series.getHigh(i), l = series.getLow(i), c = series.getClose(i);
      if (!has || slot != curSlot) {
        if (has) out.add(new double[] { curO, curH, curL, curC });
        has = true; curSlot = slot; curO = o; curH = h; curL = l; curC = c;
      } else {
        if (h > curH) curH = h;
        if (l < curL) curL = l;
        curC = c;
      }
    }
    if (has) out.add(new double[] { curO, curH, curL, curC });
    return out;
  }

  private static boolean isQualityBreakout(double[] curr, double[] prev, boolean bull)
  {
    // arr = {open, high, low, close}
    if (bull) {
      if (curr[3] <= prev[1]) return false;
      double bodyAbove = curr[3] - prev[1], upperShadow = curr[1] - curr[3];
      return bodyAbove > upperShadow && bodyAbove > 0;
    } else {
      if (curr[3] >= prev[2]) return false;
      double bodyBelow = prev[2] - curr[3], lowerShadow = curr[3] - curr[2];
      return bodyBelow > lowerShadow && bodyBelow > 0;
    }
  }

  // Breakouts of one timeframe, CLOSED bars only (the last element is the bar in progress). A
  // breakout dies when a later bar of the SAME timeframe, in the opposite direction, closes back
  // through its level — a long BO H1 lives until a bearish H1 bar closes below it. Of the survivors,
  // the last `keep` per timeframe are returned. out entries: {price, bull ? 1 : 0}
  private void detectBosInto(java.util.List<double[]> c, java.util.List<double[]> out, int keep)
  {
    int lastClosed = c.size() - 2;
    if (lastClosed < 1) return;
    java.util.List<double[]> alive = new java.util.ArrayList<>();
    for (int i = 1; i <= lastClosed; i++) {
      double[] curr = c.get(i), prev = c.get(i - 1);
      if (isQualityBreakout(curr, prev, true)) {
        boolean killed = false;
        for (int j = i + 1; j <= lastClosed; j++) { double[] b = c.get(j); if (b[3] < prev[1] && b[3] < b[0]) { killed = true; break; } }
        if (!killed) alive.add(new double[] { prev[1], 1 });
      }
      if (isQualityBreakout(curr, prev, false)) {
        boolean killed = false;
        for (int j = i + 1; j <= lastClosed; j++) { double[] b = c.get(j); if (b[3] > prev[2] && b[3] > b[0]) { killed = true; break; } }
        if (!killed) alive.add(new double[] { prev[2], 0 });
      }
    }
    int from = Math.max(0, alive.size() - Math.max(1, keep));
    for (int i = from; i < alive.size(); i++) out.add(alive.get(i));
  }

  private static String boLabel(boolean bull, String tf)
  {
    char t = bull ? '\u25B2' : '\u25BC';
    int n = tf.equals("M") ? 5 : tf.equals("W") ? 4 : tf.equals("D") ? 3 : tf.equals("H4") ? 2 : 1;
    StringBuilder sb = new StringBuilder();
    for (int i = 0; i < n; i++) sb.append(t);
    return sb + " BO " + (bull ? "L" : "S") + " " + tf;
  }

  // futures day (18:00 ET boundary) of a bar, as an epoch day
  private static long futuresDay(long timeMs)
  {
    java.time.ZonedDateTime et = java.time.Instant.ofEpochMilli(timeMs).atZone(java.time.ZoneId.of("America/New_York"));
    java.time.LocalDate fd = (et.getHour() >= 18) ? et.toLocalDate().plusDays(1) : et.toLocalDate();
    return fd.toEpochDay();
  }

  // chart bars -> one {O,H,L,C} per calendar key of the futures day (week = its Monday, month = its 1st)
  private java.util.List<double[]> aggregateByKey(DataSeries series, int barCount, boolean monthly)
  {
    java.util.List<double[]> out = new java.util.ArrayList<>();
    boolean has = false; long curKey = Long.MIN_VALUE;
    double cO = 0, cH = 0, cL = 0, cC = 0;
    for (int i = 0; i < barCount; i++) {
      java.time.LocalDate fd = java.time.LocalDate.ofEpochDay(futuresDay(series.getStartTime(i)));
      long key = monthly ? (fd.getYear() * 12L + fd.getMonthValue()) : fd.minusDays((fd.getDayOfWeek().getValue() + 6) % 7).toEpochDay();
      double o = series.getOpen(i), h = series.getHigh(i), l = series.getLow(i), c = series.getClose(i);
      if (!has || key != curKey) {
        if (has) out.add(new double[] { cO, cH, cL, cC });
        has = true; curKey = key; cO = o; cH = h; cL = l; cC = c;
      } else {
        if (h > cH) cH = h;
        if (l < cL) cL = l;
        cC = c;
      }
    }
    if (has) out.add(new double[] { cO, cH, cL, cC });
    return out;
  }

  // returns {price, label, bull, tf} per live breakout
  private java.util.List<Object[]> computeLocalBOS(DataSeries series)
  {
    java.util.List<Object[]> out = new java.util.ArrayList<>();
    if (series == null) return out;
    int n = series.size();
    if (n < 3) return out;
    int keep = getSettings().getInteger(BOS_KEEP, 2);
    String[] tfs = { "M", "W", "D", "H4", "H1" };
    boolean[] on = { getSettings().getBoolean(SHOW_BOS_M, false), getSettings().getBoolean(SHOW_BOS_W, false),
                     getSettings().getBoolean(SHOW_BOS_D, true), getSettings().getBoolean(SHOW_BOS_H4, true), getSettings().getBoolean(SHOW_BOS_H1, true) };
    for (int k = 0; k < tfs.length; k++) {
      if (!on[k]) continue;
      java.util.List<double[]> bars =
          tfs[k].equals("M") ? aggregateByKey(series, n, true) :
          tfs[k].equals("W") ? aggregateByKey(series, n, false) :
          aggregateByEtPeriod(series, n, tfs[k].equals("D") ? 1440 : tfs[k].equals("H4") ? 240 : 60);
      java.util.List<double[]> found = new java.util.ArrayList<>();
      detectBosInto(bars, found, keep);
      for (double[] f : found) {
        boolean bull = f[1] > 0.5;
        out.add(new Object[] { f[0], boLabel(bull, tfs[k]), bull, tfs[k] });
      }
    }
    return out;
  }

  // Session boxes: Asia / Europe / Pre / US hi-lo rectangles of the last futures day on the chart
  // (18:00 ET boundary), or of every day with "Show Historical". Same sessions as the TradingView
  // indicator: Asia 18:00-03:00, EU 03:00-08:00, Pre 08:00-09:30, US 09:30-16:00 ET.
  private List<DrawBox> computeSessionBoxes(DataSeries series)
  {
    List<DrawBox> out = new ArrayList<>();
    if (series == null || series.size() == 0) return out;
    boolean hist = getSettings().getBoolean(SHOW_SESSIONS_HIST, false);
    boolean[] on = { getSettings().getBoolean(SHOW_BOX_ASIA, true), getSettings().getBoolean(SHOW_BOX_EU, true),
                     getSettings().getBoolean(SHOW_BOX_PRE, true), getSettings().getBoolean(SHOW_BOX_US, true) };
    Color[] cols = { new Color(0xf5, 0x9e, 0x0b, 26), new Color(0x3b, 0x82, 0xf6, 26), new Color(0xa8, 0x55, 0xf7, 20), new Color(0x22, 0xc5, 0x5e, 20) };
    java.time.ZoneId et = java.time.ZoneId.of("America/New_York");
    int n = series.size();
    long lastDay = futuresDay(series.getStartTime(n - 1));
    int curType = -1; DrawBox cur = null;
    for (int i = 0; i < n; i++) {
      long t = series.getStartTime(i);
      if (!hist && futuresDay(t) != lastDay) continue;
      java.time.ZonedDateTime z = java.time.Instant.ofEpochMilli(t).atZone(et);
      int mins = z.getHour() * 60 + z.getMinute();
      int type = (mins >= 1080 || mins < 180) ? 0 : mins < 480 ? 1 : mins < 570 ? 2 : mins < 960 ? 3 : -1;
      if (type != curType) {
        if (cur != null) out.add(cur);
        cur = null; curType = type;
        if (type >= 0 && on[type]) { cur = new DrawBox(); cur.t0 = t; cur.t1 = t; cur.hi = series.getHigh(i); cur.lo = series.getLow(i); cur.color = cols[type]; }
      } else if (cur != null) {
        cur.t1 = t;
        if (series.getHigh(i) > cur.hi) cur.hi = series.getHigh(i);
        if (series.getLow(i) < cur.lo) cur.lo = series.getLow(i);
      }
    }
    if (cur != null) out.add(cur);
    return out;
  }

  /** Chart-display price back into futures space (the strikes' space). */
  private double toFuturesSpace(double displayPrice)
  {
    String t = getSettings().getString(DISPLAY_TICKER, "ES").toUpperCase(Locale.ROOT);
    boolean nqFamily = t.equals("NQ") || t.equals("NDX") || t.equals("QQQ");
    double esSpx = (!nqFamily && !Double.isNaN(spreadOverride)) ? spreadOverride : getSettings().getDouble(ES_SPX_SPREAD, 0.0);
    double nqNdx = (nqFamily && !Double.isNaN(spreadOverride)) ? spreadOverride : getSettings().getDouble(NQ_NDX_SPREAD, 0.0);
    double ratio = (!Double.isNaN(ndxQqqRatio) && ndxQqqRatio > 0) ? ndxQqqRatio : 40.0;
    switch (t) {
      case "SPX": return esSpx <= 0 ? displayPrice : displayPrice + esSpx;
      case "SPY": return esSpx <= 0 ? displayPrice : displayPrice * 10.0 + esSpx;
      case "NDX": return nqNdx <= 0 ? displayPrice : displayPrice + nqNdx;
      case "QQQ": return nqNdx <= 0 ? displayPrice : displayPrice * ratio + nqNdx;
      default:    return displayPrice;
    }
  }

  /** One name per level code, the same words on every platform. */
  private static String levelName(String code, String feedLabel)
  {
    switch (code) {
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
      default: return feedLabel == null ? code : feedLabel;
    }
  }

  /** The chart is an intraday tool: above 1H it draws nothing unless asked. */
  private boolean tfActive(DataContext ctx)
  {
    if (getSettings().getBoolean(SHOW_ABOVE_H1, false)) return true;
    try {
      com.motivewave.platform.sdk.common.BarSize bs = ctx.getChartBarSize();
      if (bs == null) return true;
      int mins = bs.getIntervalMinutes();
      return mins <= 60;
    } catch (Exception e) { return true; }
  }

  // Wall flip, the TradingView rule: two consecutive 5-minute closes beyond the strike flip the
  // wall, two the other way restore it. Replayed over the 5-minute series on every calc, so the
  // state is deterministic and the same on a 1-minute or hourly chart.
  private void computeWallFlips(DataContext ctx)
  {
    wallFlipped.clear();
    java.util.List<LevelEntry> walls = new java.util.ArrayList<>();
    for (LevelEntry l : levels) if (l.type.equals("CW") || l.type.equals("PW")) walls.add(l);
    if (walls.isEmpty()) return;
    DataSeries s5 = null;
    try { s5 = ctx.getDataSeries(com.motivewave.platform.sdk.common.BarSize.getBarSize(5)); } catch (Exception e) { s5 = null; }
    if (s5 == null || s5.size() < 2) return;
    int n = walls.size();
    int[] cnt = new int[n]; boolean[] flipped = new boolean[n];
    int last = s5.size() - 2;   // the last bar is still forming
    for (int i = Math.max(0, last - 2000); i <= last; i++) {
      double c = toFuturesSpace(s5.getClose(i));
      for (int w = 0; w < n; w++) {
        double k = walls.get(w).esStrike;
        boolean cw = walls.get(w).type.equals("CW");
        boolean beyond = cw ? (flipped[w] ? c < k : c > k) : (flipped[w] ? c > k : c < k);
        if (beyond) { cnt[w]++; if (cnt[w] >= 2) { flipped[w] = !flipped[w]; cnt[w] = 0; } }
        else cnt[w] = 0;
      }
    }
    for (int w = 0; w < n; w++) wallFlipped.put(walls.get(w).esStrike, flipped[w]);
  }

  private String formatPrice(double esPrice)
  {
    double c = convertPrice(esPrice);
    String t = getSettings().getString(DISPLAY_TICKER, "ES").toUpperCase(Locale.ROOT);
    if (t.equals("SPY") || t.equals("QQQ"))
      return String.format(Locale.ROOT, "%.2f", c);
    // %.0f already rounds to the nearest integer; pass the double (not Math.round's long,
    // which would throw IllegalFormatConversionException against the %f conversion).
    return String.format(Locale.ROOT, "%.0f", c);
  }

  // ================================================================================================
  // Level-type classification (faithful to NT8)
  // ================================================================================================

  private static boolean isGex(String t) { return t.equals("CW") || t.equals("PW") || t.equals("GL"); }
  private static boolean isSystem(String t)
  {
    return t.equals("ZG") || t.equals("MP") || t.equals("EH") || t.equals("EL")
        || t.equals("EHR") || t.equals("ELR") || t.equals("VH") || t.equals("VL");
  }
  private static boolean isStructure(String t)
  {
    return t.equals("PDH") || t.equals("PDL") || t.equals("PWH") || t.equals("PWL");
  }
  // v1.3 — two new families surfaced by the extended cloud payload.
  // BL / BS = Breakout Areas (long / short, from PA engine).
  // CM = Charm Magnet (strike where charm flow magnetises price).
  private static boolean isBreakout(String t) { return t.equals("BL") || t.equals("BS"); }
  private static boolean isCharmMagnet(String t) { return t.equals("CM"); }
  private static boolean isDeltaFlip(String t) { return t.equals("DF"); }
  // The TradingView palette, so the two charts read the same
  private static final Color C_ZG = new Color(0x9c, 0xa3, 0xaf), C_MP = new Color(0xef, 0x44, 0x44),
      C_EM = new Color(0x3b, 0x82, 0xf6), C_VB = new Color(0x9c, 0xa3, 0xaf), C_STRUCT = new Color(0x9c, 0xa3, 0xaf),
      C_CHARM = new Color(0xf9, 0x73, 0x16), C_DFLIP = new Color(0xf5, 0x9e, 0x0b),
      C_BO_BULL = new Color(0x22, 0xc5, 0x5e), C_BO_BEAR = new Color(0xef, 0x44, 0x44);

  private static boolean nearlyEqual(double a, double b)
  {
    if (Double.isNaN(a) || Double.isNaN(b)) return false;
    return Math.abs(a - b) < 1e-9;
  }

  // ================================================================================================
  // Colors / theme
  // ================================================================================================

  private void resolveColors()
  {
    posColor = new Color(0x22, 0xc5, 0x5e);
    negColor = new Color(0xef, 0x44, 0x44);

    String theme = getSettings().getString(THEME, "Wall Street Classic").trim();
    if (theme.equalsIgnoreCase("Boreal")) {
      posColor = new Color(0x22, 0xd3, 0xee);
      negColor = new Color(0xf4, 0x72, 0xb6);
    } else if (theme.equalsIgnoreCase("Lady Trader")) {
      posColor = new Color(0x2d, 0xd4, 0xbf);
      negColor = new Color(0xc0, 0x84, 0xfc);
    }

    String bcs = getSettings().getString(BAR_COLOR, "Theme Colors").trim();
    if (bcs.equalsIgnoreCase("Greyscale")) {
      profCallColor = new Color(0xa0, 0xa0, 0xa0);
      profPutColor  = new Color(0x50, 0x50, 0x50);
    } else if (bcs.equalsIgnoreCase("Custom")) {
      profCallColor = getSettings().getColor(CUSTOM_CALL, new Color(0xef, 0x44, 0x44));
      profPutColor  = getSettings().getColor(CUSTOM_PUT, new Color(0x22, 0xc5, 0x5e));
    } else { // Theme Colors (inverted: call=neg, put=pos — matches TV)
      profCallColor = negColor;
      profPutColor  = posColor;
    }
  }

  // ================================================================================================
  // Session AVWAP — local compute (v1.3, mirrors the Pine indicator)
  // ================================================================================================

  /** Populate the 4 AVWAP arrays from the DataSeries. Called from calculateValues
   * after the level model is built. hlc3 * volume cumulative, reset at each
   * session anchor in ET. Asia anchor at 18:00 ET = futures day start; on each
   * new Asia session the previous US AVWAP is "promoted" to PD (Pine pattern). */
  private void computeAvwapForSeries(DataSeries series)
  {
    int n = series.size();
    if (n <= 0) {
      avwapAsia = avwapEU = avwapUS = avwapPD = new double[0];
      avwapTimes = new long[0];
      currentAsiaAnchorMs = 0L;
      return;
    }
    double[] aA = new double[n];
    double[] aE = new double[n];
    double[] aU = new double[n];
    double[] aP = new double[n];
    long[]   aT = new long[n];
    for (int i = 0; i < n; i++) { aA[i] = aE[i] = aU[i] = aP[i] = Double.NaN; }

    double sumPVA = 0, sumVA = 0;
    double sumPVE = 0, sumVE = 0;
    double sumPVU = 0, sumVU = 0;
    double sumPVPD = 0, sumVPD = 0;
    long lastAsiaAnchor = 0, lastEUAnchor = 0, lastUSAnchor = 0;
    long lastSeenAsia = 0;
    java.time.ZoneId etZone = java.time.ZoneId.of("America/New_York");

    for (int i = 0; i < n; i++) {
      long t = series.getStartTime(i);
      aT[i] = t;
      double h = series.getHigh(i), l = series.getLow(i), c = series.getClose(i);
      double v = series.getVolume(i);
      if (Double.isNaN(h) || Double.isNaN(l) || Double.isNaN(c) || Double.isNaN(v)) continue;
      double hlc3 = (h + l + c) / 3.0;

      java.time.ZonedDateTime et = java.time.Instant.ofEpochMilli(t).atZone(etZone);
      long asiaAnchor = asiaAnchorFor(et);
      long euAnchor   = euAnchorFor(et);
      long usAnchor   = usAnchorFor(et);

      if (asiaAnchor != lastAsiaAnchor) {
        sumPVPD = sumPVU; sumVPD = sumVU;     // promote US → PD
        sumPVA = 0; sumVA = 0;
        lastAsiaAnchor = asiaAnchor;
        lastSeenAsia = asiaAnchor;
      }
      if (euAnchor != lastEUAnchor) { sumPVE = 0; sumVE = 0; lastEUAnchor = euAnchor; }
      if (usAnchor != lastUSAnchor) { sumPVU = 0; sumVU = 0; lastUSAnchor = usAnchor; }

      if (t >= asiaAnchor) { sumPVA += hlc3 * v; sumVA += v; }
      if (t >= euAnchor)   { sumPVE += hlc3 * v; sumVE += v; }
      if (t >= usAnchor)   { sumPVU += hlc3 * v; sumVU += v; }

      if (sumVA > 0)  aA[i] = sumPVA / sumVA;
      if (sumVE > 0)  aE[i] = sumPVE / sumVE;
      if (sumVU > 0)  aU[i] = sumPVU / sumVU;
      if (sumVPD > 0) aP[i] = sumPVPD / sumVPD;
    }

    avwapAsia = aA; avwapEU = aE; avwapUS = aU; avwapPD = aP;
    avwapTimes = aT;
    currentAsiaAnchorMs = lastSeenAsia;
  }

  private static long asiaAnchorFor(java.time.ZonedDateTime et)
  {
    java.time.ZonedDateTime t18 = et.withHour(18).withMinute(0).withSecond(0).withNano(0);
    if (et.compareTo(t18) >= 0) return t18.toInstant().toEpochMilli();
    return t18.minusDays(1).toInstant().toEpochMilli();
  }
  private static long euAnchorFor(java.time.ZonedDateTime et)
  {
    java.time.ZonedDateTime t02 = et.withHour(2).withMinute(0).withSecond(0).withNano(0);
    if (et.compareTo(t02) >= 0) return t02.toInstant().toEpochMilli();
    return t02.minusDays(1).toInstant().toEpochMilli();
  }
  private static long usAnchorFor(java.time.ZonedDateTime et)
  {
    java.time.ZonedDateTime t0930 = et.withHour(9).withMinute(30).withSecond(0).withNano(0);
    if (et.compareTo(t0930) >= 0) return t0930.toInstant().toEpochMilli();
    return t0930.minusDays(1).toInstant().toEpochMilli();
  }

  /** Draw the 4 polylines on the chart. Called from DashboardFigure.draw. */
  private void paintAvwapPolylines(Graphics2D gc, DrawContext ctx, Rectangle b)
  {
    double[] aA = avwapAsia, aE = avwapEU, aU = avwapUS, aP = avwapPD;
    long[]   times = avwapTimes;
    int n = times.length;
    if (n == 0 || aA.length != n || aE.length != n || aU.length != n || aP.length != n) return;

    boolean showHist = getSettings().getBoolean(SHOW_AVWAP_HIST, false);
    long clipFrom = showHist ? Long.MIN_VALUE : currentAsiaAnchorMs;
    int width = getSettings().getInteger(AVWAP_LINE_WIDTH, 2);

    if (getSettings().getBoolean(SHOW_AVWAP_ASIA, true))
      drawOneAvwap(gc, ctx, b, times, aA, clipFrom, new Color(0xF5, 0x9E, 0x0B), "AVWAP Asia", width);
    if (getSettings().getBoolean(SHOW_AVWAP_EU, true))
      drawOneAvwap(gc, ctx, b, times, aE, clipFrom, new Color(0x3B, 0x82, 0xF6), "AVWAP EU", width);
    if (getSettings().getBoolean(SHOW_AVWAP_US, true))
      drawOneAvwap(gc, ctx, b, times, aU, clipFrom, new Color(0x22, 0xC5, 0x5E), "AVWAP US", width);
    if (getSettings().getBoolean(SHOW_AVWAP_PD, true))
      drawOneAvwap(gc, ctx, b, times, aP, clipFrom, new Color(0x6E, 0xE7, 0xB7), "AVWAP US Prev Day", width);
  }

  private void drawOneAvwap(Graphics2D gc, DrawContext ctx, Rectangle b,
                            long[] times, double[] vals, long clipFromMs,
                            Color color, String label, int width)
  {
    if (vals == null || vals.length == 0) return;
    gc.setColor(color);
    gc.setStroke(new BasicStroke(width));
    int prevX = Integer.MIN_VALUE, prevY = Integer.MIN_VALUE;
    int lastValidX = Integer.MIN_VALUE, lastValidY = Integer.MIN_VALUE;
    double lastValidValue = Double.NaN;
    boolean haveLast = false;

    for (int i = 0; i < vals.length; i++) {
      long t = times[i];
      if (t < clipFromMs) { prevX = Integer.MIN_VALUE; continue; }
      double v = vals[i];
      if (Double.isNaN(v)) { prevX = Integer.MIN_VALUE; continue; }
      int x = ctx.translateTime(t);
      int y = ctx.translateValue(v);
      if (prevX != Integer.MIN_VALUE) gc.drawLine(prevX, prevY, x, y);
      prevX = x; prevY = y;
      lastValidX = x; lastValidY = y; lastValidValue = v;
      haveLast = true;
    }

    if (haveLast && getSettings().getBoolean(SHOW_AVWAP_LABELS, true)) {
      Font f = new Font("Arial", Font.PLAIN, 11);
      gc.setFont(f);
      FontMetrics fm = gc.getFontMetrics();
      String txt = " " + String.format(Locale.ROOT, "%.2f", lastValidValue) + " " + label + " ";
      int tw = fm.stringWidth(txt);
      int th = fm.getHeight();
      gc.setColor(new Color(color.getRed(), color.getGreen(), color.getBlue(), 180));
      gc.fillRect(lastValidX + 2, lastValidY - th / 2, tw + 4, th + 2);
      gc.setColor(Color.BLACK);
      gc.drawString(txt, lastValidX + 4, lastValidY - th / 2 + fm.getAscent());
    }
  }

  // ================================================================================================
  // Auto-fetch (background thread; never blocks calc/draw)
  // ================================================================================================

  /** Check the ET clock and fire a background fetch inside a scheduled window. */
  private void maybeScheduleFetch()
  {
    if (!getSettings().getBoolean(AUTO_FETCH, true)) return;

    java.time.ZonedDateTime etNow;
    try {
      etNow = java.time.ZonedDateTime.now(java.time.ZoneId.of("America/New_York"));
    } catch (Exception e) { return; }
    int etMins = etNow.getHour() * 60 + etNow.getMinute();

    int matchedSlot = -1;
    for (int slot : FETCH_MINUTES_ET) {
      int diff = etMins - slot;
      if (diff >= 0 && diff < 5) { matchedSlot = slot; break; }
    }
    if (matchedSlot < 0) return;
    if (matchedSlot == lastFetchMinuteET) return;
    if (System.currentTimeMillis() - lastFetchTimeMs < 4 * 60 * 1000L) return; // debounce

    lastFetchMinuteET = matchedSlot;
    lastFetchTimeMs = System.currentTimeMillis();
    startFetch(false);
  }

  /** Spawn a daemon thread to GET the data; result lands in {@link #fetchedData}. */
  private void startFetch(boolean startup)
  {
    if (fetchInFlight) return;
    final boolean hasKey = !getSettings().getString(API_KEY, "").trim().isEmpty();
    // On a timed (non-startup) fetch, require a key (free mode is fetched once at startup).
    if (!startup && !hasKey) return;

    final String ticker = isNqFamily() ? "NDX" : "SPX";
    final String apiKey = getSettings().getString(API_KEY, "").trim();
    fetchInFlight = true;

    Thread t = new Thread(() -> {
      try {
        String urlStr = hasKey
            ? API_URL + "?ticker=" + ticker
            : API_URL + "?ticker=" + ticker + "&mode=free";
        HttpURLConnection conn = (HttpURLConnection) URI.create(urlStr).toURL().openConnection();
        conn.setRequestMethod("GET");
        conn.setConnectTimeout(8000);
        conn.setReadTimeout(8000);
        conn.setRequestProperty("User-Agent", "TLADe-MW/1.0");
        if (hasKey) conn.setRequestProperty("X-API-Key", apiKey);

        int code = conn.getResponseCode();
        String data = readAll(code >= 200 && code < 400 ? conn.getInputStream() : conn.getErrorStream());
        if (data != null && data.contains("L:")) {
          fetchedData = data;
          delayedMode = !hasKey;
          lastFetchError = null; // clear any previous error state on success
          // Timestamp write is guarded — on macOS Java 25 the TZ resolution
          // throws here and used to skip the recalculate below, leaving the
          // chart with zero levels until the user removed + re-added the
          // study. Same fallback path as the scheduler helper.
          try {
            lastFetchEt = java.time.ZonedDateTime.now(java.time.ZoneId.of("America/New_York"))
                .format(java.time.format.DateTimeFormatter.ofPattern("HH:mm"));
          } catch (Throwable tzt) {
            int etMins = nowETMinutesWithFallback();
            if (etMins >= 0) {
              lastFetchEt = String.format("%02d:%02d", etMins / 60, etMins % 60);
            }
          }
          // Re-run calc so the new string is parsed and the draw model rebuilt — a bare
          // notifyRedraw() would only repaint the (still-empty) figure.
          DataContext c = lastCtx;
          if (c != null) recalculate(c);
          else notifyRedraw();
        } else {
          // v1.3.4 — response received but body does not contain a level payload
          // (e.g. HTTP 401 invalid key, 403 forbidden, 429 rate limited, or a
          // maintenance page). Surface the error in the banner instead of the
          // previous silent-fail that showed "updated — ET" indefinitely.
          String snippet = (data != null && !data.isEmpty())
              ? " · " + data.substring(0, Math.min(40, data.length())).replaceAll("\\s+", " ")
              : "";
          lastFetchError = "HTTP " + code + snippet;
          System.err.println("[TLADe] fetch no L: " + lastFetchError);
          DataContext c = lastCtx;
          if (c != null) recalculate(c);
          else notifyRedraw();
        }
      } catch (Exception e) {
        // v1.3.4 — was silent-fail; surface network/TLS errors in the banner
        // so the user can distinguish "key wrong" from "network unreachable".
        lastFetchError = "net: " + e.getClass().getSimpleName()
            + (e.getMessage() != null ? " · " + e.getMessage() : "");
        System.err.println("[TLADe] fetch failed: " + e);
        DataContext c = lastCtx;
        if (c != null) recalculate(c);
        else notifyRedraw();
      } finally {
        fetchInFlight = false;
      }
    }, "TLADe-GEX-fetch");
    t.setDaemon(true);
    t.start();
  }

  private static String readAll(InputStream in) throws Exception
  {
    if (in == null) return null;
    try (InputStream s = in) {
      byte[] buf = s.readAllBytes();
      return new String(buf, StandardCharsets.UTF_8);
    }
  }

  // ================================================================================================
  // The dashboard figure — paints lines, labels and the profile histogram in pixel space.
  // ================================================================================================

  private class DashboardFigure extends Figure
  {
    @Override
    public boolean isVisible(DrawContext ctx) { return true; }

    @Override
    public void draw(Graphics2D gc, DrawContext ctx)
    {
      Rectangle b = ctx.getBounds();
      if (b == null) return;

      gc.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
      gc.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, RenderingHints.VALUE_TEXT_ANTIALIAS_ON);

      // Session boxes, confluence zones and profile first (underlay), then level lines/labels on top.
      paintBoxes(gc, ctx, b);
      paintZones(gc, ctx, b);
      if (!drawProfile.isEmpty())
        paintProfile(gc, ctx, b);

      // v1.3 — Session AVWAP polylines below the GEX levels so the horizontal
      // wall lines stay visually dominant over the slower-moving curves.
      paintAvwapPolylines(gc, ctx, b);

      if (!drawLevels.isEmpty())
        paintLevels(gc, ctx, b);

      if (delayedMode)
        drawDelayedBanner(gc, b);

      // Diagnostics — drawn last so it is always visible, even when nothing else renders.
      if (getSettings().getBoolean(SHOW_STATUS, true))
        drawStatus(gc, b);
    }

    private void drawStatus(Graphics2D gc, Rectangle b)
    {
      String txt = statusText;
      if (txt == null || txt.isEmpty()) return;
      String[] lines = txt.split("\n");

      Object oldAA = gc.getRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING);
      gc.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING,
          RenderingHints.VALUE_TEXT_ANTIALIAS_ON);

      Font title = new Font(Font.SANS_SERIF, Font.BOLD, 14);
      Font body  = new Font(Font.SANS_SERIF, Font.PLAIN, 13);
      FontMetrics tfm = gc.getFontMetrics(title);
      FontMetrics bfm = gc.getFontMetrics(body);

      // Width = widest line measured under its own font.
      int w = tfm.stringWidth(lines[0]);
      for (int i = 1; i < lines.length; i++) w = Math.max(w, bfm.stringWidth(lines[i]));

      int padX = 10, padY = 8;
      int titleH = tfm.getHeight();
      int bodyH  = bfm.getHeight();
      int boxW = w + padX * 2;
      int boxH = titleH + (lines.length - 1) * bodyH + padY * 2;
      int m = 8;
      String pos = getSettings().getString(STATUS_POS, "BL");
      boolean right  = pos.equals("TR") || pos.equals("BR");
      boolean bottom = pos.equals("BL") || pos.equals("BR");
      int x = right  ? b.x + b.width  - boxW - m : b.x + m;
      int y = bottom ? b.y + b.height - boxH - m : b.y + m;

      // Opaque rounded panel + amber border so it stays legible over any chart.
      gc.setColor(new Color(18, 22, 31, 242));
      gc.fillRoundRect(x, y, boxW, boxH, 8, 8);
      gc.setColor(new Color(245, 158, 11));
      gc.drawRoundRect(x, y, boxW, boxH, 8, 8);

      // Title bold amber, then body lines in light grey.
      gc.setFont(title);
      gc.setColor(new Color(245, 158, 11));
      int ty = y + padY + tfm.getAscent();
      gc.drawString(lines[0], x + padX, ty);
      ty += (titleH - tfm.getAscent()) + bfm.getAscent();
      gc.setFont(body);
      gc.setColor(new Color(226, 232, 240));
      for (int i = 1; i < lines.length; i++) {
        gc.drawString(lines[i], x + padX, ty);
        ty += bodyH;
      }

      if (oldAA != null)
        gc.setRenderingHint(RenderingHints.KEY_TEXT_ANTIALIASING, oldAA);
    }

    private void paintBoxes(Graphics2D gc, DrawContext ctx, Rectangle b)
    {
      List<DrawBox> boxes = drawBoxes;
      if (boxes == null || boxes.isEmpty()) return;
      for (DrawBox bx : boxes) {
        int x0 = ctx.translateTime(bx.t0), x1 = ctx.translateTime(bx.t1);
        int yT = ctx.translateValue(bx.hi), yB = ctx.translateValue(bx.lo);
        if (x1 < x0) { int tmp = x0; x0 = x1; x1 = tmp; }
        if (x1 - x0 < 2) x1 = x0 + 2;
        gc.setColor(bx.color);
        gc.fillRect(x0, yT, x1 - x0, Math.max(1, yB - yT));
        gc.setColor(new Color(bx.color.getRed(), bx.color.getGreen(), bx.color.getBlue(), 90));
        gc.drawRect(x0, yT, x1 - x0, Math.max(1, yB - yT));
      }
    }

    private void paintZones(Graphics2D gc, DrawContext ctx, Rectangle b)
    {
      List<DrawZone> zones = drawZones;
      if (zones == null || zones.isEmpty()) return;
      Font font = labelFont(ctx.getDefaults());
      gc.setFont(font);
      FontMetrics fm = gc.getFontMetrics();
      for (DrawZone z : zones) {
        int yTop = ctx.translateValue(z.top), yBot = ctx.translateValue(z.bottom);
        if (yBot - yTop < 2) { yTop -= 1; yBot += 1; }
        gc.setColor(z.color);
        gc.fillRect(b.x, yTop, b.width, yBot - yTop);
        gc.setColor(new Color(z.color.getRed(), z.color.getGreen(), z.color.getBlue(), 200));
        gc.drawString(z.label, b.x + 4, (yTop + yBot) / 2 + fm.getAscent() / 2);
      }
    }

    private void paintLevels(Graphics2D gc, DrawContext ctx, Rectangle b)
    {
      Font font = labelFont(ctx.getDefaults());
      gc.setFont(font);
      FontMetrics fm = gc.getFontMetrics();

      for (DrawLevel d : drawLevels) {
        int yPix = ctx.translateValue(d.price);
        if (yPix < b.y - 2 || yPix > b.y + b.height + 2) continue; // off-screen

        gc.setStroke(strokeFor(d.style, d.lineWidth));
        gc.setColor(d.color);
        gc.drawLine(b.x, yPix, b.x + b.width, yPix);

        if (!d.label.isEmpty()) {
          int textW = fm.stringWidth(d.label);
          int padX = 4, padY = 1;
          int chipW = textW + padX * 2;
          int chipH = fm.getHeight() + padY * 2;
          int x = b.x + b.width - chipW - 2;
          int y2 = yPix - chipH / 2;
          gc.setColor(d.color);
          gc.fillRect(x, y2, chipW, chipH);
          gc.setColor(contrastColor(d.color));
          gc.drawString(d.label, x + padX, y2 + padY + fm.getAscent());
        }
      }
    }

    private void paintProfile(Graphics2D gc, DrawContext ctx, Rectangle b)
    {
      double barWidthPx = Math.max(1.0, ctx.getBarWidthAsDouble());
      double maxLenPx = Math.min(b.width, profWidthBars * barWidthPx);

      double tickHeightPx = ctx.getTickHeight();
      // Fall back to a sane pixel height if the SDK reports a non-positive tick height.
      if (!(tickHeightPx > 0)) tickHeightPx = 1.0;
      int halfH = Math.max(2, (int) Math.round(profHeightTicks * tickHeightPx / 2.0));

      int rightX = b.x + b.width;

      gc.setStroke(new BasicStroke(1f));
      for (DrawProf dp : drawProfile) {
        int yPix = ctx.translateValue(dp.price);
        if (yPix < b.y - halfH || yPix > b.y + b.height + halfH) continue;

        int barLenPx = (int) Math.round(dp.frac * maxLenPx);
        if (barLenPx <= 0) continue;
        if (barLenPx > b.width) barLenPx = b.width;

        int x = rightX - barLenPx;
        gc.setColor(dp.color);
        gc.fillRect(x, yPix - halfH, barLenPx, halfH * 2);
        gc.drawRect(x, yPix - halfH, barLenPx, halfH * 2);
      }
    }

    private void drawDelayedBanner(Graphics2D gc, Rectangle b)
    {
      String msg = "DELAYED DATA (3 days) — Subscribe at tradelikeadealer.com for live levels";
      gc.setFont(new Font("Dialog", Font.PLAIN, 11));
      FontMetrics fm = gc.getFontMetrics();
      int w = fm.stringWidth(msg) + 12;
      int x = b.x + b.width - w - 6;
      int y = b.y + 6;
      gc.setColor(new Color(0, 0, 0, 160));
      gc.fillRect(x, y, w, fm.getHeight() + 4);
      gc.setColor(new Color(255, 165, 0));
      gc.drawString(msg, x + 6, y + 2 + fm.getAscent());
    }
  }

  // ---- figure helpers --------------------------------------------------------------------------

  private Color colorFor(LevelEntry lvl, double y, double spot)
  {
    switch (lvl.type) {
      // colour = NATURE: a Call Wall stays the call colour whatever its role (flip = thinner + ↺)
      case "CW": return negColor;
      case "PW": return posColor;
      case "ZG": return C_ZG;
      case "MP": return C_MP;
      case "EH": case "EL": case "EHR": case "ELR": return C_EM;
      case "VH": case "VL": return C_VB;
      case "CM": return C_CHARM;
      case "DF": return C_DFLIP;
      case "BL": return C_BO_BULL;
      case "BS": return C_BO_BEAR;
      default: return C_STRUCT;
    }
  }


  private static Stroke strokeFor(int style, int width)
  {
    if (style == 0) return new BasicStroke(width); // solid
    if (style == 1) return new BasicStroke(width, BasicStroke.CAP_BUTT, BasicStroke.JOIN_MITER,
        10f, new float[] {6f, 4f}, 0f); // dashed
    return new BasicStroke(width, BasicStroke.CAP_ROUND, BasicStroke.JOIN_ROUND,
        10f, new float[] {1f, 4f}, 0f); // dotted
  }

  private int userLineStyle()
  {
    String st = getSettings().getString(LINE_STYLE, "Dotted");
    return st.equalsIgnoreCase("Solid") ? 0 : st.equalsIgnoreCase("Dashed") ? 1 : 2;
  }

  // Line styles as the TradingView indicator: walls and Zero Gamma in the user's style, Max Pain /
  // Vol Bands / Structure dotted, EM and Delta Flip dashed, Charm Magnet solid.
  private int styleFor(String type)
  {
    switch (type) {
      case "CM": return 0;
      case "MP": case "VH": case "VL": case "PDH": case "PDL": case "PWH": case "PWL": return 2;
      case "EH": case "EL": case "EHR": case "ELR": case "DF": return 1;
      default: return userLineStyle();
    }
  }

  private Font labelFont(Defaults defaults)
  {
    int size = clamp(getSettings().getInteger(LABEL_SIZE, 11), 6, 50);
    Font base = (defaults != null) ? defaults.getFont() : null;
    return (base != null) ? base.deriveFont((float) size) : new Font("Arial", Font.PLAIN, size);
  }

  private static Color contrastColor(Color bg)
  {
    double lum = (0.299 * bg.getRed() + 0.587 * bg.getGreen() + 0.114 * bg.getBlue()) / 255.0;
    return lum > 0.55 ? Color.BLACK : Color.WHITE;
  }

  private static int clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
}
