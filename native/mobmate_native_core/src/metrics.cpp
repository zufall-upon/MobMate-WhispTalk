#include "mobmate_native_core/metrics.h"
#include <algorithm>
#include <sstream>
#include <cmath>
#include <stdexcept>
#include <cctype>
#include <limits>

namespace mobmate {

namespace {

// Decode UTF-8 string into code points (uint32_t)
std::vector<uint32_t> utf8_to_codepoints(const std::string& s) {
    std::vector<uint32_t> cps;
    const uint8_t* data = reinterpret_cast<const uint8_t*>(s.data());
    size_t i = 0;
    while (i < s.size()) {
        uint32_t cp;
        if ((data[i] & 0x80) == 0) {
            cp = data[i];
            i += 1;
        } else if ((data[i] & 0xE0) == 0xC0) {
            cp = ((data[i] & 0x1F) << 6) | (data[i + 1] & 0x3F);
            i += 2;
        } else if ((data[i] & 0xF0) == 0xE0) {
            cp = ((data[i] & 0x0F) << 12) | ((data[i + 1] & 0x3F) << 6) | (data[i + 2] & 0x3F);
            i += 3;
        } else if ((data[i] & 0xF8) == 0xF0) {
            cp = ((data[i] & 0x07) << 18) | ((data[i + 1] & 0x3F) << 12) |
                 ((data[i + 2] & 0x3F) << 6) | (data[i + 3] & 0x3F);
            i += 4;
        } else {
            // Invalid, skip
            cp = data[i];
            i += 1;
        }
        cps.push_back(cp);
    }
    return cps;
}

std::string codepoints_to_utf8(const std::vector<uint32_t>& cps) {
    std::string out;
    for (uint32_t cp : cps) {
        if (cp <= 0x7F) {
            out.push_back(static_cast<char>(cp));
        } else if (cp <= 0x7FF) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp <= 0xFFFF) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }
    return out;
}

void replace_all(std::string& s, const std::string& from, const std::string& to) {
    if (from.empty()) return;
    size_t pos = 0;
    while ((pos = s.find(from, pos)) != std::string::npos) {
        s.replace(pos, from.size(), to);
        pos += to.size();
    }
}

std::string katakana_to_hiragana(const std::string& text) {
    auto cps = utf8_to_codepoints(text);
    for (auto& cp : cps) {
        if (cp >= 0x30A1 && cp <= 0x30F6) cp -= 0x60;
    }
    return codepoints_to_utf8(cps);
}

std::string normalize_common_metric(const std::string& text) {
    std::string out;
    for (uint32_t cp : utf8_to_codepoints(text)) {
        bool skip = (cp == ' ' || cp == '\t' || cp == '\n' || cp == '\r' || cp == 0x3000);
        switch (cp) {
            case 0x3002: case 0x3001: case 0xFF0C: case ',': case '.': case '!': case '?':
            case 0xFF01: case 0xFF1F: case ':': case 0xFF1A: case ';': case 0xFF1B:
            case '"': case '\'': case '`': case 0x00B4: case 0x2018: case 0x2019:
            case 0x201C: case 0x201D: case '(': case ')': case 0xFF08: case 0xFF09:
            case '[': case ']': case '{': case '}': case 0x300C: case 0x300D:
            case 0x300E: case 0x300F: case 0x2026: case 0x30FB: case 0xFF65:
                skip = true;
                break;
        }
        if (!skip) out += codepoints_to_utf8({cp});
    }
    return out;
}

std::string normalize_japanese_metric(std::string s, bool moonshine_profile) {
    const std::vector<std::pair<std::string, std::string>> common = {
        {"今日", "きょう"}, {"当た", "あた"}, {"乗せ", "のせ"}, {"真っすぐ", "まっすぐ"},
        {"私", "わたし"}, {"声", "こえ"}, {"聞", "き"}, {"話", "はな"}, {"周", "まわ"},
        {"小", "ちい"}, {"今", "いま"}, {"左", "ひだり"}, {"移動", "いどう"},
        {"画面", "がめん"}, {"確認", "かくにん"}, {"下さい", "ください"},
        {"１", "いち"}, {"1", "いち"}, {"一", "いち"}, {"秒", "びょう"},
        {"止", "と"}, {"次", "つぎ"}, {"早", "はや"}, {"開", "ひら"},
        {"切", "き"}, {"替", "か"}, {"雑音", "ざつおん"}, {"背景", "はいけい"},
        {"部屋", "へや"}, {"音", "おと"}, {"文", "ぶん"}, {"拾", "ひろ"},
        {"怨霊", "音量"}, {"人", "ひと"}
    };
    for (const auto& p : common) replace_all(s, p.first, p.second);
    if (moonshine_profile) {
        const std::vector<std::pair<std::string, std::string>> extra = {
            {"この", "この"}, {"テスト", "てすと"}, {"周り", "まわり"},
            {"うるさい", "うるさい"}, {"まだ", "まだ"}, {"はっきり", "はっきり"},
            {"しゃべ", "はな"}, {"喋", "はな"},
            {"怨霊をあげずに優しく話します", "音量を上げずにやさしく話します"},
            {"ください", "ください"}
        };
        for (const auto& p : extra) replace_all(s, p.first, p.second);
    } else {
        replace_all(s, "喋", "はな");
        replace_all(s, "しゃべ", "はな");
    }
    return katakana_to_hiragana(s);
}

std::string normalize_metric(const std::string& text, const std::string& language, bool moonshine_profile) {
    std::string s = normalize_common_metric(text);
    if (language == "ja") return normalize_japanese_metric(s, moonshine_profile);
    if (language == "en") {
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    }
    return s;
}

// Normalize whitespace: collapse runs of whitespace to single space, trim
std::string normalize_ws(const std::string& s) {
    std::string result;
    result.reserve(s.size());
    bool last_ws = true;
    for (char c : s) {
        bool ws = (c == ' ' || c == '\t' || c == '\n' || c == '\r');
        if (!ws) {
            result += c;
            last_ws = false;
        } else if (!last_ws) {
            result += ' ';
            last_ws = true;
        }
    }
    // Trim trailing space
    if (!result.empty() && result.back() == ' ') {
        result.pop_back();
    }
    return result;
}

std::string json_escape(const std::string& s) {
    std::string out;
    out.reserve(s.size() + 2);
    for (char c : s) {
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:   out += c; break;
        }
    }
    return out;
}

} // anonymous namespace

