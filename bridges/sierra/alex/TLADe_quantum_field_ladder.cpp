// TLADe Quantum Field Ladder for Sierra Chart (ACSIL)
// Author: Alex (community contributor). Written by Alex and sent to TLADe on 6 October 2026.
// Modified by TLADe with the author's permission (21 October 2026: "You can do it and have full
// control over the study"): the index->ETF ratio read from the data string (R:), the magnets read
// from the B: row of the profile, R: stripped from the manual Quantum string.
// Community bridge: TLADe has no Sierra data subscription and cannot reproduce feed-side issues.
// Each user authenticates with their own TLADe API key.

#include "sierrachart.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <cwchar>
#include <memory>
#include <mutex>
#include <process.h>
#include <sstream>
#include <string>
#include <utility>
#include <vector>
#include <winhttp.h>

SCDLLName("TLADe_Quantum_Field_Ladder")

namespace TLADeQuantum
{
enum DisplayTicker { ES, SPX, SPY, NQ, NDX, QQQ };
enum ApiExposureBasis { AutomaticExposure, ApiGexExposure, CombinedExposure };

enum InputIndex
{
	TickerInput,
	SpreadInput,
	GradientStepsInput,
	FieldScaleInput,
	ShowCenterInput,
	LabelOffsetInput,
	MaxLevelsInput,
	GradientWidthInput,
	CenterWidthInput,
	CenterStyleInput,
	LabelSizeInput,
	MagnetColorInput,
	WallColorInput,
	OuterTransparencyInput,
	InnerTransparencyInput,
	ShowLabelsInput,
	ShowDetailsInput,
	DrawBehindInput,
	RecenterInput,
	LabelAlignInput,
	UseExposureFilterInput,
	MinExposureInput,
	DataSourceInput,
	ApiKeyInput,
	ApiRefreshInput,
	ApiBandWidthInput,
	ApiAutomaticInput,
	ShowRefreshStatusInput,
	RefreshStatusTopInput,
	ShowDataWarningsInput = 37,
	ApiExposureBasisInput
};

static const int PersistentStateKey = 1;
static const int MaximumGradientSteps = 16;
static const int GradientLineSlot = 2 * MaximumGradientSteps;
static const int CenterLineSlot = GradientLineSlot + 2 * (MaximumGradientSteps + 1);
static const int LabelSlot = CenterLineSlot + 1;
static const int ConnectorSlot = LabelSlot + 1;
static const int DrawingsPerNode = ConnectorSlot + 1;

struct Node
{
	double strike = 0;
	double intensity = 0;
	double rangeUp = 0;
	double rangeDown = 0;
	bool isMagnet = false;
	bool hasExposure = false;
	double combinedExposureMillions = 0;
	double apiMagnitudeMillions = 0;
	std::string wallType;
	std::string details;
	std::string label;
};

struct Payload
{
	double spread = 0;
	// Index->ETF ratio from R: (SPX/SPY ~10.03, NDX/QQQ ~41.1), frozen once a day like the spread.
	// 0 = not in the string.
	double ratio = 0;
	bool invalidSpread = false;
	int rejectedRecords = 0;
	std::vector<Node> nodes;
};

static std::string Trim(const std::string& text)
{
	const size_t begin = text.find_first_not_of(" \t\r\n");
	if (begin == std::string::npos)
		return "";
	return text.substr(begin, text.find_last_not_of(" \t\r\n") - begin + 1);
}

static bool ParseNumber(const std::string& text, double& value)
{
	const std::string trimmed = Trim(text);
	if (trimmed.empty())
		return false;
	char* end = nullptr;
	value = std::strtod(trimmed.c_str(), &end);
	return end != trimmed.c_str() && *end == '\0' && std::isfinite(value);
}

static bool ParseExposureMillions(const std::string& text, double& value)
{
	std::string amount = Trim(text);
	if (amount.empty())
		return false;
	double multiplier = 0.000001;
	switch (amount.back())
	{
		case 'K': case 'k': multiplier = 0.001; amount.pop_back(); break;
		case 'M': case 'm': multiplier = 1; amount.pop_back(); break;
		case 'B': case 'b': multiplier = 1000; amount.pop_back(); break;
	}
	if (!ParseNumber(amount, value))
		return false;
	value *= multiplier;
	return std::isfinite(value);
}

static bool ParseCombinedExposure(const std::string& details, double& combinedMillions)
{
	bool hasCall = false;
	bool hasPut = false;
	double callMillions = 0;
	double putMillions = 0;
	std::istringstream lines(details);
	std::string line;
	while (std::getline(lines, line))
	{
		const size_t separator = line.find(':');
		if (separator == std::string::npos)
			continue;
		const std::string name = Trim(line.substr(0, separator));
		if (name == "Call")
		{
			if (hasCall || !ParseExposureMillions(line.substr(separator + 1), callMillions))
				return false;
			hasCall = true;
		}
		else if (name == "Put")
		{
			if (hasPut || !ParseExposureMillions(line.substr(separator + 1), putMillions))
				return false;
			hasPut = true;
		}
	}
	combinedMillions = std::fabs(callMillions) + std::fabs(putMillions);
	return hasCall && hasPut && std::isfinite(combinedMillions);
}

static Payload ParsePayload(const std::string& input, double spreadOverride)
{
	Payload result;
	result.spread = spreadOverride;
	std::string remaining = Trim(input);
	if (remaining.compare(0, 3, "\xEF\xBB\xBF") == 0)
		remaining = Trim(remaining.substr(3));
	if (remaining.compare(0, 2, "S:") == 0)
	{
		const size_t separator = remaining.find('|');
		if (separator == std::string::npos)
		{
			result.invalidSpread = true;
			return result;
		}
		double parsedSpread = 0;
		if (ParseNumber(remaining.substr(2, separator - 2), parsedSpread))
			result.spread = parsedSpread;
		else
			result.invalidSpread = true;
		remaining.erase(0, separator + 1);
	}
	// R: (the terminal's export carries it right after S:). Left in place it was read as the first
	// record, rejected, and that node was lost without a word.
	if (remaining.compare(0, 2, "R:") == 0)
	{
		const size_t separator = remaining.find('|');
		double parsedRatio = 0;
		if (separator != std::string::npos)
		{
			if (ParseNumber(remaining.substr(2, separator - 2), parsedRatio) && parsedRatio > 0)
				result.ratio = parsedRatio;
			remaining.erase(0, separator + 1);
		}
	}

	std::istringstream records(remaining);
	std::string record;
	while (std::getline(records, record, ';'))
	{
		if (Trim(record).empty())
			continue;
		std::istringstream fieldsStream(record);
		std::array<std::string, 6> fields;
		bool complete = true;
		for (std::string& field : fields)
		{
			if (!std::getline(fieldsStream, field, ','))
			{
				complete = false;
				break;
			}
			field = Trim(field);
		}
		Node node;
		double nodeType = 0;
		if (!complete || !ParseNumber(fields[0], node.strike)
			|| !ParseNumber(fields[1], node.intensity)
			|| !ParseNumber(fields[2], nodeType)
			|| !ParseNumber(fields[3], node.rangeUp)
			|| !ParseNumber(fields[4], node.rangeDown)
			|| (nodeType != 0 && nodeType != 1)
			|| node.intensity < 0 || node.intensity > 5
			|| node.rangeUp < node.rangeDown || fields[5].empty())
		{
			++result.rejectedRecords;
			continue;
		}
		node.isMagnet = nodeType == 1;
		node.wallType = fields[5];
		std::getline(fieldsStream, node.details, '\0');
		node.details = Trim(node.details);
		std::replace(node.details.begin(), node.details.end(), '~', '\n');
		node.hasExposure = ParseCombinedExposure(node.details, node.combinedExposureMillions);
		result.nodes.push_back(node);
	}
	return result;
}

static bool ParseIndicatorPayload(const std::string& input, double spreadOverride,
	double bandWidth, Payload& output)
{
	if (input.size() > 1024 * 1024 || input.find('\0') != std::string::npos
		|| !std::isfinite(bandWidth) || bandWidth <= 0)
		return false;
	std::string remaining = Trim(input);
	if (remaining.compare(0, 3, "\xEF\xBB\xBF") == 0)
		remaining = Trim(remaining.substr(3));
	Payload parsed;
	parsed.spread = spreadOverride;
	for (const char* prefix : {"S:", "R:"})
	{
		if (remaining.compare(0, 2, prefix) != 0)
			continue;
		const size_t separator = remaining.find('|');
		double value = 0;
		if (separator == std::string::npos || !ParseNumber(remaining.substr(2, separator - 2), value)
			|| (prefix[0] == 'R' && value <= 0))
			return false;
		if (prefix[0] == 'S')
			parsed.spread = value;
		else
			parsed.ratio = value;
		remaining.erase(0, separator + 1);
	}
	if (remaining.compare(0, 2, "L:") != 0)
		return false;
	const size_t profileStart = remaining.find("|P:");
	std::istringstream records(remaining.substr(2, profileStart == std::string::npos
		? std::string::npos : profileStart - 2));
	std::string record;
	double maximumMagnitude = 0;
	while (std::getline(records, record, ';'))
	{
		if (Trim(record).empty())
			continue;
		std::istringstream fieldsStream(record);
		std::array<std::string, 3> fields;
		for (std::string& field : fields)
		{
			if (!std::getline(fieldsStream, field, ','))
				return false;
			field = Trim(field);
		}
		std::string tail;
		std::getline(fieldsStream, tail, '\0');
		const size_t magnitudeSeparator = tail.rfind(',');
		if (magnitudeSeparator == std::string::npos)
			return false;
		double strike = 0, magnitude = 0;
		if (!ParseNumber(fields[0], strike) || !ParseNumber(tail.substr(magnitudeSeparator + 1), magnitude)
			|| magnitude < 0 || fields[1].empty())
			return false;
		if (fields[1] != "ZG" && fields[1] != "CW" && fields[1] != "PW")
			continue;
		if (strike <= 0)
			return false;
		Node node;
		node.strike = strike;
		node.wallType = fields[1];
		node.label = fields[2];
		node.isMagnet = fields[1] == "ZG";
		node.apiMagnitudeMillions = magnitude;
		node.rangeUp = strike + bandWidth / 2.0;
		node.rangeDown = strike - bandWidth / 2.0;
		node.details = Trim(tail.substr(0, magnitudeSeparator));
		std::replace(node.details.begin(), node.details.end(), '~', '\n');
		node.hasExposure = ParseCombinedExposure(node.details, node.combinedExposureMillions);
		if (node.wallType != "ZG" || magnitude != 0)
		{
			SCString magnitudeText;
			magnitudeText.Format("API GEX: %.1fM", magnitude);
			if (!node.details.empty())
				node.details += '\n';
			node.details += magnitudeText.GetChars();
		}
		maximumMagnitude = (std::max)(maximumMagnitude, magnitude);
		parsed.nodes.push_back(node);
	}
	if (parsed.nodes.empty())
		return false;
	// MAGNETS. Since 14/10/2026 the profile carries the engine's verdict as one row,
	// "B:<strike> <strike> ...,0,0" (strikes rounded to whole points, as the wall rows). It is read,
	// never recomputed from the legs: the engine decides the magnet (neither side over 60%).
	if (profileStart != std::string::npos)
	{
		std::istringstream profileRows(remaining.substr(profileStart + 3));
		std::string row;
		while (std::getline(profileRows, row, ';'))
		{
			row = Trim(row);
			if (row.compare(0, 2, "B:") != 0)
				continue;
			std::istringstream strikes(row.substr(2, row.find(',') == std::string::npos ? std::string::npos : row.find(',') - 2));
			std::string token;
			while (strikes >> token)
			{
				double magnetStrike = 0;
				if (!ParseNumber(token, magnetStrike))
					continue;
				for (Node& node : parsed.nodes)
					if (node.wallType != "ZG" && std::llround(node.strike) == std::llround(magnetStrike))
						node.isMagnet = true;
			}
			break;
		}
	}
	for (Node& node : parsed.nodes)
		node.intensity = node.wallType == "ZG" ? 3.0 : maximumMagnitude > 0
			? node.apiMagnitudeMillions / maximumMagnitude * 5.0 : 0.0;
	output = std::move(parsed);
	return true;
}

// The ETF divisor is the ratio from the data string (R:), applied only inside a sane band: the same
// field carries both families, so an NQ string loaded on SPY (ratio ~41) or a string without R:
// falls back to the original constants, 10 and 40.
static double ConvertPrice(double futuresPrice, int ticker, double spread, double ratio = 0)
{
	switch (ticker)
	{
		case SPX:
		case NDX: return futuresPrice - spread;
		case SPY: return (futuresPrice - spread) / ((ratio >= 9.0 && ratio <= 12.0) ? ratio : 10.0);
		case QQQ: return (futuresPrice - spread) / ((ratio >= 35.0 && ratio <= 48.0) ? ratio : 40.0);
		default: return futuresPrice;
	}
}

static std::vector<Node> SelectNodes(const Payload& payload, int ticker,
	double referencePrice, int maxLevels, bool useExposureFilter = false, double minExposureMillions = 50,
	bool useApiGex = false)
{
	std::vector<Node> above;
	std::vector<Node> below;
	if (!std::isfinite(referencePrice))
		return {};
	for (const Node& node : payload.nodes)
	{
		const double filterAmount = useApiGex ? node.apiMagnitudeMillions : node.combinedExposureMillions;
		if (useExposureFilter && node.wallType != "ZG"
			&& ((!useApiGex && !node.hasExposure) || filterAmount < minExposureMillions))
			continue;
		const double price = ConvertPrice(node.strike, ticker, payload.spread, payload.ratio);
		if (!std::isfinite(price))
			continue;
		(price > referencePrice ? above : below).push_back(node);
	}
	const auto strongestFirst = [](const Node& first, const Node& second)
	{
		return first.intensity > second.intensity;
	};
	std::stable_sort(above.begin(), above.end(), strongestFirst);
	std::stable_sort(below.begin(), below.end(), strongestFirst);
	const size_t perSide = static_cast<size_t>(((std::max)(1, (std::min)(60, maxLevels)) + 1) / 2);
	if (above.size() > perSide)
		above.resize(perSide);
	if (below.size() > perSide)
		below.resize(perSide);
	above.insert(above.end(), below.begin(), below.end());
	return above;
}

struct Selection
{
	bool initialized = false;
	std::string source;
	int ticker = ES;
	int maxLevels = 20;
	double spreadOverride = 0;
	double referencePrice = 0;
	bool useExposureFilter = false;
	double minExposureMillions = 50;
	bool fromApi = false;
	double apiBandWidth = 0;
	bool useApiGex = false;
	Payload payload;
	std::vector<Node> frozenNodes;
};

static bool UpdateSelection(Selection& selection, const std::string& input,
	int ticker, double spreadOverride, int maxLevels, double referencePrice, bool recenter,
	bool useExposureFilter = false, double minExposureMillions = 50,
	bool fromApi = false, double apiBandWidth = 0, bool useApiGex = false)
{
	const bool filterApiGex = fromApi && useApiGex;
	if (selection.initialized && !recenter && selection.source == input
		&& selection.ticker == ticker && selection.spreadOverride == spreadOverride
		&& selection.maxLevels == maxLevels && selection.useExposureFilter == useExposureFilter
		&& selection.minExposureMillions == minExposureMillions && selection.fromApi == fromApi
		&& selection.apiBandWidth == apiBandWidth && selection.useApiGex == filterApiGex)
		return false;
	Payload payload;
	payload.spread = spreadOverride;
	if (fromApi && !input.empty())
	{
		if (!ParseIndicatorPayload(input, spreadOverride, apiBandWidth, payload))
			return false;
	}
	else if (!fromApi)
		payload = ParsePayload(input, spreadOverride);
	selection.initialized = true;
	selection.source = input;
	selection.ticker = ticker;
	selection.spreadOverride = spreadOverride;
	selection.maxLevels = maxLevels;
	selection.referencePrice = referencePrice;
	selection.useExposureFilter = useExposureFilter;
	selection.minExposureMillions = minExposureMillions;
	selection.fromApi = fromApi;
	selection.apiBandWidth = apiBandWidth;
	selection.useApiGex = filterApiGex;
	selection.payload = std::move(payload);
	selection.frozenNodes = SelectNodes(selection.payload, ticker, referencePrice, maxLevels,
		useExposureFilter, minExposureMillions, filterApiGex);
	return true;
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
	if (key.empty() || key.size() > 2048)
		return false;
	for (unsigned char character : key)
		if (character <= 32 || character >= 127)
			return false;
	return true;
}

struct ApiReply
{
	std::string data;
	std::string error;
	DWORD status = 0;
	unsigned int generation = 0;
	SYSTEMTIME fetchedUtc = {};
	ULONGLONG fetchedTick = 0;
};

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
		if (module == NULL)
			return;
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

static ApiReply FetchIndicatorData(const HttpLibrary& library, const std::string& key,
	bool nqFamily, HANDLE cancelEvent)
{
	ApiReply result;
	const ULONGLONG started = GetTickCount64();
	auto cancelled = [&]()
	{
		return (cancelEvent != NULL && WaitForSingleObject(cancelEvent, 0) == WAIT_OBJECT_0)
			|| GetTickCount64() - started >= 20000;
	};
	auto failed = [&]()
	{
		result.error = "Network request failed (Windows error " + std::to_string(GetLastError()) + ").";
		return result;
	};
	if (!ValidApiKey(key) || !library.Ready())
	{
		result.error = "A valid API key and Windows WinHTTP are required.";
		return result;
	}
	if (cancelled())
	{
		result.error = "Request cancelled.";
		return result;
	}
	HttpHandle session(library, library.open(L"TLADe-SierraChart-Quantum/1.0", WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
		WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0));
	if (session.value == NULL || !library.setTimeouts(session.value, 3000, 3000, 3000, 3000))
		return failed();
	DWORD protocols = WINHTTP_FLAG_SECURE_PROTOCOL_TLS1_2;
	if (!library.setOption(session.value, WINHTTP_OPTION_SECURE_PROTOCOLS, &protocols, sizeof(protocols)))
		return failed();
	HttpHandle connection(library, library.connect(session.value,
		L"europe-west1-omggex.cloudfunctions.net", INTERNET_DEFAULT_HTTPS_PORT, 0));
	if (connection.value == NULL)
		return failed();
	HttpHandle request(library, library.openRequest(connection.value, L"GET",
		nqFamily ? L"/indicatorData?ticker=NDX" : L"/indicatorData?ticker=SPX",
		NULL, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE));
	if (request.value == NULL)
		return failed();
	DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
	if (!library.setOption(request.value, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy)))
		return failed();
	std::wstring headers = L"X-API-Key: " + std::wstring(key.begin(), key.end()) + L"\r\nAccept: text/plain\r\n";
	const BOOL sent = library.sendRequest(request.value, headers.c_str(), static_cast<DWORD>(headers.size()),
		WINHTTP_NO_REQUEST_DATA, 0, 0, 0);
	const DWORD sendError = sent ? ERROR_SUCCESS : GetLastError();
	ClearSecret(headers);
	if (!sent)
	{
		SetLastError(sendError);
		return failed();
	}
	if (!library.receiveResponse(request.value, NULL))
		return failed();
	DWORD statusSize = sizeof(result.status);
	if (!library.queryHeaders(request.value, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
		WINHTTP_HEADER_NAME_BY_INDEX, &result.status, &statusSize, WINHTTP_NO_HEADER_INDEX))
		return failed();
	if (result.status != 200)
	{
		result.error = "HTTP " + std::to_string(result.status);
		if (result.status == 401 || result.status == 403)
			result.error += ": check the API key and account access.";
		else if (result.status == 429)
			result.error += ": server rate limit; automatic fetching paused.";
		return result;
	}
	std::string data;
	char buffer[8192];
	for (;;)
	{
		if (cancelled())
		{
			result.error = "Request cancelled or timed out.";
			return result;
		}
		DWORD count = 0;
		if (!library.readData(request.value, buffer, sizeof(buffer), &count))
			return failed();
		if (count == 0)
			break;
		if (data.size() + count > 1024 * 1024)
		{
			result.error = "Response exceeds the 1 MB limit.";
			return result;
		}
		data.append(buffer, count);
	}
	Payload validated;
	if (!ParseIndicatorPayload(data, 0, 1, validated))
	{
		result.error = "Response is not a valid indicatorData level string.";
		return result;
	}
	result.data = std::move(data);
	GetSystemTime(&result.fetchedUtc);
	result.fetchedTick = GetTickCount64();
	return result;
}

