#pragma once

#include <string>
#include <vector>
#include <optional>

namespace mobmate {

struct ManifestRow {
    int index;
    std::string wav_path;
    std::string expected;
    std::string language;
    std::string scene;
    std::vector<std::string> aliases;
};

/// Read manifest CSV. Skips comment lines starting with '#' and header line.
/// Returns nullopt on fatal error with message in err_msg.
/// Per-row errors are reported via warnings (not fatal).
std::optional<std::vector<ManifestRow>> read_manifest(const std::string& path, std::string& err_msg);

/// Serialize a ManifestRow to JSON.
std::string manifest_row_to_json(const ManifestRow& row);

} // namespace mobmate
