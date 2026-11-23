#include "mobmate_native_core/wave.h"
#include <fstream>
#include <cstring>
#include <sstream>
#include <system_error>
#include <filesystem>
#ifdef _WIN32
#include <windows.h>
#endif
namespace mobmate {

namespace {

/// Convert UTF-8 std::string to std::filesystem::path via Wide API.
/// MSVC's std::filesystem::path(std::string) uses the current locale
/// (often Shift-JIS on ja-JP systems) and fails on UTF-8 paths.
static std::filesystem::path utf8_to_path(const std::string& utf8) {
#ifdef _WIN32
    int len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), nullptr, 0);
    if (len <= 0) return std::filesystem::path(utf8); // fallback
    std::wstring wstr(len, L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), &wstr[0], len) <= 0)
        return std::filesystem::path(utf8);
    return std::filesystem::path(wstr);
#else
    return std::filesystem::path(utf8);
#endif
}

// Read a 32-bit little-endian unsigned integer from a buffer
uint32_t read_le_u32(const uint8_t* buf) {
    return static_cast<uint32_t>(buf[0])
         | (static_cast<uint32_t>(buf[1]) << 8)
         | (static_cast<uint32_t>(buf[2]) << 16)
         | (static_cast<uint32_t>(buf[3]) << 24);
}

