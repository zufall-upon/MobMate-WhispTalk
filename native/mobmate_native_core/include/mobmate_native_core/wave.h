#pragma once

#include <cstdint>
#include <string>
#include <vector>
#include <optional>

namespace mobmate {

struct WavMetadata {
    std::string file_path;
    uint32_t sample_rate;
    uint16_t channels;
    uint16_t bits_per_sample;
    uint32_t num_samples;
    uint32_t data_size;
    bool is_pcm;
    bool is_16bit;
    bool is_16khz;
};

/// Read WAV file metadata. Returns nullopt on failure with error in err_msg.
std::optional<WavMetadata> read_wav_metadata(const std::string& path, std::string& err_msg);

/// Read WAV PCM 16-bit samples into a flat int16_t buffer (interleaved for stereo).
/// Returns nullopt on failure.
std::optional<std::vector<int16_t>> read_wav_samples(const std::string& path, std::string& err_msg);

/// Serialize WavMetadata to a simple JSON string (no external deps).
std::string wav_metadata_to_json(const WavMetadata& meta);

/// Write float32 samples as a mono 16-bit PCM WAV file.
/// Samples are expected in [-1, 1] range and will be scaled to int16.
/// Returns true on success.
bool write_wav_float32(const std::string& path,
                        const float* samples,
                        int num_samples,
                        uint32_t sample_rate);

/// Write int16 PCM samples as a mono 16-bit WAV file directly (no float conversion).
/// Returns true on success.
bool write_wav_pcm16(const std::string& path,
                     const int16_t* samples,
                     int num_samples,
                     uint32_t sample_rate);

} // namespace mobmate