struct ApiTask
{
	HANDLE thread = NULL;
	HANDLE cancelEvent = NULL;
	std::string key;
	bool nqFamily = false;
	unsigned int generation = 0;
	ApiReply reply;
	ApiTask() = default;
	ApiTask(const ApiTask&) = delete;
	ApiTask& operator=(const ApiTask&) = delete;
	~ApiTask()
	{
		Cancel();
		if (thread != NULL)
		{
			WaitForSingleObject(thread, INFINITE);
			CloseHandle(thread);
		}
		if (cancelEvent != NULL) CloseHandle(cancelEvent);
		ClearSecret(key);
	}
	void Cancel() { if (cancelEvent != NULL) SetEvent(cancelEvent); }
	bool Busy() const { return thread != NULL; }
	static unsigned __stdcall Run(void* parameter)
	{
		ApiTask& task = *static_cast<ApiTask*>(parameter);
		try
		{
			HttpLibrary library;
			task.reply = FetchIndicatorData(library, task.key, task.nqFamily, task.cancelEvent);
		}
		catch (...)
		{
			task.reply.data.clear();
			task.reply.error = "The API request could not be completed.";
		}
		task.reply.generation = task.generation;
		ClearSecret(task.key);
		return 0;
	}
	bool Start(const std::string& apiKey, bool isNq, unsigned int requestGeneration)
	{
		if (Busy()) return false;
		key = apiKey;
		nqFamily = isNq;
		generation = requestGeneration;
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
		if (thread == NULL || WaitForSingleObject(thread, 0) != WAIT_OBJECT_0)
			return false;
		CloseHandle(thread);
		CloseHandle(cancelEvent);
		thread = NULL;
		cancelEvent = NULL;
		completed = std::move(reply);
		return true;
	}
};

struct ApiState
{
	ApiTask task;
	std::string key;
	std::string data;
	std::string lastMessage;
	bool enabled = false;
	bool nqFamily = false;
	bool replaying = false;
	bool refreshPending = false;
	bool authorizationBlocked = false;
	unsigned int generation = 0;
	long long attemptedSlot = -1;
	ULONGLONG nextRequestTick = 0;
	ULONGLONG lastFetchTick = 0;
	SYSTEMTIME lastFetchUtc = {};
	long long lastFetchSlot = -1;
	DWORD lastHttpStatus = 0;
	bool lastFetchChanged = false;
	bool lastFetchFailed = false;
	bool usingSharedData = false;
	~ApiState() { ClearSecret(key); }
};

static long long ApiSessionSlot(const SCDateTime& eastern)
{
	static const int slots[] = {196, 481, 571, 631, 781, 1082};
	const long long day = static_cast<long long>(eastern.GetDate()) * 1440;
	const int minute = eastern.GetTimeInSeconds() / 60;
	long long latest = day - 1440 + slots[5];
	for (int slot : slots)
		if (minute >= slot) latest = day + slot;
	return latest;
}

static const char* ApiSessionName(long long slot)
{
	if (slot < 0) return "--";
	switch (slot % 1440)
	{
		case 1082: return "Asia";
		case 196: return "Europe";
		case 481: return "Pre-open";
		case 571: return "RTH Open";
		case 631: return "Op. Range";
		case 781: return "Charm Kick";
		default: return "--";
	}
}

static bool ApiUtcToEastern(const SYSTEMTIME& utc, SCDateTime& eastern)
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

static bool ApiClock(SCDateTime& eastern)
{
	SYSTEMTIME utc = {};
	GetSystemTime(&utc);
	return ApiUtcToEastern(utc, eastern);
}

static void ConfigureApi(ApiState& state, bool enabled, const std::string& key, bool nqFamily, bool replaying)
{
	if (state.enabled == enabled && state.key == key && state.nqFamily == nqFamily && state.replaying == replaying) return;
	state.task.Cancel();
	++state.generation;
	if (state.key != key || state.nqFamily != nqFamily || state.enabled != enabled)
	{
		state.data.clear();
		state.lastFetchTick = 0;
		state.lastFetchUtc = {};
		state.lastFetchSlot = -1;
		state.lastFetchChanged = false;
	}
	ClearSecret(state.key);
	state.key = key;
	state.enabled = enabled;
	state.nqFamily = nqFamily;
	state.replaying = replaying;
	state.attemptedSlot = -1;
	state.refreshPending = false;
	state.authorizationBlocked = false;
	state.lastMessage.clear();
	state.lastHttpStatus = 0;
	state.lastFetchFailed = false;
	state.usingSharedData = false;
}

static bool AcceptApiReply(ApiState& state, const ApiReply& reply)
{
	if (!state.enabled || state.replaying || reply.generation != state.generation) return false;
	state.lastHttpStatus = reply.status;
	state.lastFetchFailed = reply.data.empty();
	state.usingSharedData = false;
	if (!reply.data.empty())
	{
		state.lastFetchChanged = state.data != reply.data;
		state.data = reply.data;
		state.lastFetchTick = reply.fetchedTick;
		state.lastFetchUtc = reply.fetchedUtc;
		state.lastFetchSlot = state.attemptedSlot;
		state.authorizationBlocked = false;
	}
	else if (reply.status == 401 || reply.status == 403)
		state.authorizationBlocked = true;
	return true;
}

struct RefreshStatus
{
	SCString text;
	COLORREF color = RGB(156, 163, 175);
};