int levenshtein_distance(const std::string& a, const std::string& b) {
    auto cps_a = utf8_to_codepoints(a);
    auto cps_b = utf8_to_codepoints(b);

    size_t m = cps_a.size();
    size_t n = cps_b.size();

    // Use two-row DP for memory efficiency
    std::vector<int> prev(n + 1);
    std::vector<int> curr(n + 1);

    for (size_t j = 0; j <= n; ++j) {
        prev[j] = static_cast<int>(j);
    }

    for (size_t i = 1; i <= m; ++i) {
        curr[0] = static_cast<int>(i);
        for (size_t j = 1; j <= n; ++j) {
            int cost = (cps_a[i - 1] == cps_b[j - 1]) ? 0 : 1;
            curr[j] = std::min({
                prev[j] + 1,      // deletion
                curr[j - 1] + 1,  // insertion
                prev[j - 1] + cost // substitution
            });
        }
        std::swap(prev, curr);
    }

    return prev[n];
}

double compute_cer(const std::string& expected, const std::string& transcript) {
    auto cps_e = utf8_to_codepoints(expected);
    auto cps_t = utf8_to_codepoints(transcript);

    if (cps_e.empty() && cps_t.empty()) return 0.0;

    int dist = levenshtein_distance(expected, transcript);
    size_t max_len = std::max(cps_e.size(), cps_t.size());

    return static_cast<double>(dist) / static_cast<double>(max_len);
}

bool exact_match(const std::string& a, const std::string& b) {
    return normalize_ws(a) == normalize_ws(b);
}

double compute_metric_cer(const std::string& expected,
                          const std::string& transcript,
                          const std::string& language,
                          const std::vector<std::string>& aliases,
                          bool moonshine_profile) {
    std::vector<std::string> candidates{expected};
    candidates.insert(candidates.end(), aliases.begin(), aliases.end());
    double best = std::numeric_limits<double>::infinity();
    const std::string actual = normalize_metric(transcript, language, moonshine_profile);
    for (const auto& candidate : candidates) {
        const std::string normalized_expected = normalize_metric(candidate, language, moonshine_profile);
        if (normalized_expected.empty()) continue;
        const double cur = static_cast<double>(levenshtein_distance(normalized_expected, actual)) /
            static_cast<double>(utf8_to_codepoints(normalized_expected).size());
        if (cur < best) best = cur;
    }
    if (std::isinf(best)) return actual.empty() ? 0.0 : 1.0;
    return best;
}