// Read a 16-bit little-endian unsigned integer from a buffer
uint16_t read_le_u16(const uint8_t* buf) {
    return static_cast<uint16_t>(buf[0]) | (static_cast<uint16_t>(buf[1]) << 8);
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

std::optional<WavMetadata> read_wav_metadata(const std::string& path, std::string& err_msg) {
    auto fs_path = utf8_to_path(path);
    std::ifstream ifs(fs_path, std::ios::binary);
    if (!ifs) {
        err_msg = "Cannot open file: " + path;
        return std::nullopt;
    }

    // Read RIFF header (12 bytes)
    uint8_t riff_header[12];
    ifs.read(reinterpret_cast<char*>(riff_header), 12);
    if (ifs.gcount() != 12) {
        err_msg = "File too short for RIFF header";
        return std::nullopt;
    }

    // Check "RIFF"
    if (std::memcmp(riff_header, "RIFF", 4) != 0) {
        err_msg = "Not a RIFF file";
        return std::nullopt;
    }

    // Check "WAVE"
    if (std::memcmp(riff_header + 8, "WAVE", 4) != 0) {
        err_msg = "Not a WAVE file";
        return std::nullopt;
    }

    // Parse chunks to find 'fmt ' and 'data'
    uint32_t sample_rate = 0;
    uint16_t channels = 0;
    uint16_t bits_per_sample = 0;
    uint16_t audio_format = 0;
    uint32_t data_size = 0;
    bool has_fmt = false;
    bool has_data = false;

    while (ifs.good()) {
        // Read chunk header (8 bytes)
        uint8_t chunk_header[8];
        ifs.read(reinterpret_cast<char*>(chunk_header), 8);
        if (ifs.gcount() < 4) break;
        if (ifs.gcount() < 8) {
            err_msg = "Truncated chunk header";
            return std::nullopt;
        }

        char chunk_id[4];
        std::memcpy(chunk_id, chunk_header, 4);
        uint32_t chunk_size = read_le_u32(chunk_header + 4);

        if (std::memcmp(chunk_id, "fmt ", 4) == 0) {
            if (chunk_size < 16) {
                err_msg = "fmt chunk too small";
                return std::nullopt;
            }
            uint8_t fmt_data[16];
            ifs.read(reinterpret_cast<char*>(fmt_data), 16);
            if (ifs.gcount() != 16) {
                err_msg = "Truncated fmt chunk";
                return std::nullopt;
            }
            audio_format = read_le_u16(fmt_data);
            channels = read_le_u16(fmt_data + 2);
            sample_rate = read_le_u32(fmt_data + 4);
            bits_per_sample = read_le_u16(fmt_data + 14);
            has_fmt = true;
            // fmt chunk may have extra bytes (e.g. 18-byte WAVEFORMATEX), seek past remainder
            if (chunk_size > 16) {
                ifs.seekg(chunk_size - 16, std::ios::cur);
            }
        } else if (std::memcmp(chunk_id, "data", 4) == 0) {
            data_size = chunk_size;
            has_data = true;
            break;
        } else {
            ifs.seekg(chunk_size, std::ios::cur);
        }
    }

    if (!has_fmt) {
        err_msg = "No fmt chunk found";
        return std::nullopt;
    }
    if (!has_data) {
        err_msg = "No data chunk found";
        return std::nullopt;
    }

    WavMetadata meta;
    meta.file_path = utf8_to_path(path).string();
    meta.sample_rate = sample_rate;
    meta.channels = channels;
    meta.bits_per_sample = bits_per_sample;
    meta.data_size = data_size;
    meta.is_pcm = (audio_format == 1);
    meta.is_16bit = (bits_per_sample == 16);
    meta.is_16khz = (sample_rate == 16000);

    if (meta.is_pcm && meta.is_16bit) {
        meta.num_samples = data_size / (channels * 2);
    } else {
        meta.num_samples = 0;
    }

    return meta;
}

std::optional<std::vector<int16_t>> read_wav_samples(const std::string& path, std::string& err_msg) {
    auto meta = read_wav_metadata(path, err_msg);
    if (!meta) return std::nullopt;

    if (!meta->is_pcm) {
        err_msg = "Only PCM WAV is supported (audio format: " + std::to_string(meta->bits_per_sample) + ")";
        return std::nullopt;
    }
    if (!meta->is_16bit) {
        err_msg = "Only 16-bit WAV is supported (bits: " + std::to_string(meta->bits_per_sample) + ")";
        return std::nullopt;
    }

    auto fs_path = utf8_to_path(path);
    std::ifstream ifs(fs_path, std::ios::binary);
    if (!ifs) {
        err_msg = "Cannot open file: " + path;
        return std::nullopt;
    }

    // Skip RIFF header (12 bytes)
    ifs.seekg(12, std::ios::beg);
    // Skip to data chunk
    while (ifs.good()) {
        uint8_t chunk_header[8];
        ifs.read(reinterpret_cast<char*>(chunk_header), 8);
        if (ifs.gcount() < 8) break;

        char chunk_id[5] = {};
        std::memcpy(chunk_id, chunk_header, 4);
        uint32_t chunk_size = read_le_u32(chunk_header + 4);

        if (std::memcmp(chunk_id, "data", 4) == 0) {
            // Found data chunk, read samples
            std::vector<int16_t> samples(meta->num_samples * meta->channels);
            ifs.read(reinterpret_cast<char*>(samples.data()), meta->data_size);
            if (ifs.gcount() != static_cast<std::streamsize>(meta->data_size)) {
                err_msg = "Failed to read all sample data";
                return std::nullopt;
            }
            return samples;
        } else {
            ifs.seekg(chunk_size, std::ios::cur);
        }
    }

    err_msg = "Data chunk not found";
    return std::nullopt;
}

std::string wav_metadata_to_json(const WavMetadata& meta) {
    std::ostringstream oss;
    oss << "{\n"
        << "  \"file\": \"" << json_escape(meta.file_path) << "\",\n"
        << "  \"sample_rate\": " << meta.sample_rate << ",\n"
        << "  \"channels\": " << meta.channels << ",\n"
        << "  \"bits_per_sample\": " << meta.bits_per_sample << ",\n"
        << "  \"num_samples\": " << meta.num_samples << ",\n"
        << "  \"data_size\": " << meta.data_size << ",\n"
        << "  \"is_pcm\": " << (meta.is_pcm ? "true" : "false") << ",\n"
        << "  \"is_16bit\": " << (meta.is_16bit ? "true" : "false") << ",\n"
        << "  \"is_16khz\": " << (meta.is_16khz ? "true" : "false") << "\n"
        << "}";
    return oss.str();
}

bool write_wav_float32(const std::string& path,
                       const float* samples,
                       int num_samples,
                       uint32_t sample_rate) {
    if (!samples || num_samples <= 0 || !std::filesystem::exists(utf8_to_path(std::filesystem::path(path).parent_path().string()))) {
        return false;
    }

    // Convert float32 [-1, 1] to int16
    std::vector<int16_t> pcm(num_samples);
    for (int i = 0; i < num_samples; i++) {
        float s = samples[i];
        if (s > 1.0f) s = 1.0f;
        if (s < -1.0f) s = -1.0f;
        pcm[i] = static_cast<int16_t>(s * 32767.0f);
    }

    uint32_t data_size = static_cast<uint32_t>(num_samples * 2);
    uint16_t channels = 1;
    uint16_t bits_per_sample = 16;
    uint32_t byte_rate = sample_rate * channels * (bits_per_sample / 8);
    uint16_t block_align = channels * (bits_per_sample / 8);
    uint32_t riff_size = 36 + data_size;

    auto fs_path = utf8_to_path(path);
    std::ofstream ofs(fs_path, std::ios::binary);
    if (!ofs) return false;

    auto write_le_u32 = [&](uint32_t value) {
        uint8_t buf[4];
        buf[0] = static_cast<uint8_t>(value & 0xFF);
        buf[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
        buf[2] = static_cast<uint8_t>((value >> 16) & 0xFF);
        buf[3] = static_cast<uint8_t>((value >> 24) & 0xFF);
        ofs.write(reinterpret_cast<char*>(buf), 4);
    };

    auto write_le_u16 = [&](uint16_t value) {
        uint8_t buf[2];
        buf[0] = static_cast<uint8_t>(value & 0xFF);
        buf[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
        ofs.write(reinterpret_cast<char*>(buf), 2);
    };

    // RIFF header
    ofs.write("RIFF", 4);
    write_le_u32(riff_size);
    ofs.write("WAVE", 4);

    // fmt chunk
    ofs.write("fmt ", 4);
    write_le_u32(16); // chunk size
    write_le_u16(1);  // PCM
    write_le_u16(channels);
    write_le_u32(sample_rate);
    write_le_u32(byte_rate);
    write_le_u16(block_align);
    write_le_u16(bits_per_sample);

    // data chunk
    ofs.write("data", 4);
    write_le_u32(data_size);
    ofs.write(reinterpret_cast<char*>(pcm.data()), data_size);

    ofs.close();
    return ofs.good() || ofs.eof();
}

bool write_wav_pcm16(const std::string& path,
                     const int16_t* samples,
                     int num_samples,
                     uint32_t sample_rate) {
    if (!samples || num_samples <= 0) return false;
    auto parent = std::filesystem::path(path).parent_path().string();
    if (!parent.empty() && !std::filesystem::exists(utf8_to_path(parent))) {
        return false;
    }

    uint32_t data_size = static_cast<uint32_t>(num_samples * 2);
    uint16_t channels = 1;
    uint16_t bits_per_sample = 16;
    uint32_t byte_rate = sample_rate * channels * (bits_per_sample / 8);
    uint16_t block_align = channels * (bits_per_sample / 8);
    uint32_t riff_size = 36 + data_size;

    auto fs_path = utf8_to_path(path);
    std::ofstream ofs(fs_path, std::ios::binary);
    if (!ofs) return false;

    auto write_le_u32 = [&](uint32_t value) {
        uint8_t buf[4];
        buf[0] = static_cast<uint8_t>(value & 0xFF);
        buf[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
        buf[2] = static_cast<uint8_t>((value >> 16) & 0xFF);
        buf[3] = static_cast<uint8_t>((value >> 24) & 0xFF);
        ofs.write(reinterpret_cast<char*>(buf), 4);
    };

    auto write_le_u16 = [&](uint16_t value) {
        uint8_t buf[2];
        buf[0] = static_cast<uint8_t>(value & 0xFF);
        buf[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
        ofs.write(reinterpret_cast<char*>(buf), 2);
    };

    ofs.write("RIFF", 4);
    write_le_u32(riff_size);
    ofs.write("WAVE", 4);
    ofs.write("fmt ", 4);
    write_le_u32(16);
    write_le_u16(1);
    write_le_u16(channels);
    write_le_u32(sample_rate);
    write_le_u32(byte_rate);
    write_le_u16(block_align);
    write_le_u16(bits_per_sample);
    ofs.write("data", 4);
    write_le_u32(data_size);
    ofs.write(reinterpret_cast<const char*>(samples), data_size);

    ofs.close();
    return ofs.good() || ofs.eof();
}

} // namespace mobmate