static RefreshStatus BuildRefreshStatus(bool useApi, const ApiState* state, bool hasManualData,
	bool automatic, ULONGLONG now = GetTickCount64())
{
	RefreshStatus status;
	if (!useApi)
	{
		status.text = hasManualData ? "Quantum: Manual data" : "Quantum: No manual data";
		return status;
	}
	status.color = RGB(251, 191, 36);
	if (state == nullptr || !state->enabled)
		status.text = "Quantum API: Waiting";
	else if (state->replaying)
		status.text = "Quantum API: Paused (replay)";
	else if (!ValidApiKey(state->key))
	{
		status.text = state->key.empty() ? "Quantum API: Key required" : "Quantum API: Invalid key";
		status.color = RGB(248, 113, 113);
	}
	else if (state->task.Busy())
		status.text = "Quantum API: Fetching...";
	else if (state->lastFetchFailed)
	{
		if (state->lastHttpStatus != 0 && state->lastHttpStatus != 200)
			status.text.Format("Quantum API: HTTP %lu", static_cast<unsigned long>(state->lastHttpStatus));
		else
			status.text = "Quantum API: Refresh failed";
		if (state->lastHttpStatus == 429 && now < state->nextRequestTick)
		{
			const ULONGLONG seconds = (state->nextRequestTick - now + 999) / 1000;
			SCString retry;
			retry.Format("\nHTTP 429 retry in: %02lu:%02lu", static_cast<unsigned long>(seconds / 60),
				static_cast<unsigned long>(seconds % 60));
			status.text += retry;
		}
		status.color = RGB(248, 113, 113);
	}
	else if (state->refreshPending || state->attemptedSlot < 0)
		status.text = "Quantum API: Refresh queued";
	else if (state->lastFetchUtc.wYear == 0)
		status.text = "Quantum API: Waiting";
	else
	{
		status.text = state->usingSharedData ? "Quantum API: Shared data"
			: state->lastFetchChanged ? "Quantum API: Refreshed (changed)" : "Quantum API: Refreshed (same data)";
		status.color = RGB(74, 222, 128);
	}
	if (state != nullptr && state->lastFetchUtc.wYear != 0)
	{
		SCDateTime eastern;
		SCString fetched;
		if (ApiUtcToEastern(state->lastFetchUtc, eastern))
		{
			int year = 0, month = 0, day = 0;
			eastern.GetDateYMD(year, month, day);
			const int seconds = eastern.GetTimeInSeconds();
			fetched.Format("\nLast OK: %02d/%02d %02d:%02d:%02d ET", month, day,
				seconds / 3600, seconds / 60 % 60, seconds % 60);
		}
		else
			fetched.Format("\nLast OK: %02d/%02d %02d:%02d:%02d UTC", state->lastFetchUtc.wMonth,
				state->lastFetchUtc.wDay, state->lastFetchUtc.wHour, state->lastFetchUtc.wMinute, state->lastFetchUtc.wSecond);
		status.text += fetched;
		status.text += "\nLast check: ";
		status.text += ApiSessionName(state->lastFetchSlot);
	}
	else
		status.text += "\nLast OK: --";
	if (!automatic)
		status.text += "\nAuto checks: Off";
	return status;
}

static bool ApiDue(const ApiState& state, long long slot, ULONGLONG now, bool automatic)
{
	return state.enabled && !state.replaying && !state.task.Busy() && ValidApiKey(state.key)
		&& now >= state.nextRequestTick
		&& (state.refreshPending || (!state.authorizationBlocked
			&& (state.attemptedSlot < 0 || (automatic && slot > state.attemptedSlot))));
}

struct CachedReply
{
	std::string key;
	bool nqFamily = false;
	long long slot = -1;
	ApiReply reply;
	~CachedReply() { ClearSecret(key); }
};

class ApiCache
{
	std::mutex mutex;
	std::vector<std::unique_ptr<CachedReply>> entries;
public:
	void Store(const ApiState& state, const ApiReply& reply)
	{
		if (reply.status != 200 || reply.data.empty()) return;
		std::lock_guard<std::mutex> lock(mutex);
		CachedReply* target = nullptr;
		for (const auto& entry : entries)
			if (entry->key == state.key && entry->nqFamily == state.nqFamily) target = entry.get();
		if (target == nullptr)
		{
			if (entries.size() >= 16) entries.erase(entries.begin());
			entries.emplace_back(new CachedReply());
			target = entries.back().get();
			target->key = state.key;
			target->nqFamily = state.nqFamily;
		}
		if (target->reply.fetchedTick > reply.fetchedTick) return;
		target->slot = state.attemptedSlot;
		target->reply = reply;
	}
	void Invalidate(const std::string& key)
	{
		std::lock_guard<std::mutex> lock(mutex);
		entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const auto& entry) { return entry->key == key; }), entries.end());
	}
	bool Reuse(ApiState& state, long long slot, ULONGLONG now)
	{
		if (!state.enabled || state.replaying || state.authorizationBlocked || state.task.Busy()) return false;
		std::lock_guard<std::mutex> lock(mutex);
		for (const auto& entry : entries)
		{
			if (entry->key != state.key || entry->nqFamily != state.nqFamily || entry->slot != slot
				|| entry->reply.fetchedTick < state.lastFetchTick || entry->reply.fetchedTick > now
				|| now - entry->reply.fetchedTick >= 86400000
				|| (entry->reply.fetchedTick == state.lastFetchTick && state.attemptedSlot >= 0)) continue;
			ApiReply reply = entry->reply;
			reply.generation = state.generation;
			if (!AcceptApiReply(state, reply)) return false;
			state.attemptedSlot = (std::max)(state.attemptedSlot, entry->slot);
			state.lastFetchSlot = entry->slot;
			state.usingSharedData = true;
			return true;
		}
		return false;
	}
};

static ApiCache& SharedCache()
{
	static ApiCache cache;
	return cache;
}

static void ApiLog(SCStudyInterfaceRef sc, ApiState& state, const std::string& message, bool error)
{
	if (state.lastMessage == message) return;
	state.lastMessage = message;
	SCString text;
	text.Format("TLADe Quantum API: %s", message.c_str());
	sc.AddMessageToLog(text, error ? 1 : 0);
}

static void UpdateApi(SCStudyInterfaceRef sc, ApiState& state, bool enabled,
	const char* keyInput, bool nqFamily, bool automatic, bool refresh)
{
	std::string key = enabled && keyInput ? Trim(keyInput) : "";
	ConfigureApi(state, enabled, key, nqFamily, sc.ReplayStatus != REPLAY_STOPPED);
	ClearSecret(key);
	ApiCache& cache = SharedCache();
	ApiReply reply;
	if (state.task.Take(reply) && AcceptApiReply(state, reply))
	{
		if (reply.data.empty())
		{
			if (reply.status == 401 || reply.status == 403) cache.Invalidate(state.key);
			if (reply.status == 429) state.nextRequestTick = GetTickCount64() + 900000;
			ApiLog(sc, state, reply.error + " Previous API data retained; see Data Source for manual mode.", true);
		}
		else
		{
			cache.Store(state, reply);
			SCString message;
			message.Format("%s data fetched at %04d-%02d-%02d %02d:%02d:%02d UTC (%d bytes). Fetch time, not publication time.",
				nqFamily ? "NDX/NQ" : "SPX/ES", reply.fetchedUtc.wYear, reply.fetchedUtc.wMonth, reply.fetchedUtc.wDay,
				reply.fetchedUtc.wHour, reply.fetchedUtc.wMinute, reply.fetchedUtc.wSecond, static_cast<int>(reply.data.size()));
			ApiLog(sc, state, message.GetChars(), false);
		}
	}
	if (!enabled) return;
	if (refresh) state.refreshPending = true;
	if (state.replaying) { ApiLog(sc, state, "Fetching paused during replay.", false); return; }
	if (!ValidApiKey(state.key)) { ApiLog(sc, state, "Enter your API key in the study input. No free/delayed request was made.", true); return; }
	SCDateTime eastern;
	if (!ApiClock(eastern))
	{
		state.lastFetchFailed = true;
		state.lastHttpStatus = 0;
		ApiLog(sc, state, "New York time zone unavailable; fetching paused.", true);
		return;
	}
	const long long slot = ApiSessionSlot(eastern);
	const ULONGLONG now = GetTickCount64();
	if (cache.Reuse(state, slot, now)) ApiLog(sc, state, "Reused a matching Quantum response; no additional API request.", false);
	if (!ApiDue(state, slot, now, automatic)) return;
	state.attemptedSlot = slot;
	state.refreshPending = false;
	if (!state.task.Start(state.key, nqFamily, state.generation))
	{
		state.lastFetchFailed = true;
		state.lastHttpStatus = 0;
		ApiLog(sc, state, "Unable to start background fetch; previous data retained.", true);
	}
	else ApiLog(sc, state, nqFamily ? "Requesting NDX/NQ indicatorData." : "Requesting SPX/ES indicatorData.", false);
}

struct StudyState
{
	Selection selection;
	std::unique_ptr<ApiState> api;
	std::vector<int> drawingNumbers;
	int refreshStatusNumber = -1;
	int refreshStatusSpace = 0;
	int lastDrawnBar = -1;
	SCDateTime lastDrawnTime;
	COLORREF lastBackground = 0;
	bool drawingFailed = false;
	int lastLabelAlign = -1;
};

static void ClearRefreshStatus(SCStudyInterfaceRef sc, StudyState& state)
{
	if (state.refreshStatusNumber != -1)
		sc.DeleteACSChartDrawing(sc.ChartNumber, TOOL_DELETE_CHARTDRAWING, state.refreshStatusNumber);
	state.refreshStatusNumber = -1;
	state.refreshStatusSpace = 0;
}

static int MissingExposureCount(const Selection& selection)
{
	return selection.useExposureFilter && !selection.useApiGex
		? static_cast<int>(std::count_if(selection.payload.nodes.begin(), selection.payload.nodes.end(), [](const Node& node)
			{ return node.wallType != "ZG" && !node.hasExposure; })) : 0;
}

static SCString BuildDataWarning(const Selection& selection)
{
	SCString warning;
	const int missing = MissingExposureCount(selection);
	if (missing > 0)
	{
		warning.Format("Exposure filter: %d excluded\nCall/Put data missing or invalid", missing);
		if (selection.fromApi)
			warning += "\nAPI GEX basis available in In:39";
	}
	return warning;
}

static void DrawRefreshStatus(SCStudyInterfaceRef sc, StudyState& state, bool useApi)
{
	const int width = sc.StudyRegionRightCoordinate - sc.StudyRegionLeftCoordinate;
	const int height = sc.StudyRegionBottomCoordinate - sc.StudyRegionTopCoordinate;
	const int top = (std::max)(4, sc.Input[RefreshStatusTopInput].GetInt());
	const bool showStatus = sc.Input[ShowRefreshStatusInput].GetYesNo() != 0;
	const SCString warning = sc.Input[ShowDataWarningsInput].GetYesNo()
		? BuildDataWarning(state.selection) : SCString();
	if ((!showStatus && warning.GetLength() == 0) || width <= 24 || height <= top + 24)
	{
		ClearRefreshStatus(sc, state);
		return;
	}
	RefreshStatus status;
	if (showStatus)
	{
		status = BuildRefreshStatus(useApi, state.api.get(), !state.selection.payload.nodes.empty(),
			sc.Input[ApiAutomaticInput].GetYesNo() != 0);
		if (state.selection.fromApi && state.selection.useExposureFilter && state.selection.useApiGex)
		{
			SCString filter;
			filter.Format("\nFilter: API GEX >= %.2fM", state.selection.minExposureMillions);
			status.text += filter;
		}
	}
	else
		status.text = "Quantum data warning";
	if (warning.GetLength() > 0)
	{
		status.text += "\n";
		status.text += warning;
		if (status.color != RGB(248, 113, 113))
			status.color = RGB(251, 191, 36);
	}
	s_UseTool label;
	label.Clear();
	label.ChartNumber = sc.ChartNumber;
	label.Region = sc.GraphRegion;
	label.DrawingType = DRAWING_TEXT;
	label.AddMethod = UTAM_ADD_OR_ADJUST;
	label.LineNumber = state.refreshStatusNumber;
	label.BeginDateTime = 1 + static_cast<int>(std::ceil(8.0 * 149.0 / width));
	label.BeginValue = static_cast<float>(100.0 - 100.0 * top / height);
	label.UseRelativeVerticalValues = 1;
	label.TextAlignment = DT_TOP | DT_LEFT;
	label.Text = status.text;
	label.MultiLineLabel = 1;
	label.Color = status.color;
	label.FontBackColor = RGB(24, 24, 24);
	label.TransparentLabelBackground = 0;
	label.DrawUnderneathMainGraph = 0;
	label.HideDrawing = 0;
	if (sc.GetChartTextFontFaceName != nullptr)
		label.FontFace = sc.GetChartTextFontFaceName();
	for (int fontSize = 8; fontSize >= 6; --fontSize)
	{
		label.FontSize = fontSize;
		n_ACSIL::s_GraphicsFont font;
		font.m_FaceName = label.FontFace;
		font.m_Height = fontSize;
		font.m_Weight = FW_NORMAL;
		std::istringstream lines(label.Text.GetChars());
		std::string line;
		int textWidth = 0;
		int textHeight = 4;
		while (std::getline(lines, line))
		{
			n_ACSIL::s_GraphicsSize size;
			const bool measured = sc.Graphics.GetTextSizeWithFont != nullptr
				&& sc.Graphics.GetTextSizeWithFont(line.c_str(), font, size) != 0;
			const int lineWidth = measured ? size.Width : static_cast<int>(line.size()) * fontSize;
			const int lineHeight = measured && size.Height > 0 ? size.Height : fontSize * 2 + 4;
			textWidth = (std::max)(textWidth, lineWidth + 8);
			textHeight += lineHeight + 2;
		}
		if (textWidth + 24 <= width && top + textHeight + 8 <= height)
		{
			if (sc.UseTool(label) != 0)
			{
				state.refreshStatusNumber = label.LineNumber;
				state.refreshStatusSpace = top + textHeight + 8;
				return;
			}
			break;
		}
	}
	ClearRefreshStatus(sc, state);
}

