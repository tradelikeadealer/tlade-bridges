// ═══════════════════════════════════════════════════════════════════════════════
//  SPDX-License-Identifier: MIT
//  Copyright (c) 2026 TLADe — Trade Like a Dealer (https://tradelikeadealer.com)
//
//  TLADe GEX Dashboard — Sierra Chart (ACSIL) — release 3.6.1 (28 October 2026)
//  ─────────────────────────────────────────────────────────────────────────────
//  A PORT of the ATAS edition, bridge/bridges/atas/TLAdeGexDashboardATAS.cs 3.6.1, which is
//  the most complete of the C# copies. Same names, colours, line styles, wall-flip rule, breakout
//  rule, session AVWAP rule, session boxes, confluence zones, profile bars, themes, status and
//  catch-up fetch. The Worldlines H1 block is a port of the TradingView indicator
//  (bridge/bridges/tradingview/gex_es_spx_spy.pine, "WORLDLINES H1 (3.6.1)"), the only copy
//  that has them. CANONICAL for every calculation: terminal/services/clientEngineService.ts.
//  This file cannot import it (CLAUDE.md Rule #12, isolated trees): change both.
//
//  Kept from the community Sierra study by Alex (bridge/bridges/Sierra/Alex, 6 October 2026),
//  with thanks: the WinHTTP fetch on a background thread, the API key wiped from memory after
//  use, the New York clock from the Windows time zone database, the HTTP 401/403/429 handling
//  and the fetch status in the corner of the chart.
//
//  Drawing: one GDI pass (sc.p_GDIFunction), the counterpart of the ATAS OnRender. Calculations
//  run in the study function on the chart's own bars; bar times are converted from the chart's
//  time zone to New York, so sessions are right whatever time zone the chart is set to.
// ═══════════════════════════════════════════════════════════════════════════════

#include "sierrachart.h"

#include <algorithm>
#include <climits>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <iterator>
#include <map>
#include <memory>
#include <process.h>
#include <set>
#include <string>
#include <vector>
#include <winhttp.h>

SCDLLName("TLADe GEX Dashboard")

namespace TLADeGex
{
enum InputIndex
{
	InApiKey, InAutoFetch, InRefreshNow, InTicker, InManualSpread, InSnapToTick, InShowAboveH1,
	InShowGex, InShowSystem, InShowStructure, InShowCharm, InShowDeltaFlip, InShowLabels,
	InShowAxisMarkers, InShowStatus, InMaxGex, InEnableThreshold, InThreshold, InShowOnlyNear,
	InNearPct, InTheme, InLineStyle, InLineWidthGex, InLineWidthSystem, InFontSize,
	InLabelInherit, InLabelTextColor, InLabelBgAlpha, InLabelBgPositional, InLabelBgAbove,
	InLabelBgBelow, InScaleWalls, InWallMaxPct, InWallMinPct, InShowGexDirection,
	InGexScaleByStrength, InGexTickMin, InGexTickMax, InGexTickWidth, InAvwapAsia, InAvwapEU,
	InAvwapUS, InAvwapPD, InAvwapHist, InAvwapWidth, InAvwapLabels, InShowBreakout, InBosM,
	InBosW, InBosD, InBosH4, InBosH1, InBosKeep, InShowConfluence, InConfMinSize, InConfEmPct,
	InShowProfile, InProfileWidth, InProfileBarTicks, InProfileScaleMax, InShowBoxes, InBoxAsia,
	InBoxEU, InBoxPre, InBoxUS, InBoxHist, InEnableAlerts, InAlertWalls, InAlertSystem,
	InAlertBos, InShowWorldlines, InWlBull, InWlBear, InWlLabels
};

enum Ticker { T_AUTO, T_ES, T_SPX, T_SPY, T_NQ, T_NDX, T_QQQ };
static const int PersistentStateKey = 1;
static const double NA = -1e300;   // "no value" in the per-bar arrays

// ──────────────────────────────────────────────────────────────────────────────
//  Small helpers
// ──────────────────────────────────────────────────────────────────────────────
static std::string Trim(const std::string& s)
{
	size_t a = 0, b = s.size();
	while (a < b && static_cast<unsigned char>(s[a]) <= 32) ++a;
	while (b > a && static_cast<unsigned char>(s[b - 1]) <= 32) --b;
	return s.substr(a, b - a);
}

static bool ParseDouble(const std::string& text, double& out)
{
	std::string t = Trim(text);
	if (t.empty()) return false;
	char* end = nullptr;
	out = std::strtod(t.c_str(), &end);
	return end != t.c_str() && *end == 0 && std::isfinite(out);
}

static std::vector<std::string> Split(const std::string& s, char sep)
{
	std::vector<std::string> out;
	size_t start = 0;
	for (;;)
	{
		size_t p = s.find(sep, start);
		out.push_back(s.substr(start, p == std::string::npos ? std::string::npos : p - start));
		if (p == std::string::npos) break;
		start = p + 1;
	}
	return out;
}

static std::string Fmt(double v, bool trimZeros)
{
	char buf[64];
	std::snprintf(buf, sizeof(buf), "%.2f", v);
	std::string s = buf;
	if (trimZeros && s.find('.') != std::string::npos)
	{
		while (!s.empty() && s.back() == '0') s.pop_back();
		if (!s.empty() && s.back() == '.') s.pop_back();
	}
	return s;
}

template <typename Character>
static void ClearSecret(std::basic_string<Character>& secret)
{
	if (!secret.empty())
		SecureZeroMemory(&secret[0], secret.size() * sizeof(Character));
	secret.clear();
}

static bool ValidApiKey(const std::string& key)
{
	if (key.empty() || key.size() > 2048) return false;
	for (unsigned char c : key)
		if (c <= 32 || c >= 127) return false;
	return true;
}

static long long FloorDiv(long long a, long long b)
{
	long long q = a / b;
	if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
	return q;
}

// Sierra date serial (days since 1899-12-30, a Saturday): 0 = Sunday ... 6 = Saturday.
static int DayOfWeek(int dateSerial) { return ((dateSerial + 6) % 7 + 7) % 7; }

static COLORREF BlendColor(COLORREF fg, COLORREF bg, int transparency)
{
	transparency = (std::max)(0, (std::min)(100, transparency));
	const int o = 100 - transparency;
	return RGB((GetRValue(fg) * o + GetRValue(bg) * transparency + 50) / 100,
		(GetGValue(fg) * o + GetGValue(bg) * transparency + 50) / 100,
		(GetBValue(fg) * o + GetBValue(bg) * transparency + 50) / 100);
}

// ──────────────────────────────────────────────────────────────────────────────
//  New York clock (Alex): the Windows time zone database, DST included
// ──────────────────────────────────────────────────────────────────────────────
static bool UtcToEastern(const SYSTEMTIME& utc, SCDateTime& eastern)
{
	static const DYNAMIC_TIME_ZONE_INFORMATION zone = []()
	{
		DYNAMIC_TIME_ZONE_INFORMATION candidate = {};
		for (DWORD index = 0; EnumDynamicTimeZoneInformation(index, &candidate) == ERROR_SUCCESS; ++index)
			if (std::wcscmp(candidate.TimeZoneKeyName, L"Eastern Standard Time") == 0) return candidate;
		return DYNAMIC_TIME_ZONE_INFORMATION{};
	}();
	SYSTEMTIME local = {};
	if (zone.TimeZoneKeyName[0] == 0 || !SystemTimeToTzSpecificLocalTimeEx(&zone, &utc, &local)) return false;
	eastern.SetDateTimeYMDHMS(local.wYear, local.wMonth, local.wDay, local.wHour, local.wMinute, local.wSecond);
	return true;
}

static bool EtNow(SCDateTime& eastern)
{
	SYSTEMTIME utc = {};
	GetSystemTime(&utc);
	return UtcToEastern(utc, eastern);
}

// ──────────────────────────────────────────────────────────────────────────────
//  Publication age — port of atas/TLAdePublication.cs (3.6.0)
//  PUBLICATION SCHEDULE, ET — A COPY. Canonical: C:\TLADe\config\sessionConfig.ts.
//    ASIA 18:01 the evening before (dateId = the next trading day; none on Friday evening,
//    Sunday 18:01 opens Monday) · EUROPE 03:15 · PRE_OPEN 08:00 · RTH_OPEN 09:30 ·
//    OPENING_RANGE 10:30 · CLOSE 13:00. Holidays are not modelled: STALE is the truth then.
// ──────────────────────────────────────────────────────────────────────────────
static const char* SESSIONS[] = { "ASIA", "EUROPE", "PRE_OPEN", "RTH_OPEN", "OPENING_RANGE", "CLOSE" };
static const int GRACE_MIN = 10;

static int Rank(const std::string& session)
{
	for (int i = 0; i < 6; ++i)
		if (session == SESSIONS[i]) return i;
	return -1;
}

static bool ParseDateId(const std::string& dateId, int& serial)
{
	int y = 0, m = 0, d = 0;
	if (dateId.size() != 10 || std::sscanf(dateId.c_str(), "%4d-%2d-%2d", &y, &m, &d) != 3) return false;
	if (y < 2000 || m < 1 || m > 12 || d < 1 || d > 31) return false;
	SCDateTime dt;
	dt.SetDateTimeYMDHMS(y, m, d, 0, 0, 0);
	serial = dt.GetDate();
	return true;
}

static std::string DateIdOf(int serial)
{
	SCDateTime dt(serial, 0);
	int y = 0, m = 0, d = 0;
	dt.GetDateYMD(y, m, d);
	char buf[16];
	std::snprintf(buf, sizeof(buf), "%04d-%02d-%02d", y, m, d);
	return buf;
}

// Orderable key of a publication: day number * 8 + session rank; -1 when unknown.
static long long PubKey(const std::string& dateId, const std::string& session)
{
	int rank = Rank(session), serial = 0;
	if (rank < 0 || !ParseDateId(dateId, serial)) return -1;
	return static_cast<long long>(serial) * 8 + rank;
}

// The publication that should exist at this ET time (schedule above, plus the grace).
static void Expected(const SCDateTime& etNow, std::string& dateId, std::string& session)
{
	const int mins = etNow.GetTimeInSeconds() / 60;
	int day = etNow.GetDate();
	const int dow = DayOfWeek(day);
	if (dow == 6) { day -= 1; session = "CLOSE"; }
	else if (dow == 0)
	{
		if (mins >= 18 * 60 + 1 + GRACE_MIN) { day += 1; session = "ASIA"; }
		else { day -= 2; session = "CLOSE"; }
	}
	else if (mins >= 18 * 60 + 1 + GRACE_MIN)
	{
		if (dow == 5) session = "CLOSE";
		else { day += 1; session = "ASIA"; }
	}
	else if (mins >= 13 * 60 + GRACE_MIN) session = "CLOSE";
	else if (mins >= 10 * 60 + 30 + GRACE_MIN) session = "OPENING_RANGE";
	else if (mins >= 9 * 60 + 30 + GRACE_MIN) session = "RTH_OPEN";
	else if (mins >= 8 * 60 + GRACE_MIN) session = "PRE_OPEN";
	else if (mins >= 3 * 60 + 15 + GRACE_MIN) session = "EUROPE";
	else session = "ASIA";
	dateId = DateIdOf(day);
}

static long long ExpectedKey()
{
	SCDateTime et;
	if (!EtNow(et)) return -1;
	std::string d, s;
	Expected(et, d, s);
	return PubKey(d, s);
}

// Reads "T:2026-10-21,OPENING_RANGE,10:30;" out of the raw string: a row that starts right after
// "P:", ";" or "|" (never inside a label). Absent → the three fields come back empty.
static void ParseMarker(const std::string& raw, std::string& dateId, std::string& session, std::string& hhmm)
{
	dateId.clear(); session.clear(); hhmm.clear();
	size_t t = raw.find("T:");
	while (t != std::string::npos)
	{
		char before = t > 0 ? raw[t - 1] : '|';
		if (before == ':' || before == ';' || before == '|')
		{
			size_t end = raw.find(';', t);
			std::string body = end != std::string::npos ? raw.substr(t + 2, end - t - 2) : raw.substr(t + 2);
			std::vector<std::string> f = Split(body, ',');
			if (f.size() == 3 && PubKey(f[0], f[1]) >= 0) { dateId = f[0]; session = f[1]; hhmm = f[2]; return; }
		}
		t = raw.find("T:", t + 2);
	}
}

// STALE = live data whose publication is older than the one expected now. Free data is three days
// old by design and already wears the FREE badge; a string without marker cannot be judged.
static bool IsStale(bool delayed, const std::string& heldDate, const std::string& heldSession,
	std::string& expDate, std::string& expSession)
{
	SCDateTime et;
	if (!EtNow(et)) return false;
	Expected(et, expDate, expSession);
	if (delayed) return false;
	long long held = PubKey(heldDate, heldSession);
	if (held < 0) return false;
	return held < PubKey(expDate, expSession);
}

static std::string PubLabel(bool delayed, const std::string& heldDate, const std::string& heldSession,
	const std::string& heldHhmm, bool autoFetchLive, bool manual, bool& stale)
{
	std::string expDate, expSession;
	stale = IsStale(delayed, heldDate, heldSession, expDate, expSession);
	std::string label = heldDate.empty()
		? "levels: publication unknown (string without T: marker - copy a fresh one from the terminal)"
		: "levels: " + heldDate + " " + heldSession + " " + heldHhmm + " ET";
	if (!stale) return label;
	std::string hint = manual
		? "paste the latest string from the terminal, or clear the pasted string to let auto-fetch run"
		: autoFetchLive ? "auto-fetch is retrying"
		: "set your API key and Auto-fetch, or paste the latest string from the terminal";
	return label + " - STALE: expected " + expDate + " " + expSession + "; " + hint;
}

// Decides, once a minute, whether the cloud must be asked again (TLAdeCatchUp).
struct CatchUp
{
	ULONGLONG lastAttempt = 0;
	bool attempted = false;
	int misses = 0;
	long long freeKeyFetched = -1;

