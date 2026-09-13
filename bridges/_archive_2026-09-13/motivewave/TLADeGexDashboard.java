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
    boolean gex, sys;   // stroke style: gex=solid, sys=dash, else=dot
    int lineWidth;
    String label;       // "" when labels hidden
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
    tickerGrp.addRow(new DoubleDescriptor(ES_SPX_SPREAD, "ES-SPX Spread", 24.0, 0.0, 1000.0, 0.01));
    tickerGrp.addRow(new DoubleDescriptor(NQ_NDX_SPREAD, "NQ-NDX Spread", 40.0, 0.0, 1000.0, 0.01));

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
    visGrp.addRow(new BooleanDescriptor(SHOW_STRUCTURE, "Show Structure Levels (PDH/PDL/PWH/PWL)", true));
    visGrp.addRow(new BooleanDescriptor(SHOW_BREAKOUT, "Show Breakout Areas (BL/BS)", true));
    visGrp.addRow(new BooleanDescriptor(SHOW_CHARM_MAGNET, "Show Charm Magnet (CM)", true));

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

    buildStatus(series, instr, spot, dataStr);

    clearFigures();
    beginFigureUpdate();
    addFigure(new DashboardFigure());
    endFigureUpdate();
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
        else if (struct && showStructure) shouldShow = true;
        else if (breakout && getSettings().getBoolean(SHOW_BREAKOUT, true)) shouldShow = true;
        else if (charm    && getSettings().getBoolean(SHOW_CHARM_MAGNET, true)) shouldShow = true;

        if (!shouldShow || !inRange) continue;

        if ((lvl.type.equals("CW") || lvl.type.equals("PW")) && !maxIsAll && !isProtected) {
          if (isAbove) gexAboveDrawn++; else gexBelowDrawn++;
        }

        DrawLevel d = new DrawLevel();
        d.price = y;
        d.color = colorFor(lvl, y, spot);
        d.gex = gex;
        d.sys = sys;
        d.lineWidth = (lvl.type.equals("ZG") || lvl.type.equals("MP")) ? 2 : 1;
        d.label = showLabels ? (formatPrice(lvl.esStrike) + " " + lvl.label) : "";
        newLevels.add(d);
      }

      // v3.x — local Structure (PDH/PDL/PWH/PWL) from chart bars (Mother is GEX-only;
      // price-dependent levels computed front-end-side, no longer shipped in the feed).
      if (showStructure) {
        String[] paLabels = { "PDH", "PDL", "PWH", "PWL" };
        java.awt.Color paColor = new java.awt.Color(0x94, 0xA3, 0xB8);
        for (double[] pa : computeLocalPA(ctx.getDataSeries())) {
          double yy = pa[0];
          boolean inR = !onlyNear || (Math.abs(spot - yy) / Math.max(1e-9, spot) * 100.0 <= nearPct);
          if (!inR) continue;
          DrawLevel dl = new DrawLevel();
          dl.price = yy;
          dl.color = paColor;
          dl.gex = false;
          dl.sys = false;
          dl.lineWidth = 1;
          dl.label = showLabels ? (formatPrice(yy) + " " + paLabels[(int) pa[1]]) : "";
          newLevels.add(dl);
        }
      }

      // v3.x — local Breakout (BOS H4/H1) from chart bars (Mother is GEX-only; price-dependent
      // levels computed front-end-side, 1:1 with NT8/ATAS/terminal).
      if (getSettings().getBoolean(SHOW_BREAKOUT, true)) {
        java.awt.Color bullC = new java.awt.Color(0x10, 0xB9, 0x81);
        java.awt.Color bearC = new java.awt.Color(0xF4, 0x3F, 0x5E);
        for (Object[] b : computeLocalBOS(ctx.getDataSeries())) {
          double yb = (Double) b[0];
          boolean inR = !onlyNear || (Math.abs(spot - yb) / Math.max(1e-9, spot) * 100.0 <= nearPct);
          if (!inR) continue;
          DrawLevel db = new DrawLevel();
          db.price = yb;
          db.color = ((Boolean) b[2]) ? bullC : bearC;
          db.gex = false;
          db.sys = false;
          db.lineWidth = 1;
          db.label = showLabels ? (formatPrice(yb) + " " + (String) b[1]) : "";
          newLevels.add(db);
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

    // v1.3 — populate the 4 Session AVWAP arrays from the same DataSeries.
    // Runs after the level model so a slow PA → AVWAP recompute can't delay
    // the GEX lines from showing up.
    try { computeAvwapForSeries(ctx.getDataSeries()); }
    catch (Exception ignore) { /* keep the previous AVWAP arrays on failure */ }
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
    double esSpx = !Double.isNaN(spreadOverride)
        ? spreadOverride
        : getSettings().getDouble(ES_SPX_SPREAD, 24.0);
    double nqNdx = getSettings().getDouble(NQ_NDX_SPREAD, 40.0);
    switch (t) {
      case "SPX": return esPrice - esSpx;
      case "SPY": return (esPrice - esSpx) / 10.0;
      case "NQ":  return esPrice;
      case "NDX": return esPrice - nqNdx;
      case "QQQ": return (esPrice - nqNdx) / 40.0;
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
    double curH = 0, curL = 0, curC = 0;
    final long anchor = 18L * 60L; // 18:00 ET
    for (int i = 0; i < barCount; i++) {
      long t = series.getStartTime(i);
      java.time.ZonedDateTime et = java.time.Instant.ofEpochMilli(t).atZone(etZone);
      long etMin = et.toLocalDate().toEpochDay() * 1440L + et.getHour() * 60L + et.getMinute();
      long slot = Math.floorDiv(etMin - anchor, periodMinutes) * periodMinutes + anchor;
      double h = series.getHigh(i), l = series.getLow(i), c = series.getClose(i);
      if (!has || slot != curSlot) {
        if (has) out.add(new double[] { curH, curL, curC });
        has = true; curSlot = slot; curH = h; curL = l; curC = c;
      } else {
        if (h > curH) curH = h;
        if (l < curL) curL = l;
        curC = c;
      }
    }
    if (has) out.add(new double[] { curH, curL, curC });
    return out;
  }

  private static boolean isQualityBreakout(double[] curr, double[] prev, boolean bull)
  {
    // arr = {high, low, close}
    if (bull) {
      if (curr[2] <= prev[0]) return false;
      double bodyAbove = curr[2] - prev[0], upperShadow = curr[0] - curr[2];
      return bodyAbove > upperShadow && bodyAbove > 0;
    } else {
      if (curr[2] >= prev[1]) return false;
      double bodyBelow = prev[1] - curr[2], lowerShadow = curr[2] - curr[1];
      return bodyBelow > lowerShadow && bodyBelow > 0;
    }
  }

  // out entries: {price, bull ? 1 : 0}
  private void detectBosInto(java.util.List<double[]> c, java.util.List<double[]> out)
  {
    if (c.size() < 3) return;
    boolean foundBull = false, foundBear = false;
    for (int i = c.size() - 2; i >= 2 && (!foundBull || !foundBear); i--) {
      double[] curr = c.get(i); double[] prev = c.get(i - 1);
      if (!foundBull && isQualityBreakout(curr, prev, true)) {
        boolean valid = true;
        for (int j = i + 1; j < c.size(); j++) if (c.get(j)[2] < prev[0]) { valid = false; break; }
        if (valid) { out.add(new double[] { prev[0], 1 }); foundBull = true; }
      }
      if (!foundBear && isQualityBreakout(curr, prev, false)) {
        boolean valid = true;
        for (int j = i + 1; j < c.size(); j++) if (c.get(j)[2] > prev[1]) { valid = false; break; }
        if (valid) { out.add(new double[] { prev[1], 0 }); foundBear = true; }
      }
    }
  }

  // returns {price, label, bull} per detected BOS
  private java.util.List<Object[]> computeLocalBOS(DataSeries series)
  {
    java.util.List<Object[]> out = new java.util.ArrayList<>();
    if (series == null) return out;
    int n = series.size();
    if (n < 3) return out;
    String[] tfs = { "H4", "H1" };
    int[] periods = { 240, 60 };
    for (int k = 0; k < 2; k++) {
      java.util.List<double[]> found = new java.util.ArrayList<>();
      detectBosInto(aggregateByEtPeriod(series, n, periods[k]), found);
      for (double[] f : found) {
        boolean bull = f[1] > 0.5;
        out.add(new Object[] { f[0], "BOS " + tfs[k] + (bull ? " L" : " S"), bull });
      }
    }
    return out;
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
        || t.equals("VH") || t.equals("VL");
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
      drawOneAvwap(gc, ctx, b, times, aA, clipFrom, new Color(0xF5, 0x9E, 0x0B), "Asia", width);
    if (getSettings().getBoolean(SHOW_AVWAP_EU, true))
      drawOneAvwap(gc, ctx, b, times, aE, clipFrom, new Color(0x3B, 0x82, 0xF6), "EU", width);
    if (getSettings().getBoolean(SHOW_AVWAP_US, true))
      drawOneAvwap(gc, ctx, b, times, aU, clipFrom, new Color(0x22, 0xC5, 0x5E), "US", width);
    if (getSettings().getBoolean(SHOW_AVWAP_PD, true))
      drawOneAvwap(gc, ctx, b, times, aP, clipFrom, new Color(0x6E, 0xE7, 0xB7), "PD", width);
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
      lastValidX = x; lastValidY = y;
      haveLast = true;
    }

    if (haveLast && getSettings().getBoolean(SHOW_AVWAP_LABELS, true)) {
      Font f = new Font("Arial", Font.PLAIN, 11);
      gc.setFont(f);
      FontMetrics fm = gc.getFontMetrics();
      String txt = " " + label + " ";
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

      // Profile first (underlay), then level lines/labels on top.
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

    private void paintLevels(Graphics2D gc, DrawContext ctx, Rectangle b)
    {
      Font font = labelFont(ctx.getDefaults());
      gc.setFont(font);
      FontMetrics fm = gc.getFontMetrics();

      for (DrawLevel d : drawLevels) {
        int yPix = ctx.translateValue(d.price);
        if (yPix < b.y - 2 || yPix > b.y + b.height + 2) continue; // off-screen

        gc.setStroke(strokeFor(d.gex, d.sys, d.lineWidth));
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
      case "CW": {
        boolean flipped = y < spot;
        return flipped ? posColor : negColor;
      }
      case "PW": {
        boolean flipped = y > spot;
        return flipped ? negColor : posColor;
      }
      case "ZG": return new Color(0xA9, 0xA9, 0xA9); // DarkGray (#A9A9A9, matches NT8 Brushes.DarkGray)
      case "MP": return new Color(0xff, 0x00, 0x00); // Red
      case "EH":
      case "EL": return new Color(0x1e, 0x90, 0xff); // DodgerBlue
      case "VH":
      case "VL": return new Color(0x80, 0x80, 0x80); // Gray
      // v1.3 — Breakout / Charm Magnet (palettes chosen distinct from CW/PW)
      case "BL": return new Color(0x10, 0xB9, 0x81); // emerald — Breakout Long
      case "BS": return new Color(0xF4, 0x3F, 0x5E); // rose    — Breakout Short
      case "CM": return new Color(0xC0, 0x73, 0xFF); // violet  — Charm Magnet
      default:
        if (isStructure(lvl.type)) return new Color(0xA9, 0xA9, 0xA9); // DarkGray (#A9A9A9, matches NT8 Brushes.DarkGray)
        return new Color(0x80, 0x80, 0x80); // Gray
    }
  }

  private static Stroke strokeFor(boolean gex, boolean sys, int width)
  {
    if (gex) return new BasicStroke(width); // solid
    if (sys) return new BasicStroke(width, BasicStroke.CAP_BUTT, BasicStroke.JOIN_MITER,
        10f, new float[] {6f, 4f}, 0f); // dash
    return new BasicStroke(width, BasicStroke.CAP_ROUND, BasicStroke.JOIN_ROUND,
        10f, new float[] {1f, 4f}, 0f); // dot
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