static void ClearDrawings(SCStudyInterfaceRef sc, StudyState& state)
{
	for (int lineNumber : state.drawingNumbers)
		if (lineNumber != -1)
			sc.DeleteACSChartDrawing(sc.ChartNumber, TOOL_DELETE_CHARTDRAWING, lineNumber);
	state.drawingNumbers.clear();
	state.lastDrawnBar = -1;
	ClearRefreshStatus(sc, state);
}

static COLORREF BlendColor(COLORREF foreground, COLORREF background, int transparency)
{
	transparency = (std::max)(0, (std::min)(100, transparency));
	const int opacity = 100 - transparency;
	return RGB((GetRValue(foreground) * opacity + GetRValue(background) * transparency + 50) / 100,
		(GetGValue(foreground) * opacity + GetGValue(background) * transparency + 50) / 100,
		(GetBValue(foreground) * opacity + GetBValue(background) * transparency + 50) / 100);
}

static COLORREF GetBackgroundColor(SCStudyInterfaceRef sc)
{
	uint32_t color = 0;
	uint32_t width = 0;
	SubgraphLineStyles style = LINESTYLE_SOLID;
	sc.GetGraphicsSetting(sc.ChartNumber, n_ACSIL::GRAPHICS_SETTING_CHART_BACKGROUND,
		color, width, style);
	return color;
}

static int InnerTransparency(const Node& node, int inner)
{
	return (std::max)(10, inner - static_cast<int>((node.intensity - 1.0) * 5.0));
}

static double FieldHalfWidth(const Node& node, const Selection& selection, double fieldScale)
{
	return (ConvertPrice(node.rangeUp, selection.ticker, selection.payload.spread, selection.payload.ratio)
		- ConvertPrice(node.rangeDown, selection.ticker, selection.payload.spread, selection.payload.ratio)) * 0.5 * fieldScale;
}

struct LeftEdgeLabelPlacement
{
	size_t nodeIndex = 0;
	int anchorTop = 0;
	int height = 0;
	int width = 0;
	int top = 0;
	int column = 0;
};

static void ArrangeLeftEdgeLabels(std::vector<LeftEdgeLabelPlacement>& labels,
	int regionTop, int regionBottom, int gap)
{
	if (regionBottom <= regionTop)
	{
		labels.clear();
		return;
	}
	const int availableHeight = regionBottom - regionTop;
	labels.erase(std::remove_if(labels.begin(), labels.end(), [availableHeight](const LeftEdgeLabelPlacement& label)
	{
		return label.height <= 0 || label.height > availableHeight;
	}), labels.end());
	std::sort(labels.begin(), labels.end(), [](const LeftEdgeLabelPlacement& first,
		const LeftEdgeLabelPlacement& second)
	{
		if (first.anchorTop != second.anchorTop)
			return first.anchorTop < second.anchorTop;
		return first.nodeIndex < second.nodeIndex;
	});

	size_t columnStart = 0;
	int column = 0;
	while (columnStart < labels.size())
	{
		size_t columnEnd = columnStart;
		int occupiedHeight = 0;
		while (columnEnd < labels.size())
		{
			const int requiredHeight = labels[columnEnd].height
				+ (columnEnd == columnStart ? 0 : gap);
			if (columnEnd > columnStart && occupiedHeight + requiredHeight > availableHeight)
				break;
			occupiedHeight += requiredHeight;
			++columnEnd;
		}

		int nextTop = regionTop;
		for (size_t labelIndex = columnStart; labelIndex < columnEnd; ++labelIndex)
		{
			LeftEdgeLabelPlacement& label = labels[labelIndex];
			label.column = column;
			label.top = (std::max)(label.anchorTop, nextTop);
			nextTop = label.top + label.height + gap;
		}
		int nextBottom = regionBottom;
		for (size_t labelIndex = columnEnd; labelIndex-- > columnStart;)
		{
			LeftEdgeLabelPlacement& label = labels[labelIndex];
			label.top = (std::min)(label.top, nextBottom - label.height);
			nextBottom = label.top - gap;
		}
		columnStart = columnEnd;
		++column;
	}
}

static void ConfigureLineLabel(s_UseTool& label, int horizontalAlign)
{
	label.HideDrawing = 0;
	if (horizontalAlign < 1 || horizontalAlign > 5)
		return;
	label.BeginIndex = -1;
	switch (horizontalAlign)
	{
		case 1:
			label.BeginDateTime = 1;
			label.TextAlignment = DT_TOP | DT_LEFT;
			break;
		case 2:
			label.BeginDateTime = -2;
			label.TextAlignment = DT_TOP | DT_RIGHT;
			break;
		case 3:
			label.BeginDateTime = -2;
			label.TextAlignment = DT_TOP | DT_LEFT;
			break;
		case 4:
			label.BeginDateTime = 150;
			label.TextAlignment = DT_TOP | DT_RIGHT;
			break;
		case 5:
			label.BeginDateTime = 75;
			label.TextAlignment = DT_TOP | DT_CENTER;
			break;
	}
}

static void LayoutLeftEdgeLabels(SCStudyInterfaceRef sc, std::vector<s_UseTool>& labels,
	std::vector<s_UseTool>& connectors, int reservedTop = 0)
{
	const int visibleTop = sc.StudyRegionTopCoordinate + 4;
	const int regionTop = visibleTop + (std::max)(0, reservedTop);
	const int regionBottom = sc.StudyRegionBottomCoordinate - 4;
	const int regionWidth = sc.StudyRegionRightCoordinate - sc.StudyRegionLeftCoordinate;
	for (s_UseTool& label : labels)
		label.HideDrawing = 1;
	if (regionBottom <= regionTop || regionWidth <= 16)
		return;

	std::vector<LeftEdgeLabelPlacement> placements;
	for (size_t nodeIndex = 0; nodeIndex < labels.size(); ++nodeIndex)
	{
		const s_UseTool& label = labels[nodeIndex];
		const int anchorTop = sc.RegionValueToYPixelCoordinate(label.BeginValue, sc.GraphRegion);
		if (anchorTop < visibleTop || anchorTop >= regionBottom)
			continue;
		n_ACSIL::s_GraphicsFont font;
		font.m_FaceName = label.FontFace;
		font.m_Height = label.FontSize;
		font.m_Weight = label.FontBold > 0 ? FW_BOLD : FW_NORMAL;
		LeftEdgeLabelPlacement placement;
		placement.nodeIndex = nodeIndex;
		placement.anchorTop = anchorTop;
		placement.height = 4;
		std::istringstream textLines(label.Text.GetChars());
		std::string textLine;
		while (std::getline(textLines, textLine))
		{
			const SCString measureText = textLine.empty() ? "Ag" : textLine.c_str();
			n_ACSIL::s_GraphicsSize size;
			const bool measured = sc.Graphics.GetTextSizeWithFont != nullptr
				&& sc.Graphics.GetTextSizeWithFont(measureText, font, size) != 0;
			const int lineHeight = measured && size.Height > 0 ? size.Height : label.FontSize * 2 + 4;
			const int lineWidth = measured ? size.Width : static_cast<int>(textLine.size()) * label.FontSize;
			placement.height += lineHeight + 2;
			placement.width = (std::max)(placement.width, lineWidth + 8);
		}
		if (placement.width + 16 <= regionWidth)
			placements.push_back(placement);
	}
	ArrangeLeftEdgeLabels(placements, regionTop, regionBottom, 4);
	if (placements.empty())
		return;

	std::vector<int> columnWidths(placements.back().column + 1, 0);
	for (const LeftEdgeLabelPlacement& placement : placements)
		columnWidths[placement.column] = (std::max)(columnWidths[placement.column], placement.width);
	std::vector<int> columnPositions(columnWidths.size());
	double nextLeft = 12;
	for (size_t column = 0; column < columnWidths.size(); ++column)
	{
		columnPositions[column] = 1 + static_cast<int>(std::ceil(nextLeft * 149.0 / regionWidth));
		nextLeft = (columnPositions[column] - 1) * regionWidth / 149.0 + columnWidths[column] + 16;
	}

	for (const LeftEdgeLabelPlacement& placement : placements)
	{
		const int horizontalPosition = columnPositions[placement.column];
		const double left = (horizontalPosition - 1) * regionWidth / 149.0;
		if (horizontalPosition > 150 || left + placement.width + 4 > regionWidth)
			continue;
		s_UseTool& label = labels[placement.nodeIndex];
		const float strikeValue = label.BeginValue;
		label.BeginIndex = -1;
		label.BeginDateTime = horizontalPosition;
		label.BeginValue = static_cast<float>(sc.YPixelCoordinateToGraphValue(placement.top));
		label.TextAlignment = DT_TOP | DT_LEFT;
		label.HideDrawing = 0;
		if (placement.top != placement.anchorTop)
		{
			s_UseTool& connector = connectors[placement.nodeIndex];
			connector.Clear();
			connector.DrawingType = DRAWING_LINE;
			connector.BeginDateTime = 1;
			connector.BeginValue = strikeValue;
			connector.EndDateTime = label.BeginDateTime;
			connector.EndValue = label.BeginValue;
			connector.Color = label.FontBackColor;
			connector.LineWidth = 1;
			connector.LineStyle = LINESTYLE_DOT;
			connector.DrawUnderneathMainGraph = 0;
		}
	}
}

