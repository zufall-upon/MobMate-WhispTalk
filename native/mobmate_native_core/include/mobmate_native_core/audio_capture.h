#pragma once

#ifdef _WIN32

#include <cstdint>
#include <vector>
#include <string>
#include <mutex>
#include <atomic>
#include <thread>
#include <condition_variable>

// WASAPI requires COM headers
#include <windows.h>
#include <comdef.h>
#include <audioclient.h>
#include <mmdeviceapi.h>

namespace mobmate {

struct AudioDeviceInfo {
    std::string id;
    std::string name;
    bool is_default = false;
};

std::vector<AudioDeviceInfo> enumerate_capture_devices(std::string& err_msg);

/// Minimal WASAPI audio capture.
/// Captures mono 16kHz 16-bit PCM into an internal buffer.
class WasapiCapture {
public:
    /// Initialize COM and activate the default capture device.
    /// Returns 0 on success, non-zero on failure (error message in err_msg).
    int initialize(std::string& err_msg);
    int initialize(const std::string& device_id, std::string& err_msg);

    /// Start capturing audio in a background thread.
    /// Returns 0 on success.
    int start(std::string& err_msg);

    /// Stop capturing and join the background thread.
    void stop();

    /// Release all resources.
    void release();

    /// Get the captured PCM16LE samples (thread-safe snapshot).
    std::vector<int16_t> get_samples() const;

    /// Clear captured samples after a processed utterance.
    void clear_samples();

    /// Discard samples from the front after a processed utterance.
    void discard_samples(size_t count);

    /// Get the current RMS level of captured samples.
    double get_rms_level() const;

    /// Get RMS level of recent captured samples.
    double get_recent_rms_level(size_t max_samples) const;

    /// Current automatic gain control multiplier.
    double get_agc_gain() const { return _agc_gain; }

    /// Check if capture is running.
    bool is_running() const { return _running.load(); }

    WasapiCapture();
    ~WasapiCapture();
    // Non-copyable, non-movable (contains std::mutex)
    WasapiCapture(const WasapiCapture&) = delete;
    WasapiCapture& operator=(const WasapiCapture&) = delete;

private:
    // The native offline regression seeds a private buffer without starting capture.
    friend struct WasapiCaptureTestAccess;
    /// The capture loop running in a background thread.
    void capture_loop();
    void append_mono_samples_locked(const std::vector<int16_t>& mono_samples);
    void apply_agc_locked(std::vector<int16_t>& samples);

    IAudioClient* _audio_client = nullptr;
    IAudioCaptureClient* _capture_client = nullptr;
    bool _com_initialized = false;
    std::thread _capture_thread;
    std::atomic<bool> _running{false};
    std::atomic<bool> _should_stop{false};

    // Device format info
    UINT32 _device_channels = 1;
    UINT32 _device_sample_rate = 16000;
    UINT32 _device_bits_per_sample = 16;
    UINT32 _device_bytes_per_sample = 2;
    bool _device_is_float = false;

    mutable std::mutex _buffer_mutex;
    std::vector<int16_t> _pcm_buffer;
    std::vector<int16_t> _resample_pending;
    double _resample_pos = 0.0;

    // Automatic input gain control for realtime STT capture.
    double _agc_gain = 1.0;
    double _agc_smoothed_rms = 0.0;
};

} // namespace mobmate

#endif // _WIN32
