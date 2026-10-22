// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe publication age — shared by the GEX Dashboard and the Quantum Field Ladder (3.6.0)
//  ─────────────────────────────────────────────────────────────────────────────
//  The data string carries "T:<dateId>,<SESSION>,<hh:mm>;" as the first row of P: — the
//  publication the levels come from. Each indicator shows it, and compares it with the
//  publication that should exist at the current ET time: when the held one is older the
//  chart says STALE instead of drawing Friday's levels as if they were today's (one
//  subscriber had two charts, one on Friday's CLOSE and one on today's, 21/10/2026).
//
//  PUBLICATION SCHEDULE, ET — A COPY. The canonical copy is C:\TLADe\config\sessionConfig.ts
//  (SESSIONS[].times.NY, read by getAutopilotSchedules). This DLL cannot import it
//  (CLAUDE.md Rule #12, isolated trees): if the schedule changes there, change it here too.
//    ASIA 18:01 the evening before (dateId = the next trading day; none on Friday evening,
//    Sunday 18:01 opens Monday) · EUROPE 03:15 · PRE_OPEN 08:00 · RTH_OPEN 09:30 ·
//    OPENING_RANGE 10:30 · CLOSE 13:00. Holidays are not modelled: on a US holiday with no
//    publication the chart reports STALE, which is the truth.
//
//  The same rule, in the other languages of the bundle: NinjaTrader (TLADeGexDashboardNT.cs),
//  MotiveWave (TLADeGexDashboard.java), Quantower (GammaExposure.cs), TradingView (Pine).
// ═══════════════════════════════════════════════════════════════════════════════
using System;
using System.Globalization;

namespace ATAS.Indicators.Technical
{
    internal static class TLAdePublication
    {
        private static readonly string[] SESSIONS = { "ASIA", "EUROPE", "PRE_OPEN", "RTH_OPEN", "OPENING_RANGE", "CLOSE" };
        public const int GRACE_MIN = 10;   // minutes past the scheduled time before a publication is expected

        public static int Rank(string session) => Array.IndexOf(SESSIONS, session ?? "");

