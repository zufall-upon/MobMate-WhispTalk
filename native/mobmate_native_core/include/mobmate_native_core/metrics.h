#pragma once

#include <string>
#include <vector>
#include <cstdint>

namespace mobmate {

/// Compute Levenshtein edit distance between two UTF-8 strings (code point level).
int levenshtein_distance(const std::string& a, const std::string& b);

/// Compute Character Error Rate: edits / max(len_a, len_b) in code points.
/// Returns 0.0 if both strings are empty.
double compute_cer(const std::string& expected, const std::string& transcript);

/// Check exact match (case-sensitive, whitespace-normalized).
bool exact_match(const std::string& a, const std::string& b);

/// Compute V1-compatible benchmark CER using language normalizer and aliases.
double compute_metric_cer(const std::string& expected,
                          const std::string& transcript,
                          const std::string& language,
                          const std::vector<std::string>& aliases,
                          bool moonshine_profile);

/// Check V1-compatible exact match using language normalizer and aliases.
bool exact_match_metric(const std::string& expected,
                        const std::string& transcript,
                        const std::string& language,
                        const std::vector<std::string>& aliases,
                        bool moonshine_profile);

/// Benchmark result for a single row
struct BenchResult {
    int index;
    std::string wav_path;
    std::string expected;
    std::string transcript;
    bool is_exact_match;
    double cer;
    int64_t latency_ms; // -1 if not measured
    std::string error;  // non-empty on failure
    std::string extra_json_fields; // preformatted fields starting without leading comma
};

/// Serialize a BenchResult to JSONL line.
std::string bench_result_to_jsonl(const BenchResult& result);

/// Compute summary statistics from benchmark results.
struct BenchSummary {
    int total;
    int exact_count;
    double exact_rate;
    double avg_cer;
    double median_cer;
    int64_t avg_latency_ms;
    int64_t p50_latency_ms;
    int64_t p95_latency_ms;
    int error_count;
};

/// Compute summary from results.
BenchSummary compute_summary(const std::vector<BenchResult>& results);

/// Serialize summary to JSON.
std::string summary_to_json(const BenchSummary& summary);

} // namespace mobmate