static bool DrawLadder(SCStudyInterfaceRef sc, StudyState& state, COLORREF background, bool labelsOnly = false)
{
	const Selection& selection = state.selection;
	const int lastIndex = sc.ArraySize - 1;
	const int steps = (std::max)(6, (std::min)(MaximumGradientSteps, sc.Input[GradientStepsInput].GetInt()));
	const double fieldScale = sc.Input[FieldScaleInput].GetFloat();
	const int inner = sc.Input[InnerTransparencyInput].GetInt();
	const int outer = sc.Input[OuterTransparencyInput].GetInt();
	const COLORREF magnetColor = sc.Input[MagnetColorInput].GetColor();
	const COLORREF wallColor = sc.Input[WallColorInput].GetColor();
	const int drawBehind = sc.Input[DrawBehindInput].GetYesNo();
	const size_t requiredSlots = selection.frozenNodes.size() * DrawingsPerNode;
	state.drawingNumbers.resize((std::max)(state.drawingNumbers.size(), requiredSlots), -1);
	std::vector<bool> used(state.drawingNumbers.size(), false);
	if (labelsOnly)
		for (size_t slot = 0; slot < requiredSlots; ++slot)
			used[slot] = slot % DrawingsPerNode < LabelSlot && state.drawingNumbers[slot] != -1;
	bool successful = true;

	auto useDrawing = [&](size_t slot, s_UseTool& drawing)
	{
		drawing.ChartNumber = sc.ChartNumber;
		drawing.Region = sc.GraphRegion;
		drawing.AddMethod = UTAM_ADD_OR_ADJUST;
		drawing.LineNumber = state.drawingNumbers[slot];
		if (sc.UseTool(drawing) == 0)
		{
			successful = false;
			return;
		}
		state.drawingNumbers[slot] = drawing.LineNumber;
		used[slot] = true;
	};

	for (size_t nodeIndex = 0; !labelsOnly && nodeIndex < selection.frozenNodes.size(); ++nodeIndex)
	{
		const Node& node = selection.frozenNodes[nodeIndex];
		const size_t firstSlot = nodeIndex * DrawingsPerNode;
		const double price = ConvertPrice(node.strike, selection.ticker, selection.payload.spread, selection.payload.ratio);
		const double halfWidth = FieldHalfWidth(node, selection, fieldScale);
		const COLORREF color = node.isMagnet ? magnetColor : wallColor;
		const int adjustedInner = InnerTransparency(node, inner);
		if (!std::isfinite(halfWidth) || halfWidth <= 0)
			continue;

		for (int side = 0; side < 2; ++side)
		{
			const double direction = side == 0 ? 1.0 : -1.0;
			for (int step = 0; step < steps; ++step)
			{
				const double ratio = static_cast<double>(step) / steps;
				s_UseTool band;
				band.Clear();
				band.DrawingType = DRAWING_RECTANGLE_EXT_HIGHLIGHT;
				band.BeginIndex = 0;
				band.EndIndex = (std::max)(1, lastIndex);
				band.ExtendLeft = 1;
				band.ExtendRight = 1;
				band.BeginValue = static_cast<float>(price + direction * halfWidth * ratio);
				band.EndValue = static_cast<float>(price + direction * halfWidth * (step + 1.0) / steps);
				band.Color = color;
				band.SecondaryColor = color;
				band.TransparencyLevel = static_cast<int>(adjustedInner + (outer - adjustedInner) * ratio);
				band.LineWidth = 0;
				band.NoVerticalOutline = 1;
				band.DrawUnderneathMainGraph = drawBehind;
				useDrawing(firstSlot + side * MaximumGradientSteps + step, band);
			}
		}

		for (int side = 0; side < 2; ++side)
		{
			const double direction = side == 0 ? 1.0 : -1.0;
			for (int step = side == 0 ? 0 : 1; step <= steps; ++step)
			{
				const double ratio = static_cast<double>(step) / steps;
				const int transparency = static_cast<int>(adjustedInner + (outer - adjustedInner) * ratio);
				s_UseTool line;
				line.Clear();
				line.DrawingType = DRAWING_HORIZONTALLINE;
				line.BeginIndex = lastIndex;
				line.BeginValue = static_cast<float>(price + direction * halfWidth * ratio);
				line.Color = BlendColor(color, background, transparency);
				line.LineWidth = sc.Input[GradientWidthInput].GetInt();
				line.LineStyle = LINESTYLE_SOLID;
				line.DisplayHorizontalLineValue = 0;
				line.DrawUnderneathMainGraph = drawBehind;
				useDrawing(firstSlot + GradientLineSlot + side * (MaximumGradientSteps + 1) + step, line);
			}
		}
	}

	const SubgraphLineStyles centerStyle = sc.Input[CenterStyleInput].GetIndex() == 1 ? LINESTYLE_DASH
		: sc.Input[CenterStyleInput].GetIndex() == 2 ? LINESTYLE_DOT : LINESTYLE_SOLID;
	const int fontSizes[] = { 7, 9, 11, 14, 18 };
	const int fontIndex = (std::max)(0, (std::min)(4, static_cast<int>(sc.Input[LabelSizeInput].GetIndex())));
	const int labelOffset = sc.Input[LabelOffsetInput].GetInt();
	const int labelAlign = static_cast<int>(sc.Input[LabelAlignInput].GetIndex());
	std::vector<s_UseTool> labels;
	for (size_t nodeIndex = 0; nodeIndex < selection.frozenNodes.size(); ++nodeIndex)
	{
		const Node& node = selection.frozenNodes[nodeIndex];
		const size_t firstSlot = nodeIndex * DrawingsPerNode;
		const double price = ConvertPrice(node.strike, selection.ticker, selection.payload.spread, selection.payload.ratio);
		const COLORREF color = node.isMagnet ? magnetColor : wallColor;
		if (!labelsOnly && sc.Input[ShowCenterInput].GetYesNo())
		{
			s_UseTool center;
			center.Clear();
			center.DrawingType = DRAWING_HORIZONTALLINE;
			center.BeginIndex = lastIndex;
			center.BeginValue = static_cast<float>(price);
			center.Color = BlendColor(color, background, 20);
			center.LineWidth = sc.Input[CenterWidthInput].GetInt();
			center.LineStyle = centerStyle;
			center.DisplayHorizontalLineValue = 0;
			center.DrawUnderneathMainGraph = drawBehind;
			useDrawing(firstSlot + CenterLineSlot, center);
		}
		if (sc.Input[ShowLabelsInput].GetYesNo())
		{
			s_UseTool label;
			label.Clear();
			label.DrawingType = DRAWING_TEXT;
			if (labelOffset > 0)
				label.BeginDateTime = -(labelOffset + 1);
			else
				label.BeginIndex = (std::max)(0, lastIndex + labelOffset);
			label.BeginValue = static_cast<float>(price);
			label.Text = sc.FormatGraphValue(price, sc.BaseGraphValueFormat);
			label.Text += node.isMagnet ? " MAGNET" : " WALL";
			if (!node.label.empty())
			{
				label.Text += "\n";
				label.Text += node.label.c_str();
			}
			if (sc.Input[ShowDetailsInput].GetYesNo() && !node.details.empty())
			{
				label.Text += "\n";
				label.Text += node.details.c_str();
			}
			label.MultiLineLabel = 1;
			label.TextAlignment = DT_CENTER | DT_VCENTER;
			label.Color = RGB(255, 255, 255);
			label.FontBackColor = BlendColor(color, background, InnerTransparency(node, inner) + 10);
			label.TransparentLabelBackground = 0;
			label.FontSize = fontSizes[fontIndex];
			label.FontBold = 0;
			label.DrawUnderneathMainGraph = 0;
			ConfigureLineLabel(label, labelAlign);
			if (labelAlign == 1 && sc.GetChartTextFontFaceName != nullptr)
				label.FontFace = sc.GetChartTextFontFaceName();
			labels.push_back(label);
		}
	}

	std::vector<s_UseTool> connectors(labels.size());
	if (labelAlign == 1)
		LayoutLeftEdgeLabels(sc, labels, connectors, state.refreshStatusSpace);
	for (size_t nodeIndex = 0; nodeIndex < connectors.size(); ++nodeIndex)
		if (connectors[nodeIndex].DrawingType == DRAWING_LINE)
			useDrawing(nodeIndex * DrawingsPerNode + ConnectorSlot, connectors[nodeIndex]);
	for (size_t nodeIndex = 0; nodeIndex < labels.size(); ++nodeIndex)
		useDrawing(nodeIndex * DrawingsPerNode + LabelSlot, labels[nodeIndex]);

	for (size_t slot = 0; slot < state.drawingNumbers.size(); ++slot)
	{
		if (!used[slot] && state.drawingNumbers[slot] != -1)
		{
			sc.DeleteACSChartDrawing(sc.ChartNumber, TOOL_DELETE_CHARTDRAWING, state.drawingNumbers[slot]);
			state.drawingNumbers[slot] = -1;
		}
	}
	state.drawingNumbers.resize(requiredSlots);
	return successful;
}
}