        // Orderable key of a publication: day number * 8 + session rank; -1 when unknown.
        public static long Key(string dateId, string session)
        {
            int rank = Rank(session);
            if (rank < 0) return -1;
            if (!DateTime.TryParseExact(dateId ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return -1;
            return (long)(d - new DateTime(2000, 1, 1)).TotalDays * 8 + rank;
        }

        // The publication that should exist at this ET time (schedule above, plus the grace).
        public static void Expected(DateTime etNow, out string dateId, out string session)
        {
            int mins = etNow.Hour * 60 + etNow.Minute;
            DateTime day = etNow.Date;
            DayOfWeek dow = etNow.DayOfWeek;
            if (dow == DayOfWeek.Saturday) { day = day.AddDays(-1); session = "CLOSE"; }
            else if (dow == DayOfWeek.Sunday)
            {
                if (mins >= 18 * 60 + 1 + GRACE_MIN) { day = day.AddDays(1); session = "ASIA"; }
                else { day = day.AddDays(-2); session = "CLOSE"; }
            }
            else if (mins >= 18 * 60 + 1 + GRACE_MIN)
            {
                if (dow == DayOfWeek.Friday) session = "CLOSE";
                else { day = day.AddDays(1); session = "ASIA"; }
            }
            else if (mins >= 13 * 60 + GRACE_MIN) session = "CLOSE";
            else if (mins >= 10 * 60 + 30 + GRACE_MIN) session = "OPENING_RANGE";
            else if (mins >= 9 * 60 + 30 + GRACE_MIN) session = "RTH_OPEN";
            else if (mins >= 8 * 60 + GRACE_MIN) session = "PRE_OPEN";
            else if (mins >= 3 * 60 + 15 + GRACE_MIN) session = "EUROPE";
            else session = "ASIA";
            dateId = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static long ExpectedKey(DateTime etNow)
        {
            Expected(etNow, out var d, out var s);
            return Key(d, s);
        }

        public static DateTime EtNow()
        {
            TimeZoneInfo et;
            try { et = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch { et = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et);
        }

        // Reads "T:2026-10-21,OPENING_RANGE,10:30;" out of the raw string: a row that starts right
        // after "P:", ";" or "|" (never inside a label). Absent → the three fields come back empty.
        public static void ParseMarker(string raw, out string dateId, out string session, out string hhmm)
        {
            dateId = ""; session = ""; hhmm = "";
            if (string.IsNullOrEmpty(raw)) return;
            int t = raw.IndexOf("T:", StringComparison.Ordinal);
            while (t >= 0)
            {
                char before = t > 0 ? raw[t - 1] : '|';
                if (before == ':' || before == ';' || before == '|')
                {
                    int end = raw.IndexOf(';', t);
                    string body = end > t ? raw.Substring(t + 2, end - t - 2) : raw.Substring(t + 2);
                    string[] f = body.Split(',');
                    if (f.Length == 3 && Key(f[0], f[1]) >= 0) { dateId = f[0]; session = f[1]; hhmm = f[2]; return; }
                }
                t = raw.IndexOf("T:", t + 2, StringComparison.Ordinal);
            }
        }

        // STALE = live data whose publication is older than the one expected now. Free data is
        // three days old by design and already wears the FREE badge; a string without marker
        // cannot be judged (the label says so).
        public static bool IsStale(bool delayedMode, string heldDateId, string heldSession, out string expectedDateId, out string expectedSession)
        {
            Expected(EtNow(), out expectedDateId, out expectedSession);
            if (delayedMode) return false;
            long held = Key(heldDateId, heldSession);
            if (held < 0) return false;
            return held < Key(expectedDateId, expectedSession);
        }

        // The status line: what the levels are, and when stale, what was expected instead.
        // `manual`: a pasted string is on the chart, and on ATAS it wins over any fetch.
        public static string Label(bool delayedMode, string heldDateId, string heldSession, string heldHhmm, bool autoFetchLive, bool manual, out bool stale)
        {
            stale = IsStale(delayedMode, heldDateId, heldSession, out var expDate, out var expSession);
            string label = string.IsNullOrEmpty(heldDateId)
                ? "levels: publication unknown (string without T: marker — copy a fresh one from the terminal)"
                : $"levels: {heldDateId} {heldSession} {heldHhmm} ET";
            if (!stale) return label;
            string hint = manual
                ? "paste the latest string from the terminal, or clear the pasted string to let auto-fetch run"
                : autoFetchLive
                    ? "auto-fetch is retrying"
                    : "set your API key and Auto-fetch, or paste the latest string from the terminal";
            return $"{label} — STALE: expected {expDate} {expSession}; {hint}";
        }
    }

    // Decides, once a minute, whether the cloud must be asked again. Wall-clock, not bars: a
    // chart with no tick, a PC back from sleep and a late publication all get their fetch within
    // a minute of becoming due. Replaces the six fixed ET slots, which the 02:05 one made blind
    // to EUROPE (published 03:15 ET) until 08:05.
    internal sealed class TLAdeCatchUp
    {
        private DateTime _lastAttemptUtc = DateTime.MinValue;
        private int _misses = 0;
        private long _freeKeyFetched = -1;

        // Call right after the mount fetch so the first tick does not repeat it.
        public void MarkFetched(long expectedKey)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            _freeKeyFetched = expectedKey;
        }

        public bool Due(bool live, long heldKey, long expectedKey)
        {
            if (live && heldKey >= 0)
            {
                // Live with a marker: fetch while what we hold is older than what should exist —
                // 5 minutes apart for three tries, then every 15. A late publication costs at most
                // 4 calls in its first quarter hour and 4 an hour after that, inside the 60/hour
                // per key even with five charts.
                if (heldKey >= expectedKey) { _misses = 0; return false; }
                double gap = _misses < 3 ? 5 : 15;
                if ((DateTime.UtcNow - _lastAttemptUtc).TotalMinutes < gap) return false;
                _misses++;
                _lastAttemptUtc = DateTime.UtcNow;
                return true;
            }
            // Free data, or a string without marker: one fetch per expected publication.
            if (expectedKey == _freeKeyFetched) return false;
            _freeKeyFetched = expectedKey;
            _lastAttemptUtc = DateTime.UtcNow;
            return true;
        }
    }
}