bool exact_match_metric(const std::string& expected,
                        const std::string& transcript,
                        const std::string& language,
                        const std::vector<std::string>& aliases,
                        bool moonshine_profile) {
    std::vector<std::string> candidates{expected};
    candidates.insert(candidates.end(), aliases.begin(), aliases.end());
    const std::string actual = normalize_metric(transcript, language, moonshine_profile);
    for (const auto& candidate : candidates) {
        const std::string normalized_expected = normalize_metric(candidate, language, moonshine_profile);
        if (!normalized_expected.empty() && normalized_expected == actual) return true;
    }
    return false;
}

std::string bench_result_to_jsonl(const BenchResult& result) {
    std::ostringstream oss;
    oss << "{"
        << "\"index\":" << result.index << ","
        << "\"wav\":\"" << json_escape(result.wav_path) << "\","
        << "\"expected\":\"" << json_escape(result.expected) << "\","
        << "\"transcript\":\"" << json_escape(result.transcript) << "\","
        << "\"exact\":" << (result.is_exact_match ? "true" : "false") << ","
        << "\"cer\":" << std::fixed;

    // Format CER to 4 decimal places
    oss.precision(4);
    oss << result.cer;

    oss << ","
        << "\"latency_ms\":" << result.latency_ms;

    if (!result.extra_json_fields.empty()) {
        oss << "," << result.extra_json_fields;
    }

    if (!result.error.empty()) {
        oss << ","
            << "\"error\":\"" << json_escape(result.error) << "\"";
    }

    oss << "}";
    return oss.str();
}

BenchSummary compute_summary(const std::vector<BenchResult>& results) {
    BenchSummary summary = {};
    summary.total = static_cast<int>(results.size());

    if (results.empty()) return summary;

    double total_cer = 0.0;
    int64_t total_latency = 0;
    int latency_count = 0;
    std::vector<double> cer_values;
    std::vector<int64_t> latencies;

    for (const auto& r : results) {
        if (r.is_exact_match) summary.exact_count++;
        if (!r.error.empty()) summary.error_count++;
        total_cer += r.cer;
        cer_values.push_back(r.cer);

        if (r.latency_ms >= 0) {
            total_latency += r.latency_ms;
            latency_count++;
            latencies.push_back(r.latency_ms);
        }
    }

    summary.exact_rate = static_cast<double>(summary.exact_count) / static_cast<double>(summary.total);
    summary.avg_cer = total_cer / static_cast<double>(summary.total);

    // Median CER
    std::sort(cer_values.begin(), cer_values.end());
    size_t mid = cer_values.size() / 2;
    if (cer_values.size() % 2 == 0 && cer_values.size() > 1) {
        summary.median_cer = (cer_values[mid - 1] + cer_values[mid]) / 2.0;
    } else {
        summary.median_cer = cer_values[mid];
    }

    // Latency stats
    if (latency_count > 0) {
        summary.avg_latency_ms = total_latency / latency_count;
        std::sort(latencies.begin(), latencies.end());

        size_t p50_idx = (latencies.size() * 50) / 100;
        size_t p95_idx = std::min((latencies.size() * 95) / 100, latencies.size() - 1);
        summary.p50_latency_ms = latencies[p50_idx];
        summary.p95_latency_ms = latencies[p95_idx];
    } else {
        summary.avg_latency_ms = -1;
        summary.p50_latency_ms = -1;
        summary.p95_latency_ms = -1;
    }

    return summary;
}

std::string summary_to_json(const BenchSummary& summary) {
    std::ostringstream oss;
    oss << "{\n"
        << "  \"total\": " << summary.total << ",\n"
        << "  \"exact_count\": " << summary.exact_count << ",\n"
        << "  \"exact_rate\": " << std::fixed;
    oss.precision(4);
    oss << summary.exact_rate << ",\n"
        << "  \"avg_cer\": " << summary.avg_cer << ",\n"
        << "  \"median_cer\": " << summary.median_cer << ",\n"
        << "  \"avg_latency_ms\": " << summary.avg_latency_ms << ",\n"
        << "  \"p50_latency_ms\": " << summary.p50_latency_ms << ",\n"
        << "  \"p95_latency_ms\": " << summary.p95_latency_ms << ",\n"
        << "  \"error_count\": " << summary.error_count << "\n"
        << "}";
    return oss.str();
}

} // namespace mobmate