SCSFExport scsf_TLADe_Quantum_Field_Ladder(SCStudyInterfaceRef sc)
{
	using namespace TLADeQuantum;
	if (sc.SetDefaults)
	{
		sc.GraphName = "TLADe Quantum Field Ladder";
		sc.StudyDescription = R"QUANTUMDOC(
<div id="quantum-documentation" style="line-height:1.5;overflow-wrap:anywhere;">
<p><strong>TLADe Quantum Field Ladder</strong> displays imported TLADe levels as horizontal magnet/wall fields, with a stronger center fading toward the outer edges. This Sierra Chart study is based on the Quantum Field Ladder v6.11.0 rendering and frozen-selection behavior, with additional API fetching, label placement, and data-status controls.</p>
<p>It is a visualization of supplied data, not an options-chain calculator or an automated trading system. A magnet/wall designation is model context, not a guarantee that price will attract, reject, hold, or break. The study does not place orders.</p>
<p><strong>Two different data modes:</strong> Manual Quantum Data uses the ranges, intensities, and roles in the terminal's Quantum export. TLADe API (GEX-Derived Fields) builds fields from the compact GEX endpoint using this adapter's rules. API mode is not an exact reproduction of the manual Quantum export. See <a href="#api-mode">API Data and Limitations</a>.</p>
<p><a href="#quick-start">Quick Start</a> | <a href="#reading-chart">Reading the Chart</a> | <a href="#frozen-selection">Frozen Selection</a> | <a href="#input-reference">Every Input</a> | <a href="#api-refresh">Refresh Schedule and Status</a> | <a href="#manual-format">Manual Data Format</a> | <a href="#troubleshooting">Troubleshooting</a> | <a href="#sierra-settings">Sierra Settings</a> | <a href="#privacy">Privacy and Distribution</a></p>

<h3 id="quick-start">Quick Start</h3>
<h4>Automatic API Data</h4>
<ol>
<li>Add <strong>TLADe Quantum Field Ladder</strong> through Analysis &gt; Studies &gt; Add Custom Study. Keep Based On set to Main Price Graph and Chart Region set to 1.</li>
<li>Set <strong>In:1 Display Ticker</strong> to the price scale of the chart. For an NQ or MNQ futures chart select NQ; for ES or MES select ES. The study does not infer the API family from the chart symbol.</li>
<li>Set <strong>In:23 Data Source</strong> to <strong>TLADe API (GEX-Derived Fields)</strong>.</li>
<li>Enter your own TLADe key in <strong>In:24</strong>. Keep the key and the saved Chartbook private.</li>
<li>Set <strong>In:21 Exposure Filter = Yes</strong> and choose the minimum in <strong>In:22</strong>. The default <strong>Automatic (Data Source)</strong> basis uses reported API GEX in API mode, so no extra basis change is needed. Leave In:21 = No to disable filtering. Manual mode continues to use combined Call/Put exposure.</li>
<li>Choose <strong>In:7 Max Levels</strong>: 20 keeps up to 10 per side; 30 keeps up to 15 per side. Use 60 for broader coverage, accepting more drawings and labels.</li>
<li>Leave <strong>In:27 Automatic Session Refreshes = Yes</strong> and <strong>In:28 Show Refresh Status = Yes</strong>. Click Apply or OK, then check the top-left status and Window &gt; Message Log.</li>
<li>Use <strong>In:26 API Band Width</strong> to adjust the glow. On an ES/NQ chart with a 0.25-point tick, the default 12 ticks and Field Scale 0.5 produce a full displayed width of only 1.5 points. A narrow glow is not necessarily missing data.</li>
</ol>
<h4>Manual Quantum Export</h4>
<ol>
<li>In the TLADe terminal, copy the <strong>Quantum</strong> export for the required instrument and session. The GEX <code>S:...|L:...|P:...</code> export is a different format.</li>
<li>Set <strong>In:23 Data Source = Manual Quantum Data</strong>.</li>
<li>Paste the complete Quantum string into the <strong>Quantum Data</strong> text input at the bottom of the input list, including its <code>S:</code> header when present.</li>
<li>Set Display Ticker correctly and click Apply or OK. Manual mode does not make API requests.</li>
<li>Paste a new export when you want new market data. Use Recenter Now when you only want to reselect from the data already loaded.</li>
</ol>
<p><strong>Applying settings:</strong> Apply keeps the dialog open; OK applies and closes it. Unapplied edits have no effect on the chart. Sierra can perform a full recalculation when settings are applied, which also resets the frozen reference price.</p>

<h3 id="reading-chart">Reading the Chart</h3>
<ul>
<li><strong>Cyan MAGNET:</strong> the node is being rendered as an attractor. Its color is configurable in In:12.</li>
<li><strong>Magenta WALL:</strong> the node is being rendered as a repulsor. Its color is configurable in In:13. CW means Call Wall and PW means Put Wall; these codes alone do not determine a manual node's magnet/wall role.</li>
<li><strong>Center line:</strong> marks the imported strike after Display Ticker conversion. Field width and transparency do not move this center price.</li>
<li><strong>Gradient:</strong> a visual band around the center. Higher intensity makes the inner region more opaque. Width is controlled separately by the supplied manual range or the API Band Width, followed by Field Scale.</li>
<li><strong>Label:</strong> begins with the displayed price and MAGNET/WALL. API labels also retain the original supplied level name. Imported details appear underneath when In:17 is enabled.</li>
<li><strong>Left Edge connector:</strong> links a displaced label to its actual price. Packing a label lower on the screen does not move its field or center line.</li>
<li><strong>Zero Gamma (ZG):</strong> manual mode honors the supplied type, intensity, and range, including a type-0, intensity-0, zero-width pivot. API mode currently treats ZG as an intensity-3 magnet with the configured API width. A zero API magnitude on ZG is hidden as a placeholder; it is not presented as measured wall GEX.</li>
</ul>
<p><strong>Not part of this study:</strong> green/red GEX profile histograms, BOS, AVWAP, session boxes, and GEX confluence zones belong to the separate TLADe GEX Levels study. Quantum ignores the compact <code>P:</code> profile section.</p>

<h3 id="frozen-selection">Frozen Selection: Why a Loaded Level Can Be Missing</h3>
<p>The ladder is a selected subset of loaded data, not necessarily every level returned by TLADe. When selection is rebuilt, the study:</p>
<ol>
<li>Applies the optional exposure filter. The default automatic basis uses reported GEX in API mode and combined Call/Put amounts in manual mode. ZG is exempt from that filter.</li>
<li>Converts prices to the selected display scale and splits nodes above versus at-or-below the chart's current last-bar price.</li>
<li>Sorts each side by intensity, strongest first. Equal-intensity nodes keep their incoming order.</li>
<li>Keeps up to <code>ceil(Max Levels / 2)</code> nodes from each side, then freezes that selection and its reference price.</li>
</ol>
<p>For Max Levels 20, the allowance is 10 above and 10 below. For 30 it is 15 per side; for 60 it is 30 per side. Odd values round up: 5 can show up to 6 nodes. Spare places on one side are not transferred to the other side. A level very near price can still be excluded by its rank. ZG can occupy a selection slot; its exposure-filter exemption does not guarantee it is always selected.</p>
<p><strong>Price ticks and new bars do not recenter.</strong> A successful API refresh with an identical payload also does not recenter. A wall that was outside the allowance above the old reference does not automatically enter the below-price list when price crosses it.</p>
<p><strong>To reselect:</strong> set <a href="#input-19">In:19 Recenter Now</a> to Yes and click Apply. It resets to No after processing. Increasing Max Levels also rebuilds selection. New data, relevant selection settings, or Chart &gt; Recalculate rebuild it as well.</p>
<p><strong>Example:</strong> a wall ranked 15th above the load price is excluded with Max Levels 20. Max Levels 30 includes that rank. Alternatively, if price has moved above the wall, Recenter may bring it into the below-price allowance. This does not require another API request.</p>

<h3 id="input-reference">Input Reference</h3>
<p>The numbers match Sierra's <strong>In:</strong> labels. Defaults below are the defaults in this build, not necessarily the values saved in your Chartbook or custom study defaults.</p>

<h4 id="input-1">In:1 - Display Ticker (this chart's instrument)</h4>
<p><strong>Default: ES.</strong> Options: ES, SPX, SPY, NQ, NDX, QQQ. This controls both price conversion and, in API mode, which instrument family is requested. ES/SPX/SPY request SPX data; NQ/NDX/QQQ request NDX data. Selecting ES on an NQ chart can request the wrong dataset even though both futures selections use identity price conversion.</p>
<ul>
<li><strong>ES or NQ:</strong> displayed price = imported futures price.</li>
<li><strong>SPX or NDX:</strong> displayed price = imported futures price minus spread.</li>
<li><strong>SPY:</strong> displayed price = (imported futures price minus spread) / R, the SPX/SPY ratio from the data string (about 10.03), used when it is between 9 and 12; otherwise / 10.</li>
<li><strong>QQQ:</strong> displayed price = (imported futures price minus spread) / R, the NDX/QQQ ratio from the data string (about 41.1), used when it is between 35 and 48; otherwise / 40.</li>
</ul>
<p>The ratio travels in the data string as <code>R:</code>, right after <code>S:</code>, in both API and manual mode. It is frozen once a day, like the spread, so levels do not move during the session. A string without <code>R:</code> (an older one) keeps the original divisors 10 and 40. Do not pre-convert a futures-coordinate export and then convert it again with this input.</p>

<h4 id="input-2">In:2 - Futures-Cash Spread Override</h4>
<p><strong>Default: 0.</strong> Fallback basis used when there is no valid <code>S:</code> header. A valid S value takes precedence, including zero or a negative value. The spread represents the active futures/cash basis, not a tick count. It does not shift ES/NQ identity prices. A malformed manual header is reported in the Message Log; a malformed API response is rejected.</p>

<h4 id="input-3">In:3 - Gradient Smoothness</h4>
<p><strong>Default: 10; range: 6-16.</strong> Number of steps per half of each field. Higher values make the fade smoother but create more drawing objects. This does not change prices, selection, or total field width. Reduce it if a chart with many nodes is slow.</p>

<h4 id="input-4">In:4 - Field Scale</h4>
<p><strong>Default: 0.5; range: 0.2-1.5.</strong> Multiplies the full field width after price conversion. 0.5 shows half the input range; 1.0 shows the full range; 1.5 shows one-and-a-half times the range. The field remains centered on its strike. This is a visual width adjustment, not GEX strength or a probability.</p>

<h4 id="input-5">In:5 - Show Center Line</h4>
<p><strong>Default: Yes.</strong> Draws the horizontal strike line through each selected node. No removes that line but leaves gradient fills and labels enabled according to their own settings. A zero-width manual pivot has no filled band, so its center line and label are particularly useful.</p>

<h4 id="input-6">In:6 - Label Offset (bars; Study Default alignment)</h4>
<p><strong>Default: 0; range: -50 to 50.</strong> Moves labels relative to the last bar only when In:20 is Study Default. Negative values move left; positive values move into right-side fill space; zero anchors at the last bar. Other alignments use their own anchor and ignore this offset. Provide enough chart fill space for a positive offset.</p>

<h4 id="input-7">In:7 - Max Levels to Show (half per side; odd values round up)</h4>
<p><strong>Default: 20; range: 5-60.</strong> Sets the frozen selection allowance, not the number of lines guaranteed to be visible in the current viewport. 20 = up to 10 per side, 30 = 15 per side, 60 = 30 per side. Filters, missing nodes on one side, and off-screen prices can reduce what you see. Increasing this can restore lower-ranked levels but adds visual clutter and drawing work. See <a href="#frozen-selection">Frozen Selection</a>.</p>

<h4 id="input-8">In:8 - Gradient Line Width</h4>
<p><strong>Default: 1; range: 1-4.</strong> Thickness of the horizontal lines that form the fade. Larger values make fields appear denser, especially when many price levels occupy only a few pixels. It does not widen the field in price points; use In:4 or In:26 for that.</p>

<h4 id="input-9">In:9 - Center Line Width</h4>
<p><strong>Default: 2; range: 1-5.</strong> Thickness of the strike center line. Applies only while In:5 is Yes. It is independent of the gradient line width.</p>

<h4 id="input-10">In:10 - Center Line Style</h4>
<p><strong>Default: Solid.</strong> Options: Solid, Dashed, Dotted. Changes the center line only; gradient lines remain solid.</p>

<h4 id="input-11">In:11 - Label Size</h4>
<p><strong>Default: Small.</strong> Options: Tiny, Small, Normal, Large, Huge, using font sizes 7, 9, 11, 14, and 18 respectively. Larger details can require more columns or fail to fit. This input affects level labels, not the refresh box, which automatically uses a compact font.</p>

<h4 id="input-12">In:12 - Magnet Color (Attractor)</h4>
<p><strong>Default: cyan, RGB 34, 211, 238.</strong> Base color for magnet gradients, center lines, and label backgrounds. Actual opacity depends on intensity and transparency. Changing color does not change a node's role.</p>

<h4 id="input-13">In:13 - Wall Color (Repulsor)</h4>
<p><strong>Default: magenta, RGB 244, 114, 182.</strong> Base color for wall gradients, center lines, and label backgrounds. Both call-wall and put-wall codes use this color when their Quantum role is WALL; this is not the green/red sign coloring of a GEX profile.</p>

<h4 id="input-14">In:14 - Outer Transparency</h4>
<p><strong>Default: 95; range: 80-98.</strong> Transparency at the field's outer edges. Higher means more transparent, not stronger. Lower values make the edge easier to see. The fade interpolates from the intensity-adjusted inner transparency to this value.</p>

<h4 id="input-15">In:15 - Inner Transparency</h4>
<p><strong>Default: 40; range: 10-70.</strong> Base transparency near the center. Higher-intensity nodes receive an opacity boost, so the effective center transparency can be lower than this setting. Native fills use transparency; line and label colors approximate it by blending against the chart background.</p>

<h4 id="input-16">In:16 - Show Labels</h4>
<p><strong>Default: Yes.</strong> Shows prices, MAGNET/WALL designations, supplied API names, and any enabled details. No hides level labels and their layout connectors without hiding the fields, center lines, refresh status, or data warnings.</p>

<h4 id="input-17">In:17 - Show Imported Details on Labels</h4>
<p><strong>Default: Yes.</strong> Adds the imported description below each label. Tilde characters (<code>~</code>) become line breaks. These are visible label details, not hover tooltips. They can include Call/Put amounts, ranges, or hold/break text when supplied; the study does not create missing probabilities. No keeps the shorter label and API name. Filtering can still read the imported Call/Put data even when its text is hidden.</p>
<p>Imported detail text is not rewritten for Display Ticker or Field Scale. A detail may quote the original futures strike or unscaled Lagrange range while the label's main price and field have been converted/scaled.</p>

<h4 id="input-18">In:18 - Draw Fields and Center Lines Behind Candles</h4>
<p><strong>Default: Yes.</strong> Requests that gradient fills and lines draw behind the main price bars. No requests that they draw above the bars. Labels and status text remain foreground drawings. Sierra's chart-wide drawing/graph ordering can also affect the result.</p>

<h4 id="input-19">In:19 - Recenter Now (resets to No)</h4>
<p><strong>Default: No.</strong> One-shot selection action. Set Yes and click Apply to select the strongest nodes on each side of the current chart price from the data already loaded. The input returns to No. It does not fetch fresh API data, change supplied strikes, or move the chart scale. Use this when price has moved far from the original load reference.</p>

<h4 id="input-20">In:20 - Line Label Horizontal Align</h4>
<p><strong>Default: Left Edge.</strong> Controls the label anchor, not the price of the field.</p>
<ul>
<li><strong>Study Default:</strong> label centered at the last bar plus In:6 Label Offset.</li>
<li><strong>Left Edge:</strong> labels packed in columns near the visible chart's left edge. Layout is recalculated on scroll/zoom, reserves room for the status box, and draws connectors when a label has to move vertically.</li>
<li><strong>Left:</strong> the label's right edge is anchored at the start of forward fill space, so text extends left of that anchor.</li>
<li><strong>Right:</strong> the label's left edge is anchored at the start of forward fill space, so text extends right.</li>
<li><strong>Right Edge:</strong> right-aligned at the visible chart's right edge.</li>
<li><strong>Center:</strong> horizontally centered in the visible chart.</li>
</ul>
<p>Only Left Edge performs collision-aware column packing. Labels that cannot fit are hidden while their fields remain. Other alignments can overlap at nearby prices. Reduce Label Size or disable imported details when space is limited.</p>

<h4 id="input-21">In:21 - Enable Exposure Filter</h4>
<p><strong>Default: No.</strong> When Yes, a non-ZG node must pass the minimum in In:22 before per-side selection. With the default automatic basis, these two controls are sufficient: API mode compares the reported API GEX magnitude, while manual mode compares <code>abs(Call) + abs(Put)</code>. API filtering does not require separate Call/Put fields unless you deliberately choose the strict combined basis in In:39. ZG remains exempt.</p>

<h4 id="input-22">In:22 - Min Filter Amount (M)</h4>
<p><strong>Default: 50; range: 0-1,000,000,000 million.</strong> Threshold used only with In:21 = Yes. This retains the saved value of the former Min Combined Call + Put input. In combined mode, Call 30M and Put -20M combine to 50M and pass a 50M minimum. K, M, and B suffixes mean thousands, millions, and billions; an unsuffixed 1000000 means 1M. In API GEX mode, a reported magnitude of 20M passes a minimum of 20; 19.9M does not. This value is not percent or intensity.</p>
<p>A minimum of 0 still requires both amounts in combined mode. API GEX mode uses the already validated numeric API magnitude, including a real zero. Turn In:21 off to disable filtering altogether.</p>

<h4 id="input-23">In:23 - Data Source</h4>
<p><strong>Default: Manual Quantum Data.</strong> Manual mode reads the Quantum Data text box and makes no API requests. TLADe API (GEX-Derived Fields) reads <code>indicatorData</code> using In:24 and ignores the manual text while selected. Switching to Manual is an explicit fallback; API failures do not silently mix manual and API data. The manual text remains available when switching back.</p>

<h4 id="input-24">In:24 - TLADe API Key (Private - Saved in Chartbook)</h4>
<p><strong>Default: empty.</strong> Your own authorized TLADe key. It is sent in the HTTPS authentication header, not in the request URL or study status. A missing/invalid key prevents a request. Changing the key or instrument family clears the old API dataset to avoid showing another key/family's cached levels. The input is saved in the Chartbook; do not share it in a screenshot, chat, source file, or distributed Chartbook.</p>

<h4 id="input-25">In:25 - API Refresh Now (Resets to No)</h4>
<p><strong>Default: No.</strong> One-shot request action. In API mode, set Yes and click Apply to queue a refresh. The input returns to No. There is no local one-minute delay or hourly/daily request cap. If a request is already running, the refresh waits for it to finish. Requests are paused in replay, and a cooldown after an actual HTTP 429 response is still respected.</p>
<p><strong>API Refresh is not Recenter.</strong> An identical response updates fetch status but keeps the frozen selection. Use In:19 to reselect from unchanged data.</p>

<h4 id="input-26">In:26 - API Band Width (Chart Ticks, Before Field Scale)</h4>
<p><strong>Default: 12; range: 1-2000.</strong> Full raw band width for API-derived fields in the chart's tick units. It is ignored in Manual mode. On futures charts, full displayed width = <code>ticks * chart tick size * Field Scale</code>. At tick size 0.25 and scale 0.5, 12 ticks gives 1.5 points; 120 ticks gives 15 points. This is a display choice, not a recovered manual Lagrange-range formula. The ETF conversion divisor is accounted for so this input remains in chart ticks.</p>

<h4 id="input-27">In:27 - API Automatic Session Refreshes</h4>
<p><strong>Default: Yes.</strong> Enables the six scheduled checks listed below. No disables session checks, but a startup request and explicit API Refresh Now remain available. There is no extra hourly polling input in Quantum. The chart must be open and the study processing updates; missed sessions are not replayed as a burst of historical requests.</p>

<h4 id="input-28">In:28 - Show Refresh Status Box (Top Left)</h4>
<p><strong>Default: Yes.</strong> Shows mode, request result, Last OK fetch time, and Last check name. No hides normal refresh status without stopping API requests. Exposure warnings can still appear when In:38 is Yes. The box uses a compact auto-fitting font and can hide if the chart region is too small.</p>

<h4 id="input-29">In:29 - Refresh Status Box Top Offset (Pixels)</h4>
<p><strong>Default: 8; range: 4-1000 pixels.</strong> Moves the status/warning box down from the top of the study region. Increase it to place Quantum below another study's status box. Left Edge level labels reserve room below it. A large offset on a short chart can leave no room to display the box.</p>

<h4 id="input-38">In:38 - Show Data Warnings (Top Left)</h4>
<p><strong>Default: Yes.</strong> Shows an amber warning and exclusion count when the exposure filter cannot use missing/invalid Call/Put amounts. It uses the same top-left location and offset as refresh status, but can remain visible while In:28 is No. No hides the warning only; excluded nodes remain excluded. An HTTP error retains its red status color when a warning is also present.</p>
<p><strong>Why the numbering skips:</strong> In:30 through In:37 are retired, hidden inputs. They formerly controlled the removed GEX profile display. They are not used, and later inputs keep their original numbers so saved Chartbooks are not reinterpreted.</p>

<h4 id="input-39">In:39 - API Exposure Filter Basis</h4>
<p><strong>Default: Automatic (Data Source).</strong> This is an advanced override; normally use In:21 and In:22 only. Automatic uses reported GEX magnitude in API mode and combined Call/Put exposure in manual mode. <strong>API GEX Magnitude</strong> explicitly selects the same API comparison. <strong>Combined Call + Put (Strict)</strong> instead requires both separate amounts in API descriptions and excludes nodes when either is missing or invalid.</p>
<p>The status box identifies API filtering as API GEX; no separate Call/Put data is invented. Switching the basis recalculates selection from the already loaded response without another request. Manual mode always keeps combined Call/Put filtering. The former default option at index 0 now means Automatic so existing API studies can use In:21 and In:22 directly; the previously selected API GEX option at index 1 keeps its meaning. Choose the new strict option only when you intentionally need that comparison.</p>

<h3 id="api-refresh">Refresh Schedule and Status</h3>
<p>Scheduled checks use <strong>Eastern Time (America/New_York), with daylight-saving changes handled automatically</strong>, independently of the chart's time zone. They are request times, not guaranteed vendor publication times.</p>
<ul>
<li><strong>Asia:</strong> 18:02 ET.</li>
<li><strong>Europe:</strong> 03:16 ET.</li>
<li><strong>Pre-open:</strong> 08:01 ET.</li>
<li><strong>RTH Open:</strong> 09:31 ET.</li>
<li><strong>Op. Range:</strong> 10:31 ET.</li>
<li><strong>Charm Kick:</strong> 13:01 ET.</li>
</ul>
<p>Startup/manual requests are additional. Each request retrieves the current endpoint response, not a named historical session. A matching cached response can be reused without a new request. Quantum has no local minute, hourly, or daily request caps. Automatic requests still follow the six scheduled checks, not a continuous polling loop. The vendor can enforce its own limits; requests from other charts, Sierra instances, and the separate GEX study can count toward the same vendor allowance.</p>
<p>Removing the local caps does not remove caching or change loaded levels. Changing the exposure-filter basis or threshold, and using Recenter Now, work from already loaded data without requiring a fetch. Manual refreshes can be sent as soon as the prior request completes, unless an actual HTTP 429 cooldown is active.</p>
<ul>
<li><strong>Manual data / No manual data:</strong> manual source selected; no API fetch is implied.</li>
<li><strong>Waiting / Refresh queued / Fetching:</strong> data has not arrived yet, a request is pending, or the worker is running.</li>
<li><strong>Refreshed (changed):</strong> a valid response differs from the previous accepted data. Selection rebuilds using the current chart reference.</li>
<li><strong>Refreshed (same data):</strong> the request succeeded but its body is unchanged. It does not reset the frozen selection.</li>
<li><strong>Shared data:</strong> a matching response was reused from another Quantum instance. Its original successful fetch time is preserved.</li>
<li><strong>Key required / Invalid key:</strong> correct In:24 privately.</li>
<li><strong>HTTP 401/403:</strong> check key access/subscription with TLADe. Automatic requests stop after authorization denial; after resolving access, use API Refresh Now or update the key.</li>
<li><strong>HTTP 429:</strong> the vendor rejected a request as rate limited. Only after this actual response, Quantum applies a 15-minute retry cooldown and shows its countdown. This is separate from the removed proactive local caps. Existing data stays available.</li>
<li><strong>Other HTTP errors / Refresh failed:</strong> read Window &gt; Message Log for the specific cause. The previous accepted API data is retained and may be stale.</li>
<li><strong>Paused (replay):</strong> live requests and live response acceptance are paused, including while replay itself is paused.</li>
<li><strong>Auto checks: Off:</strong> scheduled checks are disabled; this does not disable startup/manual requests.</li>
</ul>
<p><strong>Last OK is fetch time, not publication time.</strong> The compact endpoint has no publication timestamp/session identifier. Last check is the local schedule slot associated with that fetch, not proof that the vendor published that session. A startup or manual request also receives the current local slot label.</p>

<h3 id="api-mode">API Data and Limitations</h3>
<p>The endpoint is <code>indicatorData?ticker=SPX</code> or <code>indicatorData?ticker=NDX</code>. There is no separate Quantum API request in this study, and Quantum does not read <code>snapshotApi</code>. The adapter consumes the spread from S and the level records from L.</p>
<ul>
<li>Only ZG, CW, and PW become API nodes. Other GEX codes such as MP, EH/EL, EMR, DF, and CM are not added to the Quantum ladder.</li>
<li>Imported API strike coordinates and magnitudes are retained. The current adapter does not round strikes to whole points before drawing.</li>
<li>CW/PW intensity currently uses linear scaling: <code>5 * node magnitude / largest accepted-node magnitude</code>. ZG uses intensity 3. These values are visual strength, not probabilities.</li>
<li>ZG is mapped to MAGNET. A CW/PW is a MAGNET when its strike is listed in the <code>B:</code> row of the P profile, the engine's own verdict (neither side over 60%); otherwise it is a WALL. The classification is read, never recomputed.</li>
<li>API width comes from In:26, not from manual <code>rangeUp/rangeDown</code>. All API nodes start with the same configured raw width.</li>
<li>The automatic API filter uses the reported GEX magnitude and labels it as such. This does not reconstruct the separate Call/Put amounts or treat a C/P ratio as exposure. Strict combined mode remains an optional advanced override.</li>
<li>From the P profile only the <code>B:</code> magnet row is read; other profile rows are ignored. EOW/5DTE selection, complete Quantum range reconstruction, and exact manual-export parity are not implemented here.</li>
</ul>
<p><strong>For the manual export's supplied ranges and roles, use Manual Quantum Data.</strong> The terminal's export applies its own intensity quantization and supplies precomputed roles/bounds. Matching a screenshot with a wider API width is a visual approximation, not proof that those values were reconstructed. No missing role, range, or publication time is inferred from a chart image.</p>

<h3 id="manual-format">Manual Data Format and Field Calculations</h3>
<p>The separate Quantum Data text box accepts:</p>
<p><code>[S:spread|][R:ratio|]strike,intensity,type,rangeUp,rangeDown,wallType,tooltip;...</code></p>
<ul>
<li><strong>S:spread|:</strong> optional numeric futures/cash basis. A valid value overrides In:2.</li>
<li><strong>R:ratio|:</strong> optional index/ETF ratio for SPY/QQQ (see In:1).</li>
<li><strong>strike:</strong> field center in the export's futures coordinates.</li>
<li><strong>intensity:</strong> numeric strength from 0 through 5. Imported as supplied.</li>
<li><strong>type:</strong> 1 for MAGNET, 0 for WALL. A PW or CW code does not override this field.</li>
<li><strong>rangeUp / rangeDown:</strong> supplied upper/lower prices, with rangeUp not below rangeDown. Equal values produce no filled width.</li>
<li><strong>wallType:</strong> supplied code, commonly CW, PW, or ZG.</li>
<li><strong>tooltip:</strong> optional imported description. Use a tilde for a displayed newline; semicolons separate records.</li>
</ul>
<p><strong>Synthetic example only, not market data:</strong></p>
<pre style="white-space:pre-wrap;overflow-wrap:anywhere;">S:50|7800,4,0,7815,7785,CW,Wall (Call Wall)~Call: 60M~Put: -20M;7760,0,0,7760,7760,ZG,Zero Gamma</pre>
<p>With Display Ticker ES and Field Scale 0.5, the first example's raw width is <code>7815 - 7785 = 30</code> points. Its displayed full width is 15 points, centered at 7800. The formula is <code>half width = (converted rangeUp - converted rangeDown) / 2 * Field Scale</code>; upper/lower visual edges are center plus/minus that half width. The ZG record has zero width but can retain its label and center line if selected.</p>
<p>For each node, the effective inner transparency is <code>max(10, Inner Transparency - int((intensity - 1) * 5))</code>. Transparency then fades toward Outer Transparency over Gradient Smoothness steps. Intensity affects opacity, not the imported width. Changing these visual controls does not recalculate option exposure.</p>
<p>Malformed manual records are skipped and counted in the Message Log. Clearing manual data removes the manual ladder. An invalid API response instead retains the last accepted API data. No example key or live licensed dataset is embedded in this documentation.</p>

<h3 id="troubleshooting">Troubleshooting</h3>
<h4>A Price Is in the Feed but Not on the Chart</h4>
<ol>
<li>Confirm Display Ticker and the selected Data Source. API mode ignores the manual text input.</li>
<li>Check In:21 and the minimum in In:22. With Automatic or API GEX Magnitude, reported API magnitudes below the minimum are excluded. If you explicitly selected Combined Call + Put (Strict) in In:39, switch back to Automatic unless both amounts are supplied. In:21 = No disables filtering regardless of the minimum.</li>
<li>Set In:19 Recenter Now to Yes and click Apply. An unchanged API refresh alone does not reselect.</li>
<li>Increase In:7 from 20 to 30 or 60. The allowance is per side of the frozen reference, not simply the first 20 prices in the viewport.</li>
<li>Check the chart's visible price range. A selected node can be off-screen.</li>
<li>If the line exists but its text is missing, check Show Labels and reduce Label Size/details. Left Edge packing hides labels that cannot fit.</li>
<li>Read Window &gt; Message Log for parsed-node counts, the frozen reference price, malformed records, and excluded exposure data.</li>
</ol>
<h4>The API Fields Look Like Thin Lines</h4>
<p>Check In:26, In:4, and the chart Tick Size. The default API width is intentionally small and is unrelated to the manual export's range. Increase API Band Width for a broader display; changing Gradient Line Width only thickens the drawing strokes. Zoom and transparency also affect apparent thickness.</p>
<h4>Manual and API Fields Differ</h4>
<p>Use the same instrument, source session, chart scale, selection reference, and exposure settings when comparing. Even then, this adapter's intensity, role, rounding, and width rules differ from the terminal export. Manual mode is the way to honor the supplied manual Quantum values. Do not paste the GEX L/P format into the manual Quantum input.</p>
<h4>Refreshing Does Not Change Anything</h4>
<p>Same data means the request succeeded with unchanged content. TLADe may not have published another dataset. Check Last OK, Auto checks, and any HTTP status or active HTTP 429 cooldown. Use Recenter for a different selection of the same data. Correct the computer clock if ET scheduling is wrong; no historical catch-up request is made for every missed slot.</p>
<h4>No Data, Old Data, or a Red Status</h4>
<p>Check the key privately, Data Source, instrument family, internet access, replay state, and the Message Log. API requests use a background worker and bounded timeouts; a request failure is not proof that levels disappeared from TLADe. Wait through rate limits. After authorization problems are resolved, use API Refresh Now. Switching to Manual and supplying a current export is an explicit fallback.</p>
<h4>Labels or Status Overlap</h4>
<p>Use Left Edge alignment, smaller labels, or fewer imported details. Increase In:29 to place Quantum's box below the GEX box on the same chart. In:28 hides normal status; In:38 separately hides warnings. Increasing the offset too far can hide the box on a short region. Increase the chart's width or height to make more room.</p>
<h4>Replay and Historical Charts</h4>
<p>API mode is not a historical snapshot backfill. Already loaded levels can remain visible in replay, but they may have been published after the replayed time. Do not treat that as a look-ahead-free backtest. For historical analysis, use a manually archived export from the correct time and verify its provenance.</p>

<h3 id="sierra-settings">Other Sierra Study Settings and Buttons</h3>
<ul>
<li><strong>Based On:</strong> leave Main Price Graph for normal use. Selection uses the chart's last-bar price; basing the study on another graph is not a way to request another API ticker.</li>
<li><strong>Chart Region / Scale:</strong> use Chart Region 1 and the chart's price scale for an overlay. An independent or shifted scale can visually separate drawings from the prices you expect.</li>
<li><strong>Short Name:</strong> optional identification in Sierra's lists/header; it does not change the data source or the MAGNET/WALL roles.</li>
<li><strong>Value Format:</strong> keep Inherited normally. Level label prices use the main chart's value format; changing a study's numeric formatting does not change its imported prices.</li>
<li><strong>Display As Main Price Graph:</strong> leave unchecked. Quantum is an overlay, not a replacement candlestick chart.</li>
<li><strong>Hide Study:</strong> removes this study's drawings and skips its normal processing while hidden. A request already in flight may finish. Hiding only the refresh box is different and does not pause fetching.</li>
<li><strong>Draw Study Underneath Main Price Graph:</strong> Sierra's general ordering setting. Use In:18 for this study's individual fields/center lines; labels/status are separately managed drawings.</li>
<li><strong>Subgraphs tab:</strong> the ladder is made from chart drawings rather than numeric subgraph plots. Set field colors, line styles, and sizes through Inputs.</li>
<li><strong>Alerts tab:</strong> there is no built-in Quantum touch/cross alert or per-level subgraph series in this implementation. The presence of Sierra's standard Alerts tab does not enable such alerts automatically.</li>
<li><strong>Include in Study Summary / Include in Spreadsheet:</strong> standard Sierra inclusion options. They do not export the drawn ladder into usable numeric level subgraphs.</li>
<li><strong>Protect with Password:</strong> Sierra's study-settings protection is not permission to share a Chartbook containing your API key.</li>
<li><strong>Standard Precedence / DLLName.FunctionName:</strong> Sierra's calculation order and study identity. They are not trading inputs; leave the normal study configuration in place.</li>
<li><strong>Apply / OK / Cancel:</strong> Apply saves edits and keeps the dialog open; OK saves and closes; Cancel discards edits not yet applied.</li>
<li><strong>Description / Help:</strong> Description opens this guide; Help opens Sierra's general help.</li>
<li><strong>Set Defaults / Reset Defaults:</strong> manage Sierra's saved/default settings. Use them only when intentionally changing/restoring defaults, not to refresh TLADe data.</li>
</ul>

<h3 id="privacy">Privacy, Distribution, and Updates</h3>
<p>Each user must authenticate with their own TLADe key and have the required access. Share study source/DLL only; do not embed a real key, redistribute licensed per-strike data, or share a populated Chartbook without removing private credentials and data. Clear keys before posting settings screenshots. The study does not grant API access or change the vendor's data license.</p>
<p>After a source update, rebuild the Quantum CPP through Sierra's Remote Build and reload/recalculate the study, then reopen Description. Quantum is self-contained and does not require the GEX study's separate JSON header. Saved input values normally remain in the Chartbook; rebuilding is not the same as resetting all inputs to defaults.</p>
<p><strong>Reference:</strong> <a href="https://github.com/tradelikeadealer/tlade-indicators/blob/main/final/tradingview/quantum_field_ladder.pine">Original TLADe Quantum Field Ladder Pine study</a>. This guide describes the current Sierra implementation, including the explicitly documented API adapter differences.</p>
</div>
)QUANTUMDOC";
		sc.GraphRegion = 0;
		sc.AutoLoop = 0;
		sc.UpdateAlways = 1;
		// TLADe, 28/10/2026: Sierra prints the input values next to the study name on the chart, and
		// they include the API key (In:24): every screenshot of the chart would publish it.
		sc.DisplayStudyInputValues = 0;
		sc.TextInputName = "Quantum Data (paste complete S:...|... string)";

		sc.Input[TickerInput].Name = "Display Ticker (this chart's instrument)";
		sc.Input[TickerInput].SetCustomInputStrings("ES;SPX;SPY;NQ;NDX;QQQ");
		sc.Input[TickerInput].SetCustomInputIndex(ES);
		sc.Input[SpreadInput].Name = "Futures-Cash Spread Override (used without a valid S: header)";
		sc.Input[SpreadInput].SetFloat(0);
		sc.Input[GradientStepsInput].Name = "Gradient Smoothness";
		sc.Input[GradientStepsInput].SetInt(10);
		sc.Input[GradientStepsInput].SetIntLimits(6, MaximumGradientSteps);
		sc.Input[FieldScaleInput].Name = "Field Scale";
		sc.Input[FieldScaleInput].SetFloat(0.5f);
		sc.Input[FieldScaleInput].SetFloatLimits(0.2f, 1.5f);
		sc.Input[ShowCenterInput].Name = "Show Center Line";
		sc.Input[ShowCenterInput].SetYesNo(1);
		sc.Input[LabelOffsetInput].Name = "Label Offset (bars; Study Default alignment)";
		sc.Input[LabelOffsetInput].SetInt(0);
		sc.Input[LabelOffsetInput].SetIntLimits(-50, 50);
		sc.Input[MaxLevelsInput].Name = "Max Levels to Show (half per side; odd values round up)";
		sc.Input[MaxLevelsInput].SetInt(20);
		sc.Input[MaxLevelsInput].SetIntLimits(5, 60);
		sc.Input[GradientWidthInput].Name = "Gradient Line Width";
		sc.Input[GradientWidthInput].SetInt(1);
		sc.Input[GradientWidthInput].SetIntLimits(1, 4);
		sc.Input[CenterWidthInput].Name = "Center Line Width";
		sc.Input[CenterWidthInput].SetInt(2);
		sc.Input[CenterWidthInput].SetIntLimits(1, 5);
		sc.Input[CenterStyleInput].Name = "Center Line Style";
		sc.Input[CenterStyleInput].SetCustomInputStrings("Solid;Dashed;Dotted");
		sc.Input[CenterStyleInput].SetCustomInputIndex(0);
		sc.Input[LabelSizeInput].Name = "Label Size";
		sc.Input[LabelSizeInput].SetCustomInputStrings("Tiny;Small;Normal;Large;Huge");
		sc.Input[LabelSizeInput].SetCustomInputIndex(1);
		sc.Input[MagnetColorInput].Name = "Magnet Color (Attractor)";
		sc.Input[MagnetColorInput].SetColor(RGB(34, 211, 238));
		sc.Input[WallColorInput].Name = "Wall Color (Repulsor)";
		sc.Input[WallColorInput].SetColor(RGB(244, 114, 182));
		sc.Input[OuterTransparencyInput].Name = "Outer Transparency";
		sc.Input[OuterTransparencyInput].SetInt(95);
		sc.Input[OuterTransparencyInput].SetIntLimits(80, 98);
		sc.Input[InnerTransparencyInput].Name = "Inner Transparency";
		sc.Input[InnerTransparencyInput].SetInt(40);
		sc.Input[InnerTransparencyInput].SetIntLimits(10, 70);
		sc.Input[ShowLabelsInput].Name = "Show Labels";
		sc.Input[ShowLabelsInput].SetYesNo(1);
		sc.Input[ShowDetailsInput].Name = "Show Imported Details on Labels";
		sc.Input[ShowDetailsInput].SetYesNo(1);
		sc.Input[DrawBehindInput].Name = "Draw Fields and Center Lines Behind Candles";
		sc.Input[DrawBehindInput].SetYesNo(1);
		sc.Input[RecenterInput].Name = "Recenter Now (resets to No)";
		sc.Input[RecenterInput].SetYesNo(0);
		sc.Input[LabelAlignInput].Name = "Line Label Horizontal Align";
		sc.Input[LabelAlignInput].SetCustomInputStrings("Study Default;Left Edge;Left;Right;Right Edge;Center");
		sc.Input[LabelAlignInput].SetCustomInputIndex(1);
		sc.Input[UseExposureFilterInput].Name = "Enable Exposure Filter";
		sc.Input[UseExposureFilterInput].SetYesNo(0);
		sc.Input[MinExposureInput].Name = "Min Filter Amount (M)";
		sc.Input[MinExposureInput].SetFloat(50.0f);
		sc.Input[MinExposureInput].SetFloatLimits(0.0f, 1000000000.0f);
		sc.Input[DataSourceInput].Name = "Data Source";
		sc.Input[DataSourceInput].SetCustomInputStrings("Manual Quantum Data;TLADe API (GEX-Derived Fields)");
		sc.Input[DataSourceInput].SetCustomInputIndex(0);
		sc.Input[ApiKeyInput].Name = "TLADe API Key (Private - Saved in Chartbook)";
		sc.Input[ApiKeyInput].SetString("");
		sc.Input[ApiRefreshInput].Name = "API Refresh Now (Resets to No)";
		sc.Input[ApiRefreshInput].SetYesNo(0);
		sc.Input[ApiBandWidthInput].Name = "API Band Width (Chart Ticks, Before Field Scale)";
		sc.Input[ApiBandWidthInput].SetInt(12);
		sc.Input[ApiBandWidthInput].SetIntLimits(1, 2000);
		sc.Input[ApiAutomaticInput].Name = "API Automatic Session Refreshes";
		sc.Input[ApiAutomaticInput].SetYesNo(1);
		sc.Input[ShowRefreshStatusInput].Name = "Show Refresh Status Box (Top Left)";
		sc.Input[ShowRefreshStatusInput].SetYesNo(1);
		sc.Input[RefreshStatusTopInput].Name = "Refresh Status Box Top Offset (Pixels)";
		sc.Input[RefreshStatusTopInput].SetInt(8);
		sc.Input[RefreshStatusTopInput].SetIntLimits(4, 1000);
		for (int retiredInput = RefreshStatusTopInput + 1; retiredInput < ShowDataWarningsInput; ++retiredInput)
			sc.Input[retiredInput].Name = "";
		sc.Input[ShowDataWarningsInput].Name = "Show Data Warnings (Top Left)";
		sc.Input[ShowDataWarningsInput].SetYesNo(1);
		sc.Input[ApiExposureBasisInput].Name = "API Exposure Filter Basis";
		sc.Input[ApiExposureBasisInput].SetCustomInputStrings("Automatic (Data Source);API GEX Magnitude;Combined Call + Put (Strict)");
		sc.Input[ApiExposureBasisInput].SetCustomInputIndex(AutomaticExposure);
		return;
	}

	sc.DisplayStudyInputValues = 0;   // TLADe: also on studies added before this build
	StudyState* state = static_cast<StudyState*>(sc.GetPersistentPointer(PersistentStateKey));
	if (sc.LastCallToFunction)
	{
		if (state != nullptr)
			ClearDrawings(sc, *state);
		delete state;
		sc.SetPersistentPointer(PersistentStateKey, nullptr);
		return;
	}
	if (state == nullptr)
	{
		state = new StudyState();
		sc.SetPersistentPointer(PersistentStateKey, state);
	}
	if (sc.IsFullRecalculation)
	{
		state->drawingNumbers.clear();
		state->refreshStatusNumber = -1;
		state->refreshStatusSpace = 0;
		state->lastDrawnBar = -1;
		state->selection.initialized = false;
	}
	if (sc.HideStudy || sc.ArraySize <= 0)
	{
		ClearDrawings(sc, *state);
		return;
	}
	if (sc.DownloadingHistoricalData)
		return;

	const int lastIndex = sc.ArraySize - 1;
	const double referencePrice = sc.BaseDataIn[SC_LAST][lastIndex];
	if (!std::isfinite(referencePrice))
		return;
	const bool recenter = sc.Input[RecenterInput].GetYesNo() != 0;
	const int ticker = static_cast<int>(sc.Input[TickerInput].GetIndex());
	const bool useApi = sc.Input[DataSourceInput].GetIndex() == 1;
	if (useApi && !state->api) state->api.reset(new ApiState());
	if (state->api)
	{
		SCInputRef keyInput = sc.Input[ApiKeyInput];
		const char* key = keyInput.UsesStringValue() && keyInput.StringValue != nullptr ? keyInput.StringValue : "";
		UpdateApi(sc, *state->api, useApi, key, ticker >= NQ,
			sc.Input[ApiAutomaticInput].GetYesNo() != 0, sc.Input[ApiRefreshInput].GetYesNo() != 0);
	}
	if (sc.Input[ApiRefreshInput].GetYesNo()) sc.Input[ApiRefreshInput].SetYesNo(0);
	const std::string input = useApi ? (state->api ? state->api->data : "") : sc.TextInput.GetChars();
	const double displayDivisor = ticker == SPY ? 10.0 : ticker == QQQ ? 40.0 : 1.0;
	const double apiWidth = useApi ? sc.Input[ApiBandWidthInput].GetInt() * sc.TickSize * displayDivisor : 0;
	const bool selectionChanged = UpdateSelection(state->selection, input,
		ticker, sc.Input[SpreadInput].GetFloat(),
		sc.Input[MaxLevelsInput].GetInt(), referencePrice, recenter,
		sc.Input[UseExposureFilterInput].GetYesNo() != 0, sc.Input[MinExposureInput].GetFloat(), useApi, apiWidth,
		sc.Input[ApiExposureBasisInput].GetIndex() != CombinedExposure);
	if (recenter)
		sc.Input[RecenterInput].SetYesNo(0);
	if (selectionChanged && !Trim(state->selection.source).empty())
	{
		const Payload& payload = state->selection.payload;
		const int missingExposure = MissingExposureCount(state->selection);
		SCString message;
		message.Format("TLADe Quantum: parsed %d nodes; displaying %d frozen at %.2f; spread %.4f. Skipped %d malformed records.",
			static_cast<int>(payload.nodes.size()), static_cast<int>(state->selection.frozenNodes.size()),
			state->selection.referencePrice, payload.spread, payload.rejectedRecords);
		if (payload.invalidSpread)
			message += " Invalid S: header; manual spread override used.";
		if (payload.nodes.empty())
			message += " No valid quantum nodes. Check the Quantum Data input format.";
		if (state->selection.useExposureFilter)
		{
			SCString filterMessage;
			if (state->selection.useApiGex)
				filterMessage.Format(" API GEX magnitude minimum %.2fM; ZG exempt. Not combined Call/Put exposure.",
					state->selection.minExposureMillions);
			else
				filterMessage.Format(" Combined exposure minimum %.2fM; ZG exempt. Excluded %d nodes with missing/invalid Call/Put amounts.",
					state->selection.minExposureMillions, missingExposure);
			message += filterMessage;
		}
		sc.AddMessageToLog(message, payload.invalidSpread || payload.rejectedRecords > 0
			|| payload.nodes.empty() || missingExposure > 0 ? 1 : 0);
	}
	DrawRefreshStatus(sc, *state, useApi);
	const COLORREF background = GetBackgroundColor(sc);
	const int labelAlign = static_cast<int>(sc.Input[LabelAlignInput].GetIndex());
	const bool redrawFields = selectionChanged || state->lastDrawnBar != lastIndex
		|| state->lastDrawnTime != sc.BaseDateTimeIn[lastIndex] || state->lastBackground != background;
	if (!redrawFields && labelAlign != 1 && state->lastLabelAlign == labelAlign)
		return;
	const bool drawn = DrawLadder(sc, *state, background, !redrawFields);
	if (!drawn && !state->drawingFailed)
		sc.AddMessageToLog("TLADe Quantum: a chart drawing could not be added; retrying on the next update.", 1);
	state->drawingFailed = !drawn;
	state->lastDrawnBar = drawn ? lastIndex : -1;
	state->lastDrawnTime = sc.BaseDateTimeIn[lastIndex];
	state->lastBackground = background;
	state->lastLabelAlign = labelAlign;
}