	void MarkFetched(long long expectedKey)
	{
		lastAttempt = GetTickCount64(); attempted = true;
		freeKeyFetched = expectedKey;
	}
	bool Due(bool live, long long heldKey, long long expectedKey)
	{
		if (live && heldKey >= 0)
		{
			// Live with a marker: fetch while what we hold is older than what should exist —
			// 5 minutes apart for three tries, then every 15.
			if (heldKey >= expectedKey) { misses = 0; return false; }
			ULONGLONG gap = (misses < 3 ? 5ULL : 15ULL) * 60000ULL;
			if (attempted && GetTickCount64() - lastAttempt < gap) return false;
			misses++;
			lastAttempt = GetTickCount64(); attempted = true;
			return true;
		}
		// Free data, or a string without marker: one fetch per expected publication.
		if (expectedKey == freeKeyFetched) return false;
		freeKeyFetched = expectedKey;
		lastAttempt = GetTickCount64(); attempted = true;
		return true;
	}
};

// ──────────────────────────────────────────────────────────────────────────────
//  Fetch (Alex): WinHTTP on a background thread, key wiped after use
// ──────────────────────────────────────────────────────────────────────────────
struct HttpLibrary
{
	HMODULE module = NULL;
	decltype(&WinHttpOpen) open = NULL;
	decltype(&WinHttpConnect) connect = NULL;
	decltype(&WinHttpOpenRequest) openRequest = NULL;
	decltype(&WinHttpSetTimeouts) setTimeouts = NULL;
	decltype(&WinHttpSetOption) setOption = NULL;
	decltype(&WinHttpSendRequest) sendRequest = NULL;
	decltype(&WinHttpReceiveResponse) receiveResponse = NULL;
	decltype(&WinHttpQueryHeaders) queryHeaders = NULL;
	decltype(&WinHttpReadData) readData = NULL;
	decltype(&WinHttpCloseHandle) closeHandle = NULL;

