#pragma once

#ifndef _WIN32

#include <cstdint>
#include <vector>
#include <string>
#include <mutex>
#include <atomic>
#include <thread>
#include <functional>

// Forward declaration to avoid pulling in miniaudio.h in this header
struct ma_device;

namespace mobmate {

struct AudioDeviceInfo {
    std::string id;
    std::string name;
    bool is_default = false;
};

std::vector<AudioDeviceInfo> enumerate_capture_devices(std::string& err_msg);

/// Minimal audio capture for Linux via miniaudio (PipeWire/PulseAudio/ALSA).
/// Captures mono 16kHz 16-bit PCM into an internal buffer.
/// Interface is intentionally identical to WasapiCapture for native_api.cpp compatibility.
class MiniAudioCapture {
public:
    MiniAudioCapture();
    ~MiniAudioCapture();
    MiniAudioCapture(const MiniAudioCapture&) = delete;
    MiniAudioCapture& operator=(const MiniAudioCapture&) = delete;

    /// Initialize with the default capture device.
    int initialize(std::string& err_msg);
    /// Initialize with a specific device id (empty string = default).
    int initialize(const std::string& device_id, std::string& err_msg);

    /// Start capturing audio (fills internal buffer via callback).
    int start(std::string& err_msg);

    /// Stop capturing.
    void stop();

    /// Release resources (idempotent).
    void release();

    /// Thread-safe snapshot of buffered PCM samples.
    std::vector<int16_t> get_samples() const;

    /// Clear all buffered samples.
    void clear_samples();

    /// Discard the first `count` samples from the buffer.
    void discard_samples(size_t count);

    /// RMS of all buffered samples.
    double get_rms_level() const;

    /// RMS of the most recent `max_samples` samples.
    double get_recent_rms_level(size_t max_samples) const;

    /// Current automatic gain control multiplier.
    double get_agc_gain() const { return _agc_gain; }

    bool is_running() const { return _running.load(); }

    /// Check an unexpected backend stop on the control thread. Only a missing
    /// explicitly selected device is conclusive; silence/suspension is not.
    bool source_lost();

private:
    // Called by miniaudio on each capture callback (on audio thread).
    static void data_callback(ma_device* pDevice, void* pOutput,
                              const void* pInput, unsigned int frameCount);
    void on_frames(const int16_t* frames, unsigned int frame_count);
    void apply_agc_locked(std::vector<int16_t>& samples);

    struct Impl;          // Forward-declared to avoid pulling in miniaudio.h here
    Impl* _impl = nullptr;

    std::atomic<bool> _running{false};
    std::atomic<bool> _source_lost{false};

    mutable std::mutex _buffer_mutex;
    std::vector<int16_t> _pcm_buffer;
    double _agc_gain = 1.0;
    double _agc_smoothed_rms = 0.0;
};

} // namespace mobmate

#endif // !_WIN32
