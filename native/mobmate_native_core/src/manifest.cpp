#include "mobmate_native_core/manifest.h"
#include <fstream>
#include <sstream>
#include <algorithm>
#include <iostream>
#include <filesystem>
#ifdef _WIN32
#include <windows.h>
#endif

namespace mobmate {

namespace {

/// Convert UTF-8 std::string to std::filesystem::path via Wide API.
static std::filesystem::path utf8_to_path(const std::string& utf8) {
#ifdef _WIN32
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), nullptr, 0);
    if (len <= 0) return std::filesystem::path(utf8);
    std::wstring wstr(len, L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), &wstr[0], len) <= 0)
        return std::filesystem::path(utf8);
    return std::filesystem::path(wstr);
#else
    return std::filesystem::path(utf8);
#endif
}

// Trim whitespace from both ends
std::string trim(const std::string& s) {
    size_t start = s.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return "";
    size_t end = s.find_last_not_of(" \t\r\n");
    return s.substr(start, end - start + 1);
}

// Parse a quoted CSV field: remove surrounding quotes and handle escaped quotes
std::string parse_csv_field(const std::string& raw) {
    std::string s = trim(raw);
    if (s.size() >= 2 && s.front() == '"' && s.back() == '"') {
        // Remove surrounding quotes
        s = s.substr(1, s.size() - 2);
        // Unescape doubled quotes
        std::string result;
        result.reserve(s.size());
        for (size_t i = 0; i < s.size(); ++i) {
            if (s[i] == '"' && i + 1 < s.size() && s[i + 1] == '"') {
                result += '"';
                ++i;
            } else {
                result += s[i];
            }
        }
        return result;
    }
    return s;
}

// Split a CSV line into fields, handling quoted fields
std::vector<std::string> split_csv_line(const std::string& line) {
    std::vector<std::string> fields;
    std::string field;
    bool in_quotes = false;

    for (size_t i = 0; i < line.size(); ++i) {
        char c = line[i];
        if (c == '"') {
            if (in_quotes && i + 1 < line.size() && line[i + 1] == '"') {
                field += "\"\"";
                ++i;
            } else {
                in_quotes = !in_quotes;
                field += c;
            }
        } else if (c == ',' && !in_quotes) {
            fields.push_back(field);
            field.clear();
        } else {
            field += c;
        }
    }
    fields.push_back(field);
    return fields;
}

// Split aliases by '|' delimiter
std::vector<std::string> split_aliases(const std::string& aliases_str) {
    std::vector<std::string> aliases;
    std::istringstream iss(aliases_str);
    std::string alias;
    while (std::getline(iss, alias, '|')) {
        std::string trimmed = trim(alias);
        if (!trimmed.empty()) {
            aliases.push_back(trimmed);
        }
    }
    return aliases;
}

// Escape a string for JSON output
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

std::optional<std::vector<ManifestRow>> read_manifest(const std::string& path, std::string& err_msg) {
    auto fs_path = utf8_to_path(path);
    std::ifstream ifs(fs_path);
    if (!ifs) {
        err_msg = "Cannot open manifest file: " + path;
        return std::nullopt;
    }

    std::vector<ManifestRow> rows;
    std::string line;
    int line_num = 0;
    bool header_found = false;

    while (std::getline(ifs, line)) {
        ++line_num;
        std::string trimmed = trim(line);

        // Skip empty lines
        if (trimmed.empty()) continue;

        // Skip comment lines
        if (trimmed[0] == '#') continue;

        // Check for header line
        if (!header_found) {
            if (trimmed.find("wav") != std::string::npos &&
                trimmed.find("expected") != std::string::npos) {
                header_found = true;
                continue;
            }
            // If it looks like data, treat as data (no header)
            header_found = true;
        }

        // Parse CSV line
        auto fields = split_csv_line(trimmed);
        if (fields.size() < 4) {
            std::cerr << "manifest warning: line " << line_num
                      << " has only " << fields.size() << " columns, skipping" << std::endl;
            continue;
        }

        ManifestRow row;
        row.index = static_cast<int>(rows.size());
        row.wav_path = parse_csv_field(fields[0]);
        row.expected = parse_csv_field(fields[1]);
        row.language = parse_csv_field(fields[2]);
        row.scene = parse_csv_field(fields[3]);

        if (fields.size() >= 5) {
            row.aliases = split_aliases(parse_csv_field(fields[4]));
        }

        rows.push_back(row);
    }

    if (rows.empty()) {
        err_msg = "No data rows found in manifest";
        return std::nullopt;
    }

    return rows;
}

std::string manifest_row_to_json(const ManifestRow& row) {
    std::ostringstream oss;
    oss << "{\n"
        << "  \"index\": " << row.index << ",\n"
        << "  \"wav_path\": \"" << json_escape(row.wav_path) << "\",\n"
        << "  \"expected\": \"" << json_escape(row.expected) << "\",\n"
        << "  \"language\": \"" << json_escape(row.language) << "\",\n"
        << "  \"scene\": \"" << json_escape(row.scene) << "\"";

    if (!row.aliases.empty()) {
        oss << ",\n  \"aliases\": [";
        for (size_t i = 0; i < row.aliases.size(); ++i) {
            if (i > 0) oss << ", ";
            oss << "\"" << json_escape(row.aliases[i]) << "\"";
        }
        oss << "]";
    }

    oss << "\n}";
    return oss.str();
}

} // namespace mobmate