	HttpLibrary()
	{
		module = LoadLibraryExW(L"winhttp.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
		if (module == NULL) return;
		open = reinterpret_cast<decltype(open)>(GetProcAddress(module, "WinHttpOpen"));
		connect = reinterpret_cast<decltype(connect)>(GetProcAddress(module, "WinHttpConnect"));
		openRequest = reinterpret_cast<decltype(openRequest)>(GetProcAddress(module, "WinHttpOpenRequest"));
		setTimeouts = reinterpret_cast<decltype(setTimeouts)>(GetProcAddress(module, "WinHttpSetTimeouts"));
		setOption = reinterpret_cast<decltype(setOption)>(GetProcAddress(module, "WinHttpSetOption"));
		sendRequest = reinterpret_cast<decltype(sendRequest)>(GetProcAddress(module, "WinHttpSendRequest"));
		receiveResponse = reinterpret_cast<decltype(receiveResponse)>(GetProcAddress(module, "WinHttpReceiveResponse"));
		queryHeaders = reinterpret_cast<decltype(queryHeaders)>(GetProcAddress(module, "WinHttpQueryHeaders"));
		readData = reinterpret_cast<decltype(readData)>(GetProcAddress(module, "WinHttpReadData"));
		closeHandle = reinterpret_cast<decltype(closeHandle)>(GetProcAddress(module, "WinHttpCloseHandle"));
	}
	~HttpLibrary() { if (module != NULL) FreeLibrary(module); }
	HttpLibrary(const HttpLibrary&) = delete;
	HttpLibrary& operator=(const HttpLibrary&) = delete;
	bool Ready() const
	{
		return open && connect && openRequest && setTimeouts && setOption
			&& sendRequest && receiveResponse && queryHeaders && readData && closeHandle;
	}
};

struct HttpHandle
{
	const HttpLibrary& library;
	HINTERNET value;
	HttpHandle(const HttpLibrary& owner, HINTERNET handle) : library(owner), value(handle) {}
	~HttpHandle() { if (value != NULL) library.closeHandle(value); }
	HttpHandle(const HttpHandle&) = delete;
	HttpHandle& operator=(const HttpHandle&) = delete;
};

struct ApiReply
{
	std::string data;
	std::string error;
	DWORD status = 0;
	bool free = false;
};

// GET /indicatorData?ticker=SPX|NDX with the user's key; without a key, &mode=free (3-day delayed).
static ApiReply FetchIndicatorData(const std::string& key, bool nqFamily, HANDLE cancelEvent)
{
	ApiReply result;
	const bool live = !key.empty();
	result.free = !live;
	const ULONGLONG started = GetTickCount64();
	auto cancelled = [&]()
	{
		return (cancelEvent != NULL && WaitForSingleObject(cancelEvent, 0) == WAIT_OBJECT_0)
			|| GetTickCount64() - started >= 20000;
	};
	auto failed = [&]()
	{
		result.error = "network request failed (Windows error " + std::to_string(GetLastError()) + ")";
		return result;
	};
	HttpLibrary library;
	if (!library.Ready()) { result.error = "Windows WinHTTP is not available"; return result; }
	if (live && !ValidApiKey(key)) { result.error = "invalid API key (spaces or non-ASCII characters)"; return result; }
	if (cancelled()) { result.error = "request cancelled"; return result; }
	std::wstring agent = L"TLADe-SierraChart/3.6.1";
	HttpHandle session(library, library.open(agent.c_str(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
		WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
	if (session.value == NULL || !library.setTimeouts(session.value, 8000, 8000, 8000, 8000)) return failed();
	DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
	library.setOption(session.value, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols));
	HttpHandle connection(library, library.connect(session.value,
		L"europe-west1-omggex.cloudfunctions.net", INTERNET_DEFAULT_HTTPS_PORT, 0));
	if (connection.value == NULL) return failed();
	const wchar_t* path = nqFamily
		? (live ? L"/indicatorData?ticker=NDX" : L"/indicatorData?ticker=NDX&mode=free")
		: (live ? L"/indicatorData?ticker=SPX" : L"/indicatorData?ticker=SPX&mode=free");
	HttpHandle request(library, library.openRequest(connection.value, L"GET", path,
		NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE));
	if (request.value == NULL) return failed();
	DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
	library.setOption(request.value, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy));
	std::wstring headers = L"Accept: text/plain\r\n";
	if (live) headers += L"X-API-Key: " + std::wstring(key.begin(), key.end()) + L"\r\n";
	const BOOL sent = library.sendRequest(request.value, headers.c_str(), static_cast<DWORD>(headers.size()),
		WINHTTP_NO_REQUEST_DATA, 0, 0, 0);
	const DWORD sendError = sent ? ERROR_SUCCESS : GetLastError();
	ClearSecret(headers);
	if (!sent) { SetLastError(sendError); return failed(); }
	if (!library.receiveResponse(request.value, NULL)) return failed();
	DWORD statusSize = sizeof(result.status);
	if (!library.queryHeaders(request.value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
		WINHTTP_HEADER_NAME_BY_INDEX, &result.status, &statusSize, WINHTTP_NO_HEADER_INDEX))
		return failed();
	if (result.status != 200)
	{
		result.error = "HTTP " + std::to_string(result.status);
		if (result.status == 401 || result.status == 403) result.error += ": check the API key and account access";
		else if (result.status == 429) result.error += ": server rate limit, automatic fetching paused 15 minutes";
		return result;
	}
	std::string data;
	char buffer[8192];
	for (;;)
	{
		if (cancelled()) { result.error = "request cancelled or timed out"; return result; }
		DWORD count = 0;
		if (!library.readData(request.value, buffer, sizeof(buffer), &count)) return failed();
		if (count == 0) break;
		if (data.size() + count > 1024 * 1024) { result.error = "response exceeds 1 MB"; return result; }
		data.append(buffer, count);
	}
	if (data.empty() || data.find("L:") == std::string::npos)
	{
		result.error = "the response is not a TLADe level string";
		return result;
	}
	result.data = std::move(data);
	return result;
}

struct ApiTask
{
	HANDLE thread = NULL;
	HANDLE cancelEvent = NULL;
	std::string key;
	bool nqFamily = false;
	ApiReply reply;
	ApiTask() = default;
	ApiTask(const ApiTask&) = delete;
	ApiTask& operator=(const ApiTask&) = delete;
	~ApiTask()
	{
		Cancel();
		if (thread != NULL) { WaitForSingleObject(thread, INFINITE); CloseHandle(thread); }
		if (cancelEvent != NULL) CloseHandle(cancelEvent);
		ClearSecret(key);
	}
	void Cancel() { if (cancelEvent != NULL) SetEvent(cancelEvent); }
	bool Busy() const { return thread != NULL; }
	static unsigned __stdcall Run(void* parameter)
	{
		ApiTask& task = *static_cast<ApiTask*>(parameter);
		try { task.reply = FetchIndicatorData(task.key, task.nqFamily, task.cancelEvent); }
		catch (...) { task.reply = ApiReply(); task.reply.error = "the request could not be completed"; }
		ClearSecret(task.key);
		return 0;
	}
	bool Start(const std::string& apiKey, bool isNq)
	{
		if (Busy()) return false;
		key = apiKey;
		nqFamily = isNq;
		reply = ApiReply();
		cancelEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
		if (cancelEvent != NULL)
			thread = reinterpret_cast<HANDLE>(_beginthreadex(NULL, 0, Run, this, 0, NULL));
		if (thread != NULL) return true;
		if (cancelEvent != NULL) CloseHandle(cancelEvent);
		cancelEvent = NULL;
		ClearSecret(key);
		return false;
	}
	bool Take(ApiReply& completed)
	{
		if (thread == NULL || WaitForSingleObject(thread, 0) != WAIT_OBJECT_0) return false;
		CloseHandle(thread); CloseHandle(cancelEvent);
		thread = NULL; cancelEvent = NULL;
		completed = std::move(reply);
		return true;
	}
};

// ──────────────────────────────────────────────────────────────────────────────
//  State
// ──────────────────────────────────────────────────────────────────────────────
struct LevelEntry
{
	bool isLocal = false;     // computed from the chart's bars (structure, breakouts), not from the feed
	bool boHigherTf = false;  // M/W/D breakout: solid, heavier
	int boWidth = 1;
	double rawStrike = 0;     // futures-space strike from the cloud
	std::string type;
	std::string label;
	double magnitude = 0;
	int gexSign = 0;          // +1 positive GEX (tick right), -1 negative (tick left), 0 unknown
};

struct ProfileEntry { double rawStrike = 0, value = 0; int sign = 0; };
struct DBar { int day = 0; double o = 0, h = 0, l = 0, c = 0; };
struct AlertLevel { double price = 0; std::string name; int kind = 0; };  // 0 wall, 1 system, 2 bo

// Session AVWAP sums AFTER a bar: the next bar starts from these, so a live tick on the forming
// bar recomputes it from the previous bar's sums and never counts itself twice.
struct AvwapSums
{
	double aPV = 0, aV = 0, ePV = 0, eV = 0, uPV = 0, uV = 0, pdPV = 0, pdV = 0;
	int futuresDay = INT_MIN;
};

struct H1Bar { long long hb = 0; double o = 0, h = 0, l = 0, c = 0; int first = 0, last = 0; };
struct Worldline
{
	int dir = 0;              // 1 bull, -1 bear
	int anchorBar = 0;        // chart bar the AVWAP starts from
	long long anchorH1 = 0;   // H1 bucket of the anchor (identity)
	long long birthHb = 0;    // displacement H1 bucket
	int intensity = 2;        // 3 STRONG, 2 REGULAR
	bool alive = true;
	int deathBar = -1;
	double pvBase = 0, vBase = 0;   // Σ up to the bar before the anchor
};

struct StudyState
{
	// data
	std::string rawData, manualData, pubDate, pubSession, pubHhmm;
	bool isDelayed = false;
	bool fetchedFree = false;   // the mode of the last fetched string, restored when a pasted one is cleared
	double esSpread = 0, nqSpread = 0, ratio = 0;
	std::vector<LevelEntry> levels;
	std::vector<ProfileEntry> profile;

	// fetch
	ApiTask task;
	std::string key;
	bool keyNq = false;
	bool mounted = false;
	bool authBlocked = false;
	ULONGLONG lastCheck = 0, pausedUntil = 0;
	std::string fetchError, lastLog;
	CatchUp catchUp;

	// per-bar
	std::vector<SCDateTime> et;
	std::vector<AvwapSums> sums;
	std::vector<double> avAsia, avEU, avUS, avPD;
	std::vector<double> cumPV, cumV;

	// worldlines
	std::vector<H1Bar> hours;
	size_t hoursClosed = 0;
	int wlCommitted = 0;
	double wlEmRange = NA;
	std::vector<Worldline> worldlines;

	// local levels and flips
	std::vector<LevelEntry> localBos, localPA;
	std::string bosKey;
	int paBars = -1;
	std::map<double, bool> wallFlipped;
	std::string flipsKey;

	// alerts and drawing
	std::vector<AlertLevel> alertLevels;
	int alertLastBar = -1;
	bool staleDrawn = false;

	~StudyState() { ClearSecret(key); }
};

// ──────────────────────────────────────────────────────────────────────────────
//  Ticker and price conversion (ATAS ResolveTicker / ConvertPrice / ChartToRaw)
// ──────────────────────────────────────────────────────────────────────────────
static std::string Upper(const char* s)
{
	std::string u = s ? s : "";
	for (char& c : u) c = static_cast<char>(toupper(static_cast<unsigned char>(c)));
	return u;
}

static int ResolveTicker(SCStudyInterfaceRef sc)
{
	int t = sc.Input[InTicker].GetIndex();
	if (t != T_AUTO) return t;
	std::string inst = Upper(sc.Symbol.GetChars());
	if (inst.find("NQ") != std::string::npos || inst.find("NDX") != std::string::npos || inst.find("MNQ") != std::string::npos) return T_NQ;
	return T_ES;
}

static bool IsNqFamily(SCStudyInterfaceRef sc)
{
	int t = ResolveTicker(sc);
	return t == T_NQ || t == T_NDX || t == T_QQQ;
}

static double EffEs(SCStudyInterfaceRef sc, const StudyState& s)
{
	double m = sc.Input[InManualSpread].GetFloat();
	return m > 0 ? m : s.esSpread;
}
static double EffNq(SCStudyInterfaceRef sc, const StudyState& s)
{
	double m = sc.Input[InManualSpread].GetFloat();
	return m > 0 ? m : s.nqSpread;
}

static double SnapPrice(SCStudyInterfaceRef sc, double price)
{
	if (!sc.Input[InSnapToTick].GetYesNo()) return price;
	double tick = sc.TickSize;
	if (tick <= 0) return price;
	double q = price / tick;
	return (q >= 0 ? std::floor(q + 0.5) : std::ceil(q - 0.5)) * tick;   // away from zero at .5
}

// Strikes arrive in FUTURES points (ES / NQ). On a cash or ETF chart the spread from the data string is
// taken off; with no spread known the strikes stay as they are. Ratio from R:, per family: NDX→QQQ
// (else 40) on a QQQ chart, SPX→SPY inside 9-12 (else 10) on a SPY one.
static double ConvertPrice(SCStudyInterfaceRef sc, const StudyState& s, double raw)
{
	double es = EffEs(sc, s), nq = EffNq(sc, s), cv = raw;
	double ratio = s.ratio > 0 ? s.ratio : 40.0;
	double spyRatio = (s.ratio >= 9.0 && s.ratio <= 12.0) ? s.ratio : 10.0;
	switch (ResolveTicker(sc))
	{
		case T_SPX: cv = es <= 0 ? raw : raw - es; break;
		case T_SPY: cv = es <= 0 ? raw : (raw - es) / spyRatio; break;
		case T_NDX: cv = nq <= 0 ? raw : raw - nq; break;
		case T_QQQ: cv = nq <= 0 ? raw : (raw - nq) / ratio; break;
		default: cv = raw; break;
	}
	return SnapPrice(sc, cv);
}

static double ChartToRaw(SCStudyInterfaceRef sc, const StudyState& s, double chartPrice)
{
	double es = EffEs(sc, s), nq = EffNq(sc, s);
	double ratio = s.ratio > 0 ? s.ratio : 40.0;
	double spyRatio = (s.ratio >= 9.0 && s.ratio <= 12.0) ? s.ratio : 10.0;
	switch (ResolveTicker(sc))
	{
		case T_SPX: return es <= 0 ? chartPrice : chartPrice + es;
		case T_SPY: return es <= 0 ? chartPrice : chartPrice * spyRatio + es;
		case T_NDX: return nq <= 0 ? chartPrice : chartPrice + nq;
		case T_QQQ: return nq <= 0 ? chartPrice : chartPrice * ratio + nq;
		default: return chartPrice;
	}
}

static std::string FormatPrice(SCStudyInterfaceRef sc, const StudyState& s, double raw)
{
	int t = ResolveTicker(sc);
	return Fmt(ConvertPrice(sc, s, raw), t == T_SPY || t == T_QQQ);
}

static bool IsGex(const std::string& t) { return t == "CW" || t == "PW" || t == "GL"; }
static bool IsSystem(const std::string& t)
{
	return t == "ZG" || t == "MP" || t == "EH" || t == "EL" || t == "EHR" || t == "ELR" || t == "VH" || t == "VL";
}
static bool IsBreakout(const std::string& t) { return t == "BL" || t == "BS"; }
static bool IsStructure(const std::string& t) { return t == "PDH" || t == "PDL" || t == "PWH" || t == "PWL"; }

// One name per level code, the same words on every platform. Labels read "price + name".
static std::string LevelName(const std::string& code, const std::string& feedLabel)
{
	static const std::map<std::string, std::string> names = {
		{"CW", "Call Wall"}, {"PW", "Put Wall"}, {"GL", "GEX Level"}, {"ZG", "Zero Gamma"},
		{"MP", "Max Pain"}, {"EH", "EM High Globex"}, {"EL", "EM Low Globex"}, {"EHR", "EM High RTH"},
		{"ELR", "EM Low RTH"}, {"VH", "Vol High"}, {"VL", "Vol Low"}, {"CM", "Charm Magnet"},
		{"DF", "Delta Flip"}, {"PDH", "Previous Day High"}, {"PDL", "Previous Day Low"},
		{"PWH", "Previous Week High"}, {"PWL", "Previous Week Low"} };
	auto it = names.find(code);
	return it != names.end() ? it->second : (feedLabel.empty() ? code : feedLabel);
}

// ──────────────────────────────────────────────────────────────────────────────
//  Parse (ATAS ParseData)
// ──────────────────────────────────────────────────────────────────────────────
static void ParseData(SCStudyInterfaceRef sc, StudyState& s, std::string raw)
{
	raw.erase(std::remove(raw.begin(), raw.end(), '\r'), raw.end());
	raw.erase(std::remove(raw.begin(), raw.end(), '\n'), raw.end());
	raw = Trim(raw);
	if (raw.empty()) { s.levels.clear(); s.profile.clear(); s.pubDate.clear(); s.pubSession.clear(); s.pubHhmm.clear(); return; }
	ParseMarker(raw, s.pubDate, s.pubSession, s.pubHhmm);
	const bool nq = IsNqFamily(sc);
	if (raw.compare(0, 2, "S:") == 0)
	{
		size_t e = raw.find('|');
		if (e != std::string::npos && e > 2)
		{
			double sp = 0;
			if (ParseDouble(raw.substr(2, e - 2), sp) && sp > 0) { if (nq) s.nqSpread = sp; else s.esSpread = sp; }
			raw = raw.substr(e + 1);
		}
	}
	// R: index→ETF ratio — NDX→QQQ on an NQ string, SPX→SPY on an ES one
	if (raw.compare(0, 2, "R:") == 0)
	{
		size_t e = raw.find('|');
		if (e != std::string::npos && e > 2)
		{
			double rr = 0;
			if (ParseDouble(raw.substr(2, e - 2), rr) && rr > 0) s.ratio = rr;
			raw = raw.substr(e + 1);
		}
	}
	std::string levelsData, profileData;
	size_t pPos = raw.find("|P:");
	if (pPos != std::string::npos)
	{
		std::string before = raw.substr(0, pPos);
		if (before.compare(0, 2, "L:") == 0) levelsData = before.substr(2);
		profileData = raw.substr(pPos + 3);
	}
	else if (raw.compare(0, 2, "L:") == 0) levelsData = raw.substr(2);

	// P: = GEX profile per strike -> sign map (+1/-1); rows whose first field is not a number
	// (the T: marker, the B: magnet row) are skipped.
	std::map<long long, int> signMap;
	std::vector<ProfileEntry> freshProfile;
	for (const std::string& pair : Split(profileData, ';'))
	{
		if (pair.empty()) continue;
		std::vector<std::string> p = Split(pair, ',');
		double strike = 0, val = 0;
		if (p.size() < 2 || !ParseDouble(p[0], strike)) continue;
		ParseDouble(p[1], val);
		int sign = 0;
		double sv = 0;
		if (p.size() >= 3 && ParseDouble(p[2], sv)) sign = sv > 0 ? 1 : sv < 0 ? -1 : 0;
		else sign = val >= 0 ? 1 : -1;
		signMap[std::llround(strike)] = sign;
		ProfileEntry pe; pe.rawStrike = strike; pe.value = val; pe.sign = sign;
		freshProfile.push_back(pe);
	}
	s.profile = freshProfile;

	std::vector<LevelEntry> fresh;
	for (const std::string& pair : Split(levelsData, ';'))
	{
		if (pair.empty()) continue;
		std::vector<std::string> parts = Split(pair, ',');
		double strike = 0, mag = 0;
		if (parts.size() < 3 || !ParseDouble(parts[0], strike)) continue;
		if (parts.size() >= 5) ParseDouble(parts[4], mag);
		LevelEntry l;
		l.rawStrike = strike;
		l.type = Trim(parts[1]);
		l.label = Trim(parts[2]);
		l.magnitude = std::fabs(mag);
		auto it = signMap.find(std::llround(strike));
		l.gexSign = it != signMap.end() ? it->second : 0;
		fresh.push_back(l);
	}
	if (fresh.empty()) return;   // input incomplete/invalid — keep the existing levels
	s.levels = fresh;
}

// ──────────────────────────────────────────────────────────────────────────────
//  Fetch lifecycle (ATAS RunScheduler, Alex's status and error handling)
// ──────────────────────────────────────────────────────────────────────────────
static void Log(SCStudyInterfaceRef sc, StudyState& s, const std::string& msg)
{
	if (s.lastLog == msg) return;
	s.lastLog = msg;
	SCString t;
	t.Format("TLADe GEX: %s", msg.c_str());
	sc.AddMessageToLog(t, 0);   // 0: never pop the Message Log window open
}

static void StartFetch(SCStudyInterfaceRef sc, StudyState& s, const char* why)
{
	if (s.task.Busy()) return;
	if (!s.key.empty() && s.authBlocked) return;
	if (GetTickCount64() < s.pausedUntil) return;
	if (s.task.Start(s.key, IsNqFamily(sc)))
		Log(sc, s, std::string(why) + ": requesting " + (IsNqFamily(sc) ? "NDX" : "SPX") + (s.key.empty() ? " (FREE, delayed)" : " (LIVE)"));
	else
		s.fetchError = "unable to start the background fetch";
}

static void UpdateFetch(SCStudyInterfaceRef sc, StudyState& s, bool& changed)
{
	// the key and the family come from the inputs; a change starts again from a mount fetch
	std::string key = Trim(sc.Input[InApiKey].GetString() ? sc.Input[InApiKey].GetString() : "");
	bool nq = IsNqFamily(sc);
	if (key != s.key || nq != s.keyNq)
	{
		s.task.Cancel();
		ClearSecret(s.key);
		s.key = key;
		s.keyNq = nq;
		s.mounted = false;
		s.authBlocked = false;
		s.fetchError.clear();
		s.catchUp = CatchUp();
	}
	ClearSecret(key);

	ApiReply reply;
	if (s.task.Take(reply))
	{
		if (!reply.data.empty())
		{
			s.fetchedFree = reply.free;
			if (s.manualData.empty()) s.isDelayed = reply.free;
			s.rawData = reply.data;
			s.fetchError.clear();
			if (s.manualData.empty()) { ParseData(sc, s, s.rawData); changed = true; }
			Log(sc, s, "fetched " + std::to_string(reply.data.size()) + " bytes, " + (s.pubDate.empty() ? std::string("no T: marker") : s.pubDate + " " + s.pubSession));
		}
		else
		{
			s.fetchError = reply.error;
			if (reply.status == 401 || reply.status == 403) s.authBlocked = true;
			if (reply.status == 429) s.pausedUntil = GetTickCount64() + 900000;
			Log(sc, s, reply.error + " (previous levels kept)");
		}
	}

	// A pasted string owns the chart; fetching behind it would only make the levels flip.
	std::string manual = Trim(sc.TextInput.GetChars() ? sc.TextInput.GetChars() : "");
	if (manual != s.manualData)
	{
		s.manualData = manual;
		ParseData(sc, s, manual.empty() ? s.rawData : manual);
		s.isDelayed = manual.empty() ? s.fetchedFree : false;
		changed = true;
	}

	const bool refresh = sc.Input[InRefreshNow].GetYesNo() != 0;
	if (refresh) { sc.Input[InRefreshNow].SetYesNo(0); s.authBlocked = false; s.pausedUntil = 0; }
	if (!sc.Input[InAutoFetch].GetYesNo() && !refresh) return;
	if (!s.manualData.empty() && !refresh) return;
	// Replay: the levels loaded stay, nothing new is fetched (they would be from after the replayed time).
	if (sc.ReplayStatus != REPLAY_STOPPED) return;
	if (refresh) { StartFetch(sc, s, "refresh now"); return; }
	if (!s.mounted)
	{
		s.mounted = true;
		StartFetch(sc, s, "mount");
		s.catchUp.MarkFetched(ExpectedKey());
		s.lastCheck = GetTickCount64();
		return;
	}
	if (GetTickCount64() - s.lastCheck < 60000) return;
	s.lastCheck = GetTickCount64();
	long long expected = ExpectedKey();
	long long held = PubKey(s.pubDate, s.pubSession);
	if (expected >= 0 && s.catchUp.Due(!s.key.empty(), held, expected))
		StartFetch(sc, s, "catch-up");
}

// ──────────────────────────────────────────────────────────────────────────────
//  Session AVWAP (ATAS ComputeAvwapForBar, the rule of 17/10/2026)
//  An AVWAP has two parameters only: where it anchors and when it re-anchors. The four lines are
//  one function with four anchors, all re-anchored on the futures day (18:00 ET):
//    Asia 18:00 ET · EU 03:00 ET · US 09:30 ET of the current futures day
//    PD   09:30 ET of the PREVIOUS futures day, running on to the live bar
// ──────────────────────────────────────────────────────────────────────────────
static int FuturesDay(const SCDateTime& et) { return et.GetDate() + (et.GetHour() >= 18 ? 1 : 0); }
static int EtMins(const SCDateTime& et) { return et.GetTimeInSeconds() / 60; }
static long long EtEpochMin(const SCDateTime& et) { return static_cast<long long>(et.GetDate()) * 1440 + EtMins(et); }

static void ComputeBars(SCStudyInterfaceRef sc, StudyState& s, int start)
{
	const int n = sc.ArraySize;
	s.et.resize(n); s.sums.resize(n);
	s.avAsia.resize(n, NA); s.avEU.resize(n, NA); s.avUS.resize(n, NA); s.avPD.resize(n, NA);
	s.cumPV.resize(n, 0); s.cumV.resize(n, 0);
	for (int i = start; i < n; ++i)
	{
		s.et[i] = sc.ConvertDateTimeFromChartTimeZone(sc.BaseDateTimeIn[i], TIMEZONE_NEW_YORK);
		const double h = sc.BaseDataIn[SC_HIGH][i], l = sc.BaseDataIn[SC_LOW][i], c = sc.BaseDataIn[SC_LAST][i];
		const double hlc3 = (h + l + c) / 3.0;
		// A zero-volume bar weighs 1, not 0 (vol = b.volume > 0 ? b.volume : 1 in the SSOT).
		const double vol = sc.BaseDataIn[SC_VOLUME][i] > 0 ? sc.BaseDataIn[SC_VOLUME][i] : 1.0;
		s.cumPV[i] = (i > 0 ? s.cumPV[i - 1] : 0) + hlc3 * vol;
		s.cumV[i] = (i > 0 ? s.cumV[i - 1] : 0) + vol;

		AvwapSums a = i > 0 ? s.sums[i - 1] : AvwapSums();
		const int fd = FuturesDay(s.et[i]);
		const int mins = EtMins(s.et[i]);
		if (fd != a.futuresDay)
		{
			// PD = the same 09:30 anchor one futures day back, kept alive — only when the day that
			// ends actually had an RTH session (a Saturday or holiday rollover has none).
			if (a.uV > 0) { a.pdPV = a.uPV; a.pdV = a.uV; }
			a.aPV = a.aV = a.ePV = a.eV = a.uPV = a.uV = 0;
			a.futuresDay = fd;
		}
		a.aPV += hlc3 * vol; a.aV += vol;
		if (mins >= 180 && mins < 1080) { a.ePV += hlc3 * vol; a.eV += vol; }
		if (mins >= 570 && mins < 1080) { a.uPV += hlc3 * vol; a.uV += vol; }
		if (a.pdV > 0) { a.pdPV += hlc3 * vol; a.pdV += vol; }
		s.sums[i] = a;
		s.avAsia[i] = a.aV > 0 ? a.aPV / a.aV : NA;
		s.avEU[i] = a.eV > 0 ? a.ePV / a.eV : NA;
		s.avUS[i] = a.uV > 0 ? a.uPV / a.uV : NA;
		s.avPD[i] = a.pdV > 0 ? a.pdPV / a.pdV : NA;
	}
}

// ──────────────────────────────────────────────────────────────────────────────
//  Bars aggregated by ET (ATAS AggregateByEtPeriod / AggregateByFuturesKey)
// ──────────────────────────────────────────────────────────────────────────────
static double O(SCStudyInterfaceRef sc, int i) { return sc.BaseDataIn[SC_OPEN][i]; }
static double Hh(SCStudyInterfaceRef sc, int i) { return sc.BaseDataIn[SC_HIGH][i]; }
static double Ll(SCStudyInterfaceRef sc, int i) { return sc.BaseDataIn[SC_LOW][i]; }
static double Cc(SCStudyInterfaceRef sc, int i) { return sc.BaseDataIn[SC_LAST][i]; }

// Period buckets (minutes) anchored at 18:00 ET (Globex open) = clientEngineService.aggregateCandles.
static std::vector<DBar> AggregateByEtPeriod(SCStudyInterfaceRef sc, const StudyState& s, int barCount, int period)
{
	std::vector<DBar> out;
	bool has = false; long long curSlot = 0; DBar cur;
	const long long anchor = 18 * 60;
	for (int i = 0; i < barCount; ++i)
	{
		long long etMin = EtEpochMin(s.et[i]);
		long long slot = FloorDiv(etMin - anchor, period) * period + anchor;
		if (!has || slot != curSlot)
		{
			if (has) out.push_back(cur);
			has = true; curSlot = slot;
			cur.day = s.et[i].GetDate(); cur.o = O(sc, i); cur.h = Hh(sc, i); cur.l = Ll(sc, i); cur.c = Cc(sc, i);
		}
		else { cur.h = (std::max)(cur.h, Hh(sc, i)); cur.l = (std::min)(cur.l, Ll(sc, i)); cur.c = Cc(sc, i); }
	}
	if (has) out.push_back(cur);
	return out;
}

// One bar per calendar key of the FUTURES day (week = its Monday, month = its 1st).
static std::vector<DBar> AggregateByFuturesKey(SCStudyInterfaceRef sc, const StudyState& s, int barCount, bool monthly)
{
	std::vector<DBar> out;
	bool has = false; int curKey = 0; DBar cur;
	for (int i = 0; i < barCount; ++i)
	{
		int fd = FuturesDay(s.et[i]);
		int key;
		if (monthly) { SCDateTime d(fd, 0); key = d.GetYear() * 12 + d.GetMonth(); }
		else key = fd - (DayOfWeek(fd) + 6) % 7;
		if (!has || key != curKey)
		{
			if (has) out.push_back(cur);
			has = true; curKey = key;
			cur.day = fd; cur.o = O(sc, i); cur.h = Hh(sc, i); cur.l = Ll(sc, i); cur.c = Cc(sc, i);
		}
		else { cur.h = (std::max)(cur.h, Hh(sc, i)); cur.l = (std::min)(cur.l, Ll(sc, i)); cur.c = Cc(sc, i); }
	}
	if (has) out.push_back(cur);
	return out;
}

// ──────────────────────────────────────────────────────────────────────────────
//  Breakouts (ATAS DetectBosInto = clientEngineService.detectBOS)
//  CLOSED bars only (the last element is the bar in progress). A breakout dies when a later bar of
//  the SAME timeframe, in the opposite direction, closes back through its level. Of the survivors,
//  the last N per timeframe are kept.
// ──────────────────────────────────────────────────────────────────────────────
static bool IsQualityBreakout(const DBar& curr, const DBar& prev, bool bull)
{
	if (bull)
	{
		if (curr.c <= prev.h) return false;
		double bodyAbove = curr.c - prev.h, upperShadow = curr.h - curr.c;
		return bodyAbove > upperShadow && bodyAbove > 0;
	}
	if (curr.c >= prev.l) return false;
	double bodyBelow = prev.l - curr.c, lowerShadow = curr.c - curr.l;
	return bodyBelow > lowerShadow && bodyBelow > 0;
}

// Triangles by timeframe (▲ H1, ▲▲ H4, ▲▲▲ D, ▲▲▲▲ W, ▲▲▲▲▲ M), then "BO L/S <tf>".
static std::string BoLabel(bool bull, const std::string& tf)
{
	const char* t = bull ? "\xE2\x96\xB2" : "\xE2\x96\xBC";
	int n = tf == "M" ? 5 : tf == "W" ? 4 : tf == "D" ? 3 : tf == "H4" ? 2 : 1;
	std::string out;
	for (int i = 0; i < n; ++i) out += t;
	return out + " BO " + (bull ? "L" : "S") + " " + tf;
}

static LevelEntry MakeBos(SCStudyInterfaceRef sc, const StudyState& s, double chartPrice, bool bull, const std::string& tf)
{
	LevelEntry e;
	e.rawStrike = ChartToRaw(sc, s, chartPrice);
	e.type = bull ? "BL" : "BS";
	e.label = BoLabel(bull, tf);
	e.isLocal = true;
	e.boHigherTf = tf == "M" || tf == "W" || tf == "D";
	int base = (std::max)(1, sc.Input[InLineWidthGex].GetInt());
	e.boWidth = tf == "M" ? base + 2 : tf == "W" ? base + 1 : base;
	return e;
}

static void DetectBosInto(SCStudyInterfaceRef sc, const StudyState& s, const std::vector<DBar>& c, const std::string& tf, std::vector<LevelEntry>& out)
{
	int lastClosed = static_cast<int>(c.size()) - 2;
	if (lastClosed < 1) return;
	std::vector<LevelEntry> alive;
	for (int i = 1; i <= lastClosed; ++i)
	{
		const DBar& curr = c[i]; const DBar& prev = c[i - 1];
		if (IsQualityBreakout(curr, prev, true))
		{
			bool killed = false;
			for (int j = i + 1; j <= lastClosed; ++j) if (c[j].c < prev.h && c[j].c < c[j].o) { killed = true; break; }
			if (!killed) alive.push_back(MakeBos(sc, s, prev.h, true, tf));
		}
		if (IsQualityBreakout(curr, prev, false))
		{
			bool killed = false;
			for (int j = i + 1; j <= lastClosed; ++j) if (c[j].c > prev.l && c[j].c > c[j].o) { killed = true; break; }
			if (!killed) alive.push_back(MakeBos(sc, s, prev.l, false, tf));
		}
	}
	int keep = (std::max)(1, sc.Input[InBosKeep].GetInt());
	for (int i = (std::max)(0, static_cast<int>(alive.size()) - keep); i < static_cast<int>(alive.size()); ++i) out.push_back(alive[i]);
}

static void ComputeLocalBos(SCStudyInterfaceRef sc, StudyState& s, int barCount)
{
	char key[128];
	std::snprintf(key, sizeof(key), "%d|%d%d%d%d%d|%d|%.4f", barCount, sc.Input[InBosM].GetYesNo(), sc.Input[InBosW].GetYesNo(),
		sc.Input[InBosD].GetYesNo(), sc.Input[InBosH4].GetYesNo(), sc.Input[InBosH1].GetYesNo(), sc.Input[InBosKeep].GetInt(), EffEs(sc, s) + EffNq(sc, s) + s.ratio);
	if (s.bosKey == key) return;
	s.bosKey = key;
	s.localBos.clear();
	if (sc.Input[InBosM].GetYesNo()) DetectBosInto(sc, s, AggregateByFuturesKey(sc, s, barCount, true), "M", s.localBos);
	if (sc.Input[InBosW].GetYesNo()) DetectBosInto(sc, s, AggregateByFuturesKey(sc, s, barCount, false), "W", s.localBos);
	if (sc.Input[InBosD].GetYesNo()) DetectBosInto(sc, s, AggregateByEtPeriod(sc, s, barCount, 1440), "D", s.localBos);
	if (sc.Input[InBosH4].GetYesNo()) DetectBosInto(sc, s, AggregateByEtPeriod(sc, s, barCount, 240), "H4", s.localBos);
	if (sc.Input[InBosH1].GetYesNo()) DetectBosInto(sc, s, AggregateByEtPeriod(sc, s, barCount, 60), "H1", s.localBos);
}

// Local Structure: PDH/PDL = prior futures-day (18:00 ET) high/low; PWH/PWL = prior week (Monday-
// anchored) high/low, from the chart's own bars (ATAS ComputeLocalPA).
static void ComputeLocalPA(SCStudyInterfaceRef sc, StudyState& s, int barCount)
{
	if (barCount == s.paBars) return;
	s.paBars = barCount;
	s.localPA.clear();
	if (barCount < 2) return;
	std::map<int, std::pair<double, double>> day;
	for (int i = 0; i < barCount; ++i)
	{
		int fd = FuturesDay(s.et[i]);
		auto it = day.find(fd);
		if (it == day.end()) day[fd] = std::make_pair(Hh(sc, i), Ll(sc, i));
		else { it->second.first = (std::max)(it->second.first, Hh(sc, i)); it->second.second = (std::min)(it->second.second, Ll(sc, i)); }
	}
	auto make = [&](double price, const char* type)
	{
		LevelEntry e; e.rawStrike = ChartToRaw(sc, s, price); e.type = type; e.label = type; e.isLocal = true;
		s.localPA.push_back(e);
	};
	if (day.size() >= 2)
	{
		auto pd = std::prev(day.end(), 2);
		make(pd->second.first, "PDH"); make(pd->second.second, "PDL");
	}
	std::map<int, std::pair<double, double>> week;
	for (const auto& d : day)
	{
		int wk = d.first - (DayOfWeek(d.first) + 6) % 7;   // Monday of that week
		auto it = week.find(wk);
		if (it == week.end()) week[wk] = d.second;
		else { it->second.first = (std::max)(it->second.first, d.second.first); it->second.second = (std::min)(it->second.second, d.second.second); }
	}
	if (week.size() >= 2)
	{
		auto pw = std::prev(week.end(), 2);
		make(pw->second.first, "PWH"); make(pw->second.second, "PWL");
	}
}

// ──────────────────────────────────────────────────────────────────────────────
//  Wall flip, the TradingView rule (ATAS ComputeWallFlips): two consecutive 5-minute closes beyond
//  the strike flip the wall, two the other way restore it. Charts under 5 minutes are aggregated to
//  5 minutes; above, each bar close counts as one close. Closed bars only.
// ──────────────────────────────────────────────────────────────────────────────
static void ComputeWallFlips(SCStudyInterfaceRef sc, StudyState& s, int barCount)
{
	std::string key = std::to_string(barCount);
	std::vector<const LevelEntry*> walls;
	for (const LevelEntry& l : s.levels)
		if (l.type == "CW" || l.type == "PW") { walls.push_back(&l); key += ";" + l.type + Fmt(l.rawStrike, true); }
	if (key == s.flipsKey) return;
	s.flipsKey = key;
	s.wallFlipped.clear();
	if (walls.empty() || barCount < 3) return;
	int tf = sc.SecondsPerBar / 60;
	std::vector<DBar> bars;
	bool agg = tf > 0 && tf < 5;
	if (agg) bars = AggregateByEtPeriod(sc, s, barCount, 5);
	const size_t n = walls.size();
	std::vector<int> cnt(n, 0);
	std::vector<char> flipped(n, 0);
	int last = (agg ? static_cast<int>(bars.size()) : barCount) - 2;   // the last bar is still forming
	for (int i = (std::max)(0, last - 3000); i <= last; ++i)
	{
		double close = agg ? bars[i].c : Cc(sc, i);
		if (close <= 0) continue;
		double c = ChartToRaw(sc, s, close);
		for (size_t w = 0; w < n; ++w)
		{
			double k = walls[w]->rawStrike;
			bool cw = walls[w]->type == "CW";
			bool beyond = cw ? (flipped[w] ? c < k : c > k) : (flipped[w] ? c > k : c < k);
			if (beyond) { if (++cnt[w] >= 2) { flipped[w] = !flipped[w]; cnt[w] = 0; } }
			else cnt[w] = 0;
		}
	}
	for (size_t w = 0; w < n; ++w) s.wallFlipped[walls[w]->rawStrike] = flipped[w] != 0;
}

// ──────────────────────────────────────────────────────────────────────────────
//  Worldlines H1 — port of the TradingView block (3.6.1). A COPY of the terminal's rule.
//  CANONICAL: terminal/services/clientEngineService.ts (detectDisplacements, findSwingAnchor,
//  refineAnchorTo5m, trackWorldlines) and the chart filter in terminal/components/ChartSection.tsx.
//    H1 = clock-hour buckets of the chart bars. A DISPLACEMENT is a closed H1 that closes through the
//    previous H1's high (bull) / low (bear) with the body doing more than the wick, and whose NEXT
//    closed H1 leaves a gap (FVG: next.low > prev.high / next.high < prev.low). STRONG when the body
//    ≥ EM × √(60/390) (EM = EH − EL of the data string), else REGULAR.
//    ANCHOR = the latest 5-bar swing pivot within 5 H1 bars before the displacement (the displacement
//    bar itself if none), refined to the chart bar of that hour with the lowest low / highest high.
//    One worldline per (direction, anchor hour); a stronger re-test upgrades the intensity.
//    LINE = AVWAP from the anchor bar (zero volume weighs 1).
//    DEATH = at an H1 close, once the displacement's own hour has closed: bull dies when close <
//    AVWAP, bear when close > AVWAP; the line freezes there. Kept: all alive + the 4 latest dead.
//  Sierra differences, both on the side of the closed bar: a death is judged when the hour's last bar
//  has closed (TradingView also shows it tentatively on the forming bar), and there is no 4900-bar
//  history cap (a TradingView limit, not part of the rule).
// ──────────────────────────────────────────────────────────────────────────────
static bool WlActive(SCStudyInterfaceRef sc, bool tfActive)
{
	const int sec = sc.SecondsPerBar;
	return sc.Input[InShowWorldlines].GetYesNo() && tfActive && sec > 0 && sec <= 3600 && 3600 % sec == 0;
}

static double WlEmRange(SCStudyInterfaceRef sc, const StudyState& s)
{
	double eh = NA, el = NA;
	for (const LevelEntry& l : s.levels)
	{
		if (l.type == "EH") eh = ConvertPrice(sc, s, l.rawStrike);
		else if (l.type == "EL") el = ConvertPrice(sc, s, l.rawStrike);
	}
	return eh != NA && el != NA && eh > el ? eh - el : NA;
}

static double AvwapFrom(const StudyState& s, const Worldline& w, int bar)
{
	double v = s.cumV[bar] - w.vBase;
	return v > 0 ? (s.cumPV[bar] - w.pvBase) / v : NA;
}

static void CloseHour(SCStudyInterfaceRef sc, StudyState& s, size_t k)
{
	const H1Bar& hk = s.hours[k];
	// death check at the hour's close, once the displacement's own hour has closed
	for (Worldline& w : s.worldlines)
	{
		if (!w.alive || hk.hb <= w.birthHb) continue;
		double av = AvwapFrom(s, w, hk.last);
		if (av == NA) continue;
		if ((w.dir == 1 && hk.c < av) || (w.dir == -1 && hk.c > av)) { w.alive = false; w.deathBar = hk.last; }
	}
	// a displacement confirmed by the hour just closed
	if (k >= 2)
	{
		const size_t i = k - 1, ip = k - 2, nx = k;
		const H1Bar& c = s.hours[i]; const H1Bar& p = s.hours[ip]; const H1Bar& x = s.hours[nx];
		int dir = 0;
		if (c.c > p.h && (c.c - p.h) > (c.h - c.c)) dir = 1;
		else if (c.c < p.l && (p.l - c.c) > (c.c - c.l)) dir = -1;
		bool fvg = dir == 1 ? x.l > p.h : dir == -1 ? x.h < p.l : false;
		if (dir != 0 && fvg)
		{
			double body = std::fabs(c.c - c.o);
			double gvi = s.wlEmRange == NA ? NA : s.wlEmRange * std::sqrt(60.0 / 390.0);
			int intensity = gvi != NA && body >= gvi ? 3 : 2;
			// swing anchor: latest 5-bar pivot at or before i, within 5 H1 bars
			int anchorJ = -1;
			int minJ = (std::max)(2, static_cast<int>(i) - 5);
			for (int j = static_cast<int>(i); j >= minJ && anchorJ < 0; --j)
			{
				bool pivot = true;
				for (int kk = -2; kk <= 2; ++kk)
				{
					int q = j + kk;
					if (kk == 0 || q < 0 || q > static_cast<int>(i)) continue;
					if (dir == 1 && s.hours[j].l > s.hours[q].l) pivot = false;
					if (dir == -1 && s.hours[j].h < s.hours[q].h) pivot = false;
				}
				if (pivot) anchorJ = j;
			}
			if (anchorJ < 0) anchorJ = static_cast<int>(i);
			// refine to the chart bar of that hour with the lowest low / highest high
			const H1Bar& ah = s.hours[anchorJ];
			int anchorBar = ah.first;
			double best = dir == 1 ? Ll(sc, ah.first) : Hh(sc, ah.first);
			for (int b = ah.first; b <= ah.last; ++b)
			{
				if (dir == 1 && Ll(sc, b) < best) { best = Ll(sc, b); anchorBar = b; }
				if (dir == -1 && Hh(sc, b) > best) { best = Hh(sc, b); anchorBar = b; }
			}
			int found = -1;
			for (size_t w = 0; w < s.worldlines.size(); ++w)
				if (s.worldlines[w].dir == dir && s.worldlines[w].anchorH1 == ah.hb) found = static_cast<int>(w);
			if (found >= 0) { if (intensity > s.worldlines[found].intensity) s.worldlines[found].intensity = intensity; }
			else
			{
				Worldline w;
				w.dir = dir; w.anchorBar = anchorBar; w.anchorH1 = ah.hb; w.birthHb = c.hb; w.intensity = intensity;
				w.pvBase = anchorBar > 0 ? s.cumPV[anchorBar - 1] : 0;
				w.vBase = anchorBar > 0 ? s.cumV[anchorBar - 1] : 0;
				s.worldlines.push_back(w);
			}
		}
	}
	// keep every alive line and the 4 most recently dead
	int deadKept = 0;
	for (int w = static_cast<int>(s.worldlines.size()) - 1; w >= 0; --w)
	{
		if (s.worldlines[w].alive) continue;
		if (++deadKept > 4) s.worldlines.erase(s.worldlines.begin() + w);
	}
}

static void UpdateWorldlines(SCStudyInterfaceRef sc, StudyState& s, bool active)
{
	const int last = sc.ArraySize - 1;
	double em = WlEmRange(sc, s);
	bool reset = !active || em != s.wlEmRange || s.wlCommitted > last;
	if (reset)
	{
		s.hours.clear(); s.hoursClosed = 0; s.wlCommitted = 0; s.worldlines.clear();
		s.wlEmRange = em;
	}
	if (!active || last < 1) return;
	// closed bars go into their clock hour
	for (int i = s.wlCommitted; i < last; ++i)
	{
		long long hb = EtEpochMin(s.et[i]) / 60;
		if (s.hours.empty() || s.hours.back().hb != hb)
		{
			H1Bar h; h.hb = hb; h.o = O(sc, i); h.h = Hh(sc, i); h.l = Ll(sc, i); h.c = Cc(sc, i); h.first = i; h.last = i;
			s.hours.push_back(h);
		}
		else
		{
			H1Bar& h = s.hours.back();
			h.h = (std::max)(h.h, Hh(sc, i)); h.l = (std::min)(h.l, Ll(sc, i)); h.c = Cc(sc, i); h.last = i;
		}
	}
	s.wlCommitted = last;
	// an hour is closed once a bar of a later hour exists (committed or forming)
	size_t closed = s.hours.empty() ? 0 : s.hours.size() - 1;
	if (!s.hours.empty() && EtEpochMin(s.et[last]) / 60 != s.hours.back().hb) closed = s.hours.size();
	for (; s.hoursClosed < closed; ++s.hoursClosed) CloseHour(sc, s, s.hoursClosed);
}

// ──────────────────────────────────────────────────────────────────────────────
//  Alerts: a CLOSE that crosses a drawn level, evaluated once when a bar closes (ATAS)
// ──────────────────────────────────────────────────────────────────────────────
static void CheckLevelAlerts(SCStudyInterfaceRef sc, StudyState& s, int closedBar)
{
	if (closedBar < 1 || closedBar == s.alertLastBar) return;
	s.alertLastBar = closedBar;
	double c1 = Cc(sc, closedBar), c2 = Cc(sc, closedBar - 1);
	for (const AlertLevel& al : s.alertLevels)
	{
		bool wanted = al.kind == 0 ? sc.Input[InAlertWalls].GetYesNo() != 0
			: al.kind == 1 ? sc.Input[InAlertSystem].GetYesNo() != 0 : sc.Input[InAlertBos].GetYesNo() != 0;
		if (!wanted) continue;
		bool up = c2 < al.price && c1 >= al.price, down = c2 > al.price && c1 <= al.price;
		if (!up && !down) continue;
		SCString msg;
		msg.Format("TLADe %s crossed %s", al.name.c_str(), up ? "UP" : "DOWN");
		sc.SetAlert(1, msg);
	}
}

// ──────────────────────────────────────────────────────────────────────────────
//  Drawing (the ATAS OnRender, through sc.Graphics)
// ──────────────────────────────────────────────────────────────────────────────
struct Theme { COLORREF pos, neg; };
static Theme ResolveTheme(SCStudyInterfaceRef sc)
{
	switch (sc.Input[InTheme].GetIndex())
	{
		case 1: return { RGB(0x22, 0xd3, 0xee), RGB(0xf4, 0x72, 0xb6) };   // Boreal
		case 2: return { RGB(0x2d, 0xd4, 0xbf), RGB(0xc0, 0x84, 0xfc) };   // Lady Trader
		default: return { RGB(0x22, 0xc5, 0x5e), RGB(0xef, 0x44, 0x44) };  // Wall Street Classic
	}
}
static const COLORREF C_ZG = RGB(0x9c, 0xa3, 0xaf), C_MP = RGB(0xef, 0x44, 0x44), C_EM = RGB(0x3b, 0x82, 0xf6),
	C_VB = RGB(0x9c, 0xa3, 0xaf), C_STRUCT = RGB(0x9c, 0xa3, 0xaf), C_CHARM = RGB(0xf9, 0x73, 0x16),
	C_DFLIP = RGB(0xf5, 0x9e, 0x0b), C_BO_BULL = RGB(0x22, 0xc5, 0x5e), C_BO_BEAR = RGB(0xef, 0x44, 0x44);
static const COLORREF AV_ASIA = RGB(0xF5, 0x9E, 0x0B), AV_EU = RGB(0x3B, 0x82, 0xF6), AV_US = RGB(0x22, 0xC5, 0x5E), AV_PD = RGB(0x6E, 0xE7, 0xB7);

enum Dash { SOLID, DASH, DOT };

struct Painter
{
	SCStudyInterfaceRef sc;
	n_ACSIL::s_GraphicsFont font;
	std::vector<RECT> boxes;   // label boxes placed this frame — a new label stays off them
	COLORREF background = RGB(0, 0, 0);

	explicit Painter(SCStudyInterfaceRef s) : sc(s)
	{
		font.m_FaceName = "Arial";
		font.m_Height = (std::max)(7, (std::min)(16, sc.Input[InFontSize].GetInt()));
		font.m_Weight = FW_NORMAL;
		uint32_t color = 0, width = 0;
		SubgraphLineStyles style = LINESTYLE_SOLID;
		sc.GetGraphicsSetting(sc.ChartNumber, n_ACSIL::GRAPHICS_SETTING_CHART_BACKGROUND, color, width, style);
		background = color;
	}
	static n_ACSIL::s_GraphicsColor Gc(COLORREF c) { n_ACSIL::s_GraphicsColor g; g.SetRGB(GetRValue(c), GetGValue(c), GetBValue(c)); return g; }
	int Y(double price) const { return sc.RegionValueToYPixelCoordinate(static_cast<float>(price), sc.GraphRegion); }
	int X(int bar) const { return sc.BarIndexToXPixelCoordinate(bar); }
	void Line(int x1, int y1, int x2, int y2, COLORREF c, int width, Dash dash)
	{
		n_ACSIL::s_GraphicsPen pen;
		pen.m_PenColor = Gc(c);
		pen.m_Width = (std::max)(1, width);
		pen.m_PenStyle = dash == SOLID ? n_ACSIL::s_GraphicsPen::e_PenStyle::PEN_STYLE_SOLID
			: dash == DASH ? n_ACSIL::s_GraphicsPen::e_PenStyle::PEN_STYLE_DASH : n_ACSIL::s_GraphicsPen::e_PenStyle::PEN_STYLE_DOT;
		sc.Graphics.SetPen(pen);
		sc.Graphics.MoveTo(x1, y1);
		sc.Graphics.LineTo(x2, y2);
	}
	// alpha 0-255 as in the ATAS colours; Sierra takes a transparency level 0-100.
	void Fill(int l, int t, int r, int b, COLORREF c, int alpha)
	{
		n_ACSIL::s_GraphicsRectangle rect;
		rect.Left = l; rect.Top = t; rect.Right = r; rect.Bottom = b;
		alpha = (std::max)(0, (std::min)(255, alpha));
		if (alpha >= 255) sc.Graphics.FillRectangleWithColor(rect, Gc(c));
		else sc.Graphics.FillRectangleWithColorTransparent(rect, Gc(c), static_cast<uint8_t>(100 - alpha * 100 / 255));
	}
	void Frame(int l, int t, int r, int b, COLORREF c)
	{
		Line(l, t, r, t, c, 1, SOLID); Line(r, t, r, b, c, 1, SOLID);
		Line(r, b, l, b, c, 1, SOLID); Line(l, b, l, t, c, 1, SOLID);
	}
	SIZE Measure(const std::string& text)
	{
		n_ACSIL::s_GraphicsSize size;
		SIZE out = { static_cast<LONG>(text.size()) * font.m_Height * 6 / 10, font.m_Height + 4 };
		if (sc.Graphics.GetTextSizeWithFont(text.c_str(), font, size) != 0 && size.Width > 0) { out.cx = size.Width; out.cy = size.Height; }
		return out;
	}
	void Text(const std::string& text, int x, int y, COLORREF c)
	{
		sc.Graphics.SetTextFont(font);
		sc.Graphics.SetTextColor(Gc(c));
		sc.Graphics.SetBackgroundMode(TRANSPARENT);
		sc.Graphics.SetTextAlign(TA_LEFT | TA_TOP | TA_NOUPDATECP);
		sc.Graphics.DrawTextAt(text.c_str(), x, y);
	}
	static bool Hit(const RECT& a, const RECT& b) { return a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom; }
	bool Taken(const RECT& r) const { for (const RECT& b : boxes) if (Hit(b, r)) return true; return false; }
	// Two levels a few points apart drew one label over the other (Alden, 22/10/2026): the line stays
	// at its price, the label box moves to the first free slot — below, then above, then further out.
	RECT Place(int x, int y, int w, int h)
	{
		RECT box = { x, y, x + w, y + h };
		for (int step = 1; step <= 8 && Taken(box); ++step)
		{
			int k = (step + 1) / 2;
			int dy = (step % 2 == 1) ? k * (h + 2) : -k * (h + 2);
			box = { x, y + dy, x + w, y + dy + h };
		}
		boxes.push_back(box);
		return box;
	}
};

static bool TfActive(SCStudyInterfaceRef sc)
{
	if (sc.Input[InShowAboveH1].GetYesNo()) return true;
	return sc.SecondsPerBar <= 0 || sc.SecondsPerBar <= 3600;   // tick / range / volume bars: intraday
}

static Dash UserDash(SCStudyInterfaceRef sc)
{
	int i = sc.Input[InLineStyle].GetIndex();
	return i == 0 ? SOLID : i == 1 ? DASH : DOT;
}

// Line styles as the TradingView indicator: walls and Zero Gamma in the user's style, Max Pain / Vol
// Bands / Structure dotted, EM and Delta Flip dashed, Charm Magnet solid, breakouts by timeframe.
static Dash StyleFor(SCStudyInterfaceRef sc, const std::string& t)
{
	if (t == "CM") return SOLID;
	if (t == "MP" || t == "VH" || t == "VL" || IsStructure(t)) return DOT;
	if (t == "EH" || t == "EL" || t == "EHR" || t == "ELR" || t == "DF") return DASH;
	return UserDash(sc);
}

static void HL(Painter& p, StudyState& s, double price, const std::string& label, COLORREF color, int width,
	int x1, int x2, bool isAbove, int gexSign, int tickLen, Dash dash)
{
	SCStudyInterfaceRef sc = p.sc;
	if (price <= 0) return;
	int y = p.Y(price);
	if (y < sc.StudyRegionTopCoordinate || y > sc.StudyRegionBottomCoordinate) return;
	p.Line(x1, y, x2, y, color, width, dash);
	if (sc.Input[InShowLabels].GetYesNo())
	{
		SIZE sz = p.Measure(label);
		int labW = sz.cx, labH = sz.cy;
		int lx = (x1 - sc.StudyRegionLeftCoordinate > labW + 10) ? (x1 - labW - 6) : (x2 - labW - 6);
		int ly = y - labH - 1;
		int ba = (std::max)(0, (std::min)(255, sc.Input[InLabelBgAlpha].GetInt()));
		COLORREF bg;
		if (sc.Input[InLabelBgPositional].GetYesNo()) bg = isAbove ? sc.Input[InLabelBgAbove].GetColor() : sc.Input[InLabelBgBelow].GetColor();
		else bg = RGB((std::min)(GetRValue(color) / 4 + 8, 255), (std::min)(GetGValue(color) / 4 + 8, 255), (std::min)(GetBValue(color) / 4 + 8, 255));
		COLORREF textCol = sc.Input[InLabelInherit].GetYesNo() ? color : sc.Input[InLabelTextColor].GetColor();
		int boxX = lx - 2, boxW = labW + 6, boxH = labH + 2;
		RECT box = p.Place(boxX, ly - 1, boxW, boxH);
		p.Fill(box.left, box.top, box.right, box.bottom, bg, ba);
		p.Text(label, lx, box.top + 1, textCol);
		// GEX direction tick: right = positive, left = negative. The level's colour.
		if (sc.Input[InShowGexDirection].GetYesNo() && gexSign != 0)
		{
			int midY = box.top + boxH / 2;
			const int gap = 3;
			int tx1, tx2;
			if (gexSign > 0) { tx1 = box.right + gap; tx2 = tx1 + tickLen; }
			else { tx2 = box.left - gap; tx1 = tx2 - tickLen; }
			p.Line(tx1, midY, tx2, midY, color, sc.Input[InGexTickWidth].GetInt(), SOLID);
		}
	}
	if (sc.Input[InShowAxisMarkers].GetYesNo())
	{
		std::string v = Fmt(price, true);
		SIZE sz = p.Measure(v);
		int ax = sc.StudyRegionRightCoordinate + 1;
		p.Fill(ax, y - sz.cy / 2 - 1, ax + sz.cx + 6, y + sz.cy / 2 + 1, color, 255);
		p.Text(v, ax + 3, y - sz.cy / 2, RGB(0, 0, 0));
	}
}

// Session boxes: Asia / Europe / Pre / US hi-lo rectangles of the last futures day on the chart, or of
// every day with "Show Historical". Asia 18:00-03:00, EU 03:00-08:00, Pre 08:00-09:30, US 09:30-16:00 ET.
static void DrawSessionBoxes(Painter& p, const StudyState& s)
{
	SCStudyInterfaceRef sc = p.sc;
	const int n = sc.ArraySize;
	if (n < 2) return;
	const bool hist = sc.Input[InBoxHist].GetYesNo() != 0;
	const int lastDay = FuturesDay(s.et[n - 1]);
	const bool on[4] = { sc.Input[InBoxAsia].GetYesNo() != 0, sc.Input[InBoxEU].GetYesNo() != 0, sc.Input[InBoxPre].GetYesNo() != 0, sc.Input[InBoxUS].GetYesNo() != 0 };
	const COLORREF cols[4] = { RGB(0xf5, 0x9e, 0x0b), RGB(0x3b, 0x82, 0xf6), RGB(0xa8, 0x55, 0xf7), RGB(0x22, 0xc5, 0x5e) };
	const int alphas[4] = { 26, 26, 20, 20 };
	int curType = -1, b0 = -1, b1 = -1; double hi = 0, lo = 0;
	const int firstBar = (std::max)(hist ? 0 : n - 2000, sc.IndexOfFirstVisibleBar - 2000);
	auto flush = [&]()
	{
		if (curType < 0 || b0 < 0 || !on[curType]) return;
		if (b1 < sc.IndexOfFirstVisibleBar || b0 > sc.IndexOfLastVisibleBar) return;
		int x0 = p.X(b0), x1 = p.X(b1);
		if (x1 < x0) std::swap(x0, x1);
		int yT = p.Y(hi), yB = p.Y(lo);
		COLORREF c = cols[curType];
		p.Fill(x0, yT, x0 + (std::max)(2, x1 - x0), yT + (std::max)(1, yB - yT), c, alphas[curType]);
		p.Frame(x0, yT, x0 + (std::max)(2, x1 - x0), yT + (std::max)(1, yB - yT), BlendColor(c, p.background, 65));
	};
	for (int i = (std::max)(0, firstBar); i < n; ++i)
	{
		int fd = FuturesDay(s.et[i]);
		if (!hist && fd != lastDay) continue;
		int mins = EtMins(s.et[i]);
		int type = (mins >= 1080 || mins < 180) ? 0 : mins < 480 ? 1 : mins < 570 ? 2 : mins < 960 ? 3 : -1;
		if (type != curType)
		{
			flush();
			curType = type; b0 = i; b1 = i; hi = Hh(sc, i); lo = Ll(sc, i);
		}
		else { b1 = i; hi = (std::max)(hi, Hh(sc, i)); lo = (std::min)(lo, Ll(sc, i)); }
	}
	flush();
}

// GEX profile: a right-edge histogram of the P: rows. THE SAME BAR AS THE TERMINAL (3.6.1), canonical
// copy terminal/components/LWChart.tsx GexProfilePrimitive "NET GEX": length = |net| / max|net| of the
// whole profile (min 2 px), alpha 0.40 + ratio × 0.45; a strike that is not one of the drawn walls is
// "inactive": grey and 40% of the alpha.
static void DrawProfileBars(Painter& p, const StudyState& s, int x2, const std::set<long long>& active)
{
	SCStudyInterfaceRef sc = p.sc;
	double maxAbs = sc.Input[InProfileScaleMax].GetFloat() > 0 ? sc.Input[InProfileScaleMax].GetFloat() : 0;
	if (maxAbs <= 0) for (const ProfileEntry& pr : s.profile) maxAbs = (std::max)(maxAbs, std::fabs(pr.value));
	if (maxAbs <= 0) return;
	const Theme th = ResolveTheme(sc);
	double tick = sc.TickSize > 0 ? sc.TickSize : 0.25;
	int widthPx = (std::max)(10, sc.Input[InProfileWidth].GetInt());
	const COLORREF inactiveC = RGB(148, 163, 184);
	for (const ProfileEntry& pr : s.profile)
	{
		if (pr.value == 0) continue;
		double ratio = (std::min)(1.0, std::fabs(pr.value) / maxAbs);
		double y = ConvertPrice(sc, s, pr.rawStrike);
		int yPix = p.Y(y);
		if (yPix < sc.StudyRegionTopCoordinate || yPix > sc.StudyRegionBottomCoordinate) continue;
		int barH = (std::max)(2, std::abs(p.Y(y) - p.Y(y + tick * (std::max)(1, sc.Input[InProfileBarTicks].GetInt()))));
		int len = (std::max)(2, static_cast<int>(std::lround(ratio * widthPx)));
		bool isActive = active.count(std::llround(y * 100)) > 0;
		COLORREF baseC = !isActive ? inactiveC : (pr.sign >= 0 ? th.neg : th.pos);   // calls = neg colour, puts = pos colour
		double a = (0.40 + ratio * 0.45) * (isActive ? 1.0 : 0.4);
		p.Fill(x2 - len, yPix - barH / 2, x2, yPix - barH / 2 + (std::max)(1, barH), baseC, static_cast<int>(std::lround(255.0 * a)));
	}
}

// Confluence zones: every level on the chart (feed levels, breakouts, AVWAPs, structure), sorted and
// clustered within a band sized on the EM range. No EM in the data → no zones.
static void DrawConfluenceZones(Painter& p, StudyState& s, int x1, int x2)
{
	SCStudyInterfaceRef sc = p.sc;
	double emH = NA, emL = NA;
	for (const LevelEntry& l : s.levels) { if (l.type == "EH") emH = ConvertPrice(sc, s, l.rawStrike); else if (l.type == "EL") emL = ConvertPrice(sc, s, l.rawStrike); }
	if (emH == NA || emL == NA || emH - emL <= 0) return;
	double band = (emH - emL) * (sc.Input[InConfEmPct].GetFloat() / 100.0) / 2.0;
	std::vector<double> prices;
	for (const LevelEntry& l : s.levels) if (IsGex(l.type) || IsSystem(l.type)) prices.push_back(ConvertPrice(sc, s, l.rawStrike));
	if (sc.Input[InShowBreakout].GetYesNo()) { ComputeLocalBos(sc, s, sc.ArraySize); for (const LevelEntry& b : s.localBos) prices.push_back(ConvertPrice(sc, s, b.rawStrike)); }
	if (sc.Input[InShowStructure].GetYesNo()) { ComputeLocalPA(sc, s, sc.ArraySize); for (const LevelEntry& b : s.localPA) prices.push_back(ConvertPrice(sc, s, b.rawStrike)); }
	const std::vector<double>* av[4] = { &s.avAsia, &s.avEU, &s.avUS, &s.avPD };
	const bool on[4] = { sc.Input[InAvwapAsia].GetYesNo() != 0, sc.Input[InAvwapEU].GetYesNo() != 0, sc.Input[InAvwapUS].GetYesNo() != 0, sc.Input[InAvwapPD].GetYesNo() != 0 };
	int lastIdx = sc.ArraySize - 1;
	for (int i = 0; i < 4; ++i)
		if (on[i] && lastIdx >= 0 && lastIdx < static_cast<int>(av[i]->size()) && (*av[i])[lastIdx] > 0) prices.push_back((*av[i])[lastIdx]);
	std::sort(prices.begin(), prices.end());
	const int n = static_cast<int>(prices.size()), minSize = (std::max)(2, sc.Input[InConfMinSize].GetInt());
	int i0 = 0;
	while (i0 < n)
	{
		double cMin = prices[i0], cMax = cMin; int size = 1, j = i0 + 1;
		while (j < n && prices[j] - cMin <= 2 * band) { cMax = prices[j]; size++; j++; }
		if (size >= minSize)
		{
			int capped = (std::min)(size, 5);
			COLORREF c = capped >= 5 ? RGB(0xef, 0x44, 0x44) : capped >= 4 ? RGB(0xfb, 0x92, 0x3c) : RGB(0xfa, 0xcc, 0x15);
			int alpha = capped >= 5 ? 89 : capped >= 4 ? 77 : 64;
			int yTop = p.Y(cMax), yBot = p.Y(cMin);
			if (yBot - yTop < 2) { yTop -= 1; yBot += 1; }
			p.Fill(x1, yTop, x2, yBot, c, alpha);
			std::string txt = capped >= 5 ? "UBER" : std::to_string(capped) + "x";
			SIZE sz = p.Measure(txt);
			p.Text(txt, x1 + 4, (yTop + yBot) / 2 - sz.cy / 2, c);
		}
		i0 = j;
	}
}

static void DrawStatus(Painter& p, StudyState& s, int count)
{
	SCStudyInterfaceRef sc = p.sc;
	std::string mode = s.isDelayed ? "FREE" : "LIVE";
	bool stale = false;
	std::string age = (s.rawData.empty() && s.manualData.empty()) ? "no data yet"
		: PubLabel(s.isDelayed, s.pubDate, s.pubSession, s.pubHhmm,
			sc.Input[InAutoFetch].GetYesNo() && !s.key.empty(), !s.manualData.empty(), stale);
	s.staleDrawn = stale;
	std::string line1 = " TLADe GEX [" + mode + "] levels=" + std::to_string(count) + " | " + age;
	// Alex's status: what the fetch is doing when something is off
	std::string line2;
	if (s.task.Busy()) line2 = " fetching...";
	else if (!s.fetchError.empty()) line2 = " fetch: " + s.fetchError;
	else if (s.key.empty() && s.manualData.empty()) line2 = " no API key: FREE mode, data 3 days old";
	int x = sc.StudyRegionLeftCoordinate + 4, y = sc.StudyRegionTopCoordinate + 4;
	SIZE s1 = p.Measure(line1), s2 = line2.empty() ? SIZE{ 0, 0 } : p.Measure(line2);
	int w = (std::max)(s1.cx, s2.cx) + 8, h = s1.cy + 4 + (line2.empty() ? 0 : s2.cy + 2);
	bool bad = stale || !s.fetchError.empty();
	p.Fill(x, y, x + w, y + h, bad ? RGB(60, 10, 10) : RGB(12, 14, 22), bad ? 220 : 180);
	p.Text(line1, x + 4, y + 2, stale ? RGB(0xef, 0x44, 0x44) : RGB(220, 220, 220));
	if (!line2.empty()) p.Text(line2, x + 4, y + 2 + s1.cy + 2, s.fetchError.empty() ? RGB(251, 191, 36) : RGB(248, 113, 113));
	p.boxes.push_back(RECT{ x, y, x + w, y + h });
}

static void DrawAvwapPolyline(Painter& p, const StudyState& s, const std::vector<double>& arr, COLORREF color, const char* label)
{
	SCStudyInterfaceRef sc = p.sc;
	if (arr.empty()) return;
	int firstBar = (std::max)(0, sc.IndexOfFirstVisibleBar);
	int lastBar = (std::min)(sc.IndexOfLastVisibleBar, static_cast<int>(arr.size()) - 1);
	if (lastBar < firstBar) return;
	// Historical off: only the futures day of the last visible bar (data-relative, not wall-clock).
	const bool hist = sc.Input[InAvwapHist].GetYesNo() != 0;
	const int minDay = FuturesDay(s.et[lastBar]);
	const int width = sc.Input[InAvwapWidth].GetInt();
	bool havePrev = false; int px = 0, py = 0;
	int lastX = -1, lastY = -1; double lastV = 0;
	for (int b = firstBar; b <= lastBar; ++b)
	{
		double v = arr[b];
		if (v == NA || (!hist && FuturesDay(s.et[b]) < minDay)) { havePrev = false; continue; }
		int x = p.X(b), y = p.Y(v);
		if (havePrev) p.Line(px, py, x, y, color, width, SOLID);
		havePrev = true; px = x; py = y;
		lastX = x; lastY = y; lastV = v;
	}
	if (sc.Input[InAvwapLabels].GetYesNo() && lastX >= 0)
	{
		std::string txt = " " + Fmt(lastV, false) + " " + label + " ";
		SIZE sz = p.Measure(txt);
		RECT box = p.Place(lastX + 2, lastY - sz.cy / 2, sz.cx + 4, sz.cy + 2);
		p.Fill(box.left, box.top, box.right, box.bottom, color, 180);
		p.Text(txt, box.left + 2, box.top + 1, RGB(0, 0, 0));
	}
}

static void DrawWorldlines(Painter& p, const StudyState& s)
{
	SCStudyInterfaceRef sc = p.sc;
	const int last = sc.ArraySize - 1;
	const COLORREF bull = sc.Input[InWlBull].GetColor(), bear = sc.Input[InWlBear].GetColor();
	for (const Worldline& w : s.worldlines)
	{
		int aBar = w.anchorBar, eBar = w.alive ? last : w.deathBar;
		int from = (std::max)(aBar, sc.IndexOfFirstVisibleBar), to = (std::min)(eBar, sc.IndexOfLastVisibleBar);
		COLORREF base = w.dir == 1 ? bull : bear;
		int transp = !w.alive ? 45 : w.intensity == 3 ? 5 : w.intensity == 2 ? 25 : 50;
		COLORREF col = BlendColor(base, p.background, transp);
		bool havePrev = false; int px = 0, py = 0;
		for (int b = from; b <= to; ++b)
		{
			double v = AvwapFrom(s, w, b);
			if (v == NA) { havePrev = false; continue; }
			int x = p.X(b), y = p.Y(v);
			if (havePrev) p.Line(px, py, x, y, col, w.intensity, w.alive ? SOLID : DASH);
			havePrev = true; px = x; py = y;
		}
		if (w.alive && sc.Input[InWlLabels].GetYesNo() && last >= 1 && last <= sc.IndexOfLastVisibleBar)
		{
			double v = AvwapFrom(s, w, last);
			if (v == NA) continue;
			int step = p.X(last) - p.X(last - 1);
			SIZE sz = p.Measure("H1");
			p.Text("H1 " + FormatPrice(sc, s, ChartToRaw(sc, s, v)) + " active", p.X(last) + 2 * step, p.Y(v) - sz.cy / 2, base);
		}
	}
}

void SCDLLCALL DrawToChart(HWND, HDC, SCStudyInterfaceRef sc)
{
	StudyState* sp = static_cast<StudyState*>(sc.GetPersistentPointer(PersistentStateKey));
	if (sp == nullptr || sc.ArraySize <= 0 || static_cast<int>(sp->et.size()) < sc.ArraySize) return;
	StudyState& s = *sp;
	Painter p(sc);
	if (!TfActive(sc)) { s.alertLevels.clear(); return; }
	const Theme th = ResolveTheme(sc);
	const int x1 = sc.StudyRegionLeftCoordinate, x2 = sc.StudyRegionRightCoordinate, regionW = x2 - x1;
	std::vector<LevelEntry> snap = s.levels;
	std::vector<AlertLevel> alerts;

	if (sc.Input[InShowBoxes].GetYesNo()) DrawSessionBoxes(p, s);
	if (sc.Input[InShowProfile].GetYesNo() && !s.profile.empty())
	{
		// active = the walls this chart draws (the terminal's "named price line" set)
		std::set<long long> active;
		for (const LevelEntry& l : snap) if (IsGex(l.type)) active.insert(std::llround(ConvertPrice(sc, s, l.rawStrike) * 100));
		DrawProfileBars(p, s, x2, active);
	}
	if (sc.Input[InShowConfluence].GetYesNo()) DrawConfluenceZones(p, s, x1, x2);
	if (sc.Input[InShowStatus].GetYesNo()) DrawStatus(p, s, static_cast<int>(snap.size()));

	// Session AVWAP and worldlines come from the chart's bars: drawn even with no cloud levels.
	if (sc.Input[InAvwapAsia].GetYesNo()) DrawAvwapPolyline(p, s, s.avAsia, AV_ASIA, "AVWAP Asia");
	if (sc.Input[InAvwapEU].GetYesNo()) DrawAvwapPolyline(p, s, s.avEU, AV_EU, "AVWAP EU");
	if (sc.Input[InAvwapUS].GetYesNo()) DrawAvwapPolyline(p, s, s.avUS, AV_US, "AVWAP US");
	if (sc.Input[InAvwapPD].GetYesNo()) DrawAvwapPolyline(p, s, s.avPD, AV_PD, "AVWAP US Prev Day");
	if (WlActive(sc, true)) DrawWorldlines(p, s);

	if (sc.Input[InShowBreakout].GetYesNo()) { ComputeLocalBos(sc, s, sc.ArraySize); snap.insert(snap.end(), s.localBos.begin(), s.localBos.end()); }
	if (sc.Input[InShowStructure].GetYesNo()) { ComputeLocalPA(sc, s, sc.ArraySize); snap.insert(snap.end(), s.localPA.begin(), s.localPA.end()); }
	if (snap.empty()) { s.alertLevels.clear(); return; }

	const double spot = Cc(sc, sc.ArraySize - 1);
	int maxVisible = sc.Input[InMaxGex].GetInt() <= 0 ? 10 : sc.Input[InMaxGex].GetInt();
	const bool maxIsAll = maxVisible >= 999;
	const int halfMax = static_cast<int>(std::ceil(maxVisible / 2.0));

	// closest CW/PW above/below: always drawn ("protect" rule)
	double closestAbove = NA, closestBelow = NA, dAbove = 1e300, dBelow = 1e300;
	for (const LevelEntry& l : snap)
	{
		if (!IsGex(l.type)) continue;
		double y = ConvertPrice(sc, s, l.rawStrike), d = std::fabs(y - spot);
		if (y > spot && d < dAbove) { dAbove = d; closestAbove = l.rawStrike; }
		else if (y <= spot && d < dBelow) { dBelow = d; closestBelow = l.rawStrike; }
	}
	double maxWallMag = 0, maxTickMag = 0;
	for (const LevelEntry& l : snap)
	{
		if (IsGex(l.type)) maxWallMag = (std::max)(maxWallMag, l.magnitude);
		if (l.gexSign != 0) maxTickMag = (std::max)(maxTickMag, l.magnitude);
	}
	if (maxWallMag <= 0) maxWallMag = 1;
	if (maxTickMag <= 0) maxTickMag = 1;
	const double maxWallPx = regionW * (sc.Input[InWallMaxPct].GetInt() / 100.0), minWallPx = regionW * (sc.Input[InWallMinPct].GetInt() / 100.0);

	int aboveDrawn = 0, belowDrawn = 0;
	for (const LevelEntry& l : snap)
	{
		double y = ConvertPrice(sc, s, l.rawStrike);
		const bool isGex = IsGex(l.type), isSystem = IsSystem(l.type), isStructure = IsStructure(l.type),
			isBreakout = IsBreakout(l.type), isCharm = l.type == "CM", isDf = l.type == "DF";
		bool inRange = !sc.Input[InShowOnlyNear].GetYesNo() || (spot > 0 && std::fabs(spot - y) / spot * 100.0 <= sc.Input[InNearPct].GetFloat());
		bool passes = !sc.Input[InEnableThreshold].GetYesNo() || !isGex || l.magnitude == 0 || l.magnitude >= sc.Input[InThreshold].GetFloat();
		bool isProtected = isGex && (l.rawStrike == closestAbove || l.rawStrike == closestBelow);
		bool isAbove = y > spot;
		bool show = false;
		if (isGex && sc.Input[InShowGex].GetYesNo() && passes)
		{
			if (maxIsAll || isProtected) show = true;
			else if (isAbove && aboveDrawn < halfMax) show = true;
			else if (!isAbove && belowDrawn < halfMax) show = true;
		}
		else if (isSystem && sc.Input[InShowSystem].GetYesNo()) show = true;
		else if (isStructure && sc.Input[InShowStructure].GetYesNo() && l.isLocal) show = true;   // feed structure ignored
		else if (isBreakout && sc.Input[InShowBreakout].GetYesNo() && l.isLocal) show = true;     // feed breakouts ignored
		else if (isCharm && sc.Input[InShowCharm].GetYesNo()) show = true;
		else if (isDf && sc.Input[InShowDeltaFlip].GetYesNo()) show = true;
		if (!show || !inRange) continue;

		COLORREF color;
		int width = isSystem ? sc.Input[InLineWidthSystem].GetInt() : sc.Input[InLineWidthGex].GetInt();
		bool flipped = false;
		// Colour = NATURE (a Call Wall stays the call colour whatever its role); a flipped wall is told by
		// a thinner line and the word "flipped" (Sierra's text drawing has no ↺ glyph: it drew a box).
		if (l.type == "CW" || l.type == "PW")
		{
			auto it = s.wallFlipped.find(l.rawStrike);
			flipped = it != s.wallFlipped.end() && it->second;
			color = l.type == "CW" ? th.neg : th.pos;
			if (!maxIsAll && !isProtected) { if (isAbove) aboveDrawn++; else belowDrawn++; }
		}
		else if (l.type == "ZG") color = C_ZG;
		else if (l.type == "MP") color = C_MP;
		else if (l.type == "EH" || l.type == "EL" || l.type == "EHR" || l.type == "ELR") color = C_EM;
		else if (l.type == "VH" || l.type == "VL") color = C_VB;
		else if (l.type == "BL") color = C_BO_BULL;
		else if (l.type == "BS") color = C_BO_BEAR;
		else if (isCharm) color = C_CHARM;
		else if (isDf) color = C_DFLIP;
		else color = C_STRUCT;
		if (flipped) width = (std::max)(1, width - 1);
		Dash dash = isBreakout ? (l.boHigherTf ? SOLID : DASH) : StyleFor(sc, l.type);
		if (isBreakout) width = l.boWidth;

		int lineX1 = x1;
		if (sc.Input[InScaleWalls].GetYesNo() && isGex)
		{
			double frac = (std::min)(1.0, l.magnitude / maxWallMag);
			lineX1 = (std::max)(x1, x2 - static_cast<int>(minWallPx + frac * (maxWallPx - minWallPx)));
		}
		int tickMin = sc.Input[InGexTickMin].GetInt(), tickMax = sc.Input[InGexTickMax].GetInt(), tickLen = tickMax;
		if (sc.Input[InGexScaleByStrength].GetYesNo())
		{
			double frac = l.magnitude > 0 ? (std::min)(1.0, l.magnitude / maxTickMag) : 0;
			tickLen = (std::max)(1, static_cast<int>(std::lround(tickMin + frac * (tickMax - tickMin))));
		}
		std::string name = isBreakout ? l.label + " " + Fmt(y, false)
			: FormatPrice(sc, s, l.rawStrike) + " " + LevelName(l.type, l.label) + (flipped ? " flipped" : "");
		HL(p, s, y, name, color, width, lineX1, x2, isAbove, l.gexSign, tickLen, dash);
		if (isGex || isSystem || isBreakout)
		{
			AlertLevel a; a.price = y; a.name = name; a.kind = isGex ? 0 : isSystem ? 1 : 2;
			alerts.push_back(a);
		}
	}
	s.alertLevels = alerts;
}

static const char* HELP = R"TLADEDOC(
<div style="line-height:1.5;">
<p><strong>TLADe GEX Dashboard</strong> for Sierra Chart, release 3.6.1. The same indicator as the TLADe
ATAS, NinjaTrader and MotiveWave editions: GEX walls, Zero Gamma, Max Pain, Expected Move, Vol Bands,
Delta Flip and Charm Magnet from the TLADe cloud; session AVWAP, Worldlines H1, session boxes, breakout
levels, previous day/week structure and confluence zones computed from this chart's own bars.</p>
<h3>Quick start</h3>
<ol>
<li>Add <strong>TLADe GEX Dashboard</strong> through Analysis &gt; Studies &gt; Add Custom Study, on an
intraday chart (1 to 60 minutes; Worldlines need a bar period that divides the hour).</li>
<li>Enter your own TLADe API key in the first input. Without a key the study runs in FREE mode: the data
is three days old. Keep the key private: it is saved in the Chartbook.</li>
<li>Display Ticker: <em>auto</em> reads ES or NQ from the chart symbol. On a cash or ETF chart choose
SPX, SPY, NDX or QQQ: the study subtracts the spread (S:) and divides by the ratio (R:) of the data string.</li>
<li>The status in the top-left corner says which publication the levels come from, and STALE when a newer
one should exist. The study checks once a minute and fetches only when a newer publication is due.</li>
</ol>
<h3>What is drawn</h3>
<ul>
<li><strong>Walls:</strong> Call Walls in the call colour, Put Walls in the put colour (theme). The nearest
wall above and below price is always drawn; the others up to <em>Max GEX walls</em>, half per side.
A wall that price has closed beyond twice on 5-minute closes is <em>flipped</em>: thinner, marked "flipped".</li>
<li><strong>System levels:</strong> Zero Gamma grey, Max Pain red, EM blue dashed, Vol Bands grey dotted,
Delta Flip amber dashed, Charm Magnet orange.</li>
<li><strong>Session AVWAP:</strong> Asia from 18:00 ET, EU from 03:00, US from 09:30 of the current futures
day, and the US one of the previous day running on. All re-anchor at the 18:00 ET rollover.</li>
<li><strong>Worldlines H1:</strong> AVWAPs anchored at the swing before an H1 displacement confirmed by a
gap; they die at an H1 close on the wrong side.</li>
<li><strong>Breakouts:</strong> closed bars of each timeframe whose close breaks the previous high/low with
more body than wick; a later opposite bar closing back through the level removes it.</li>
<li><strong>Profile:</strong> the net GEX per strike at the right edge, as the terminal draws it.</li>
</ul>
<p>Bar times are converted from the chart's time zone to New York, so the sessions are correct whatever
time zone the chart uses. During replay the study does not fetch: the levels loaded stay on the chart, and
they may come from after the replayed time.</p>
<p>Credits: the fetch code, the key handling and the status corner come from the community Sierra study
written by Alex. Each user authenticates with their own TLADe key; do not share a Chartbook that holds it.</p>
</div>
)TLADEDOC";
}  // namespace TLADeGex

SCSFExport scsf_TLADe_GEX_Dashboard(SCStudyInterfaceRef sc)
{
	using namespace TLADeGex;
	if (sc.SetDefaults)
	{
		sc.GraphName = "TLADe GEX Dashboard";
		sc.StudyDescription = HELP;
		sc.GraphRegion = 0;
		sc.AutoLoop = 0;
		sc.UpdateAlways = 1;
		// Sierra prints the input values next to the study name on the chart, and the first input is the
		// API key: every screenshot of the chart would publish it (seen on 28/10/2026).
		sc.DisplayStudyInputValues = 0;
		sc.TextInputName = "Manual data string (override: paste S:|R:|L:|P: from the terminal)";

		auto yn = [&](int i, const char* name, int v) { sc.Input[i].Name = name; sc.Input[i].SetYesNo(v); };
		auto in = [&](int i, const char* name, int v, int lo, int hi) { sc.Input[i].Name = name; sc.Input[i].SetInt(v); sc.Input[i].SetIntLimits(lo, hi); };
		auto fl = [&](int i, const char* name, float v) { sc.Input[i].Name = name; sc.Input[i].SetFloat(v); };
		auto co = [&](int i, const char* name, COLORREF v) { sc.Input[i].Name = name; sc.Input[i].SetColor(v); };

		sc.Input[InApiKey].Name = "API Key (empty = FREE mode, 3-day delayed; private, saved in the Chartbook)";
		sc.Input[InApiKey].SetString("");
		yn(InAutoFetch, "Auto-fetch from TLADe Cloud", 1);
		yn(InRefreshNow, "Refresh now (resets to No)", 0);
		sc.Input[InTicker].Name = "Ticker display (auto = ES or NQ from the chart symbol)";
		sc.Input[InTicker].SetCustomInputStrings("auto;ES;SPX;SPY;NQ;NDX;QQQ");
		sc.Input[InTicker].SetCustomInputIndex(T_AUTO);
		fl(InManualSpread, "Manual spread override (0 = from S:)", 0.0f);
		yn(InSnapToTick, "Snap to instrument tick", 1);
		yn(InShowAboveH1, "Draw on timeframes above 1H", 0);
		yn(InShowGex, "Show GEX walls (CW/PW)", 1);
		yn(InShowSystem, "Show System (ZG/MP/EM/VB)", 1);
		yn(InShowStructure, "Show Structure (PDH/PDL/PWH/PWL)", 0);
		yn(InShowCharm, "Show Charm Magnet (CM)", 0);
		yn(InShowDeltaFlip, "Show Delta Flip (DF)", 1);
		yn(InShowLabels, "Show labels", 1);
		yn(InShowAxisMarkers, "Show price-axis markers", 1);
		yn(InShowStatus, "Show status (publication / fetch)", 1);
		in(InMaxGex, "Max GEX walls (999 = All)", 10, 2, 999);
		yn(InEnableThreshold, "Enable threshold filter", 0);
		fl(InThreshold, "Threshold magnitude (M)", 50.0f);
		yn(InShowOnlyNear, "Show only levels near price", 0);
		fl(InNearPct, "  Radius (%)", 3.0f);
		sc.Input[InTheme].Name = "Theme";
		sc.Input[InTheme].SetCustomInputStrings("Wall Street Classic;Boreal;Lady Trader");
		sc.Input[InTheme].SetCustomInputIndex(0);
		sc.Input[InLineStyle].Name = "Line style walls/ZG";
		sc.Input[InLineStyle].SetCustomInputStrings("Solid;Dashed;Dotted");
		sc.Input[InLineStyle].SetCustomInputIndex(2);
		in(InLineWidthGex, "Line width GEX", 1, 1, 5);
		in(InLineWidthSystem, "Line width System/ZG/MP", 2, 1, 5);
		in(InFontSize, "Font size label", 9, 7, 16);
		yn(InLabelInherit, "Label text inherits line color", 1);
		co(InLabelTextColor, "Label text color (when not inheriting)", RGB(255, 255, 255));
		in(InLabelBgAlpha, "Label background opacity (0-255)", 140, 0, 255);
		yn(InLabelBgPositional, "Label background differs above/below spot", 0);
		co(InLabelBgAbove, "Label background - above spot", RGB(0xdc, 0x26, 0x26));
		co(InLabelBgBelow, "Label background - below spot", RGB(0x16, 0xa3, 0x4a));
		yn(InScaleWalls, "Scale wall lines by magnitude", 0);
		in(InWallMaxPct, "Wall line max width (% chart)", 60, 10, 100);
		in(InWallMinPct, "Wall line min width (% chart)", 5, 1, 50);
		yn(InShowGexDirection, "Show GEX direction (left/right)", 1);
		yn(InGexScaleByStrength, "GEX direction length proportional to wall strength", 1);
		in(InGexTickMin, "GEX line min length (px)", 4, 1, 200);
		in(InGexTickMax, "GEX line max length (px)", 40, 2, 400);
		in(InGexTickWidth, "GEX line thickness", 3, 1, 20);
		yn(InAvwapAsia, "Session AVWAP - Asia", 1);
		yn(InAvwapEU, "Session AVWAP - EU", 1);
		yn(InAvwapUS, "Session AVWAP - US", 1);
		yn(InAvwapPD, "Session AVWAP - Prev Day US", 1);
		yn(InAvwapHist, "Show historical AVWAP (prev days)", 0);
		in(InAvwapWidth, "AVWAP line width", 2, 1, 4);
		yn(InAvwapLabels, "Show AVWAP labels", 1);
		yn(InShowBreakout, "Show Breakout Structure", 0);
		yn(InBosM, "  Breakout Monthly", 0);
		yn(InBosW, "  Breakout Weekly", 0);
		yn(InBosD, "  Breakout Daily", 1);
		yn(InBosH4, "  Breakout 4H", 1);
		yn(InBosH1, "  Breakout 1H", 1);
		in(InBosKeep, "  Keep last N per timeframe", 2, 1, 10);
		yn(InShowConfluence, "Show Confluence Zones", 0);
		in(InConfMinSize, "  Min cluster size for box", 3, 2, 5);
		fl(InConfEmPct, "  Band width (% of EM)", 7.0f);
		yn(InShowProfile, "Show Profile Bars (P:)", 1);
		in(InProfileWidth, "Profile width (px)", 120, 10, 1000);
		in(InProfileBarTicks, "Profile bar height (ticks)", 8, 1, 100);
		fl(InProfileScaleMax, "Profile scale max (0 = the chain's own max, as the terminal)", 0.0f);
		yn(InShowBoxes, "Show Session Boxes", 0);
		yn(InBoxAsia, "  Asia (18:00-03:00 ET)", 1);
		yn(InBoxEU, "  Europe (03:00-08:00 ET)", 1);
		yn(InBoxPre, "  Pre-Market (08:00-09:30 ET)", 1);
		yn(InBoxUS, "  US RTH (09:30-16:00 ET)", 1);
		yn(InBoxHist, "  Show historical (prev days)", 0);
		yn(InEnableAlerts, "Enable level cross alerts (Alerts tab sets the sound)", 0);
		yn(InAlertWalls, "  Alert: Call/Put Walls", 1);
		yn(InAlertSystem, "  Alert: ZG / Max Pain / EM / Vol Bands", 1);
		yn(InAlertBos, "  Alert: Breakouts", 1);
		yn(InShowWorldlines, "Show Worldlines H1", 1);
		co(InWlBull, "  Worldline bull", RGB(38, 166, 154));
		co(InWlBear, "  Worldline bear", RGB(239, 83, 80));
		yn(InWlLabels, "  Labels on active worldlines", 1);
		return;
	}

	StudyState* state = static_cast<StudyState*>(sc.GetPersistentPointer(PersistentStateKey));
	if (sc.LastCallToFunction)
	{
		delete state;
		sc.SetPersistentPointer(PersistentStateKey, nullptr);
		return;
	}
	if (state == nullptr)
	{
		state = new StudyState();
		sc.SetPersistentPointer(PersistentStateKey, state);
	}
	sc.p_GDIFunction = DrawToChart;
	sc.DisplayStudyInputValues = 0;   // also on studies added before this build: the key must never reach the chart
	StudyState& s = *state;
	if (sc.HideStudy || sc.ArraySize <= 0) return;

	bool changed = false;
	UpdateFetch(sc, s, changed);
	if (sc.DownloadingHistoricalData) return;

	int start = sc.IsFullRecalculation ? 0 : (std::max)(0, (std::min)(sc.UpdateStartIndex, static_cast<int>(s.et.size())));
	if (sc.IsFullRecalculation)
	{
		s.et.clear(); s.sums.clear(); s.avAsia.clear(); s.avEU.clear(); s.avUS.clear(); s.avPD.clear();
		s.cumPV.clear(); s.cumV.clear(); s.bosKey.clear(); s.paBars = -1; s.flipsKey.clear();
		s.wlCommitted = sc.ArraySize + 1;   // forces the worldline replay
		changed = true;
	}
	ComputeBars(sc, s, start);

	const bool tfActive = TfActive(sc);
	if (changed) s.flipsKey.clear();
	ComputeWallFlips(sc, s, sc.ArraySize);
	UpdateWorldlines(sc, s, WlActive(sc, tfActive));
	if (sc.Input[InEnableAlerts].GetYesNo() && sc.ArraySize >= 3) CheckLevelAlerts(sc, s, sc.ArraySize - 2);
}
