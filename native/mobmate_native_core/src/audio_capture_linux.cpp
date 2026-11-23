#ifndef _WIN32

// miniaudio: single-header implementation (compiled once here)
// Disable unused codec/format subsystems to reduce compile time.
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_WAV
#define MA_NO_FLAC
#define MA_NO_MP3
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#define MINIAUDIO_IMPLEMENTATION
#include "../../vendor/miniaudio.h"

#include "mobmate_native_core/audio_capture_linux.h"

#include <cmath>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <sstream>
#include <vector>

namespace mobmate {

// ---- Impl (holds ma_device + ma_context) ----

struct MiniAudioCapture::Impl {
    ma_context context{};
    ma_device  device{};
    bool context_initialized = false;
    bool device_initialized  = false;
    MiniAudioCapture* owner  = nullptr;
    std::string selected_name;
    std::atomic<bool> watching{false};
    std::atomic<bool> stopped_notification{false};

    static void notification_callback(const ma_device_notification* event) {
        if (!event || !event->pDevice || event->type != ma_device_notification_type_stopped) return;
        auto* capture = static_cast<MiniAudioCapture*>(event->pDevice->pUserData);
        if (capture && capture->_impl->watching.load())
            capture->_impl->stopped_notification.store(true);
    }
};

// ---- Device enumeration ----

std::vector<AudioDeviceInfo> enumerate_capture_devices(std::string& err_msg) {
    std::vector<AudioDeviceInfo> result;
    ma_context ctx{};
    if (ma_context_init(nullptr, 0, nullptr, &ctx) != MA_SUCCESS) {
        err_msg = "miniaudio: context init failed";
        return result;
    }

    ma_device_info* capture_infos = nullptr;
    ma_uint32 capture_count = 0;
    ma_result r = ma_context_get_devices(&ctx, nullptr, nullptr,
                                         &capture_infos, &capture_count);
    if (r != MA_SUCCESS) {
        err_msg = "miniaudio: get_devices failed";
        ma_context_uninit(&ctx);
        return result;
    }

    for (ma_uint32 i = 0; i < capture_count; ++i) {
        AudioDeviceInfo info;
        // miniaudio id is an opaque union; use the device name as id for simplicity
        info.name = capture_infos[i].name;
        info.id   = capture_infos[i].name; // unique enough for selection
        info.is_default = (i == 0); // first is typically the default
        result.push_back(info);
    }

    ma_context_uninit(&ctx);
    return result;
}

// ---- MiniAudioCapture ----

MiniAudioCapture::MiniAudioCapture()
    : _impl(new Impl{}) {
    _impl->owner = this;
}

MiniAudioCapture::~MiniAudioCapture() {
    release();
    delete _impl;
}

// miniaudio capture callback (called on audio thread)
void MiniAudioCapture::data_callback(ma_device* pDevice, void* /*pOutput*/,
                                      const void* pInput,
                                      unsigned int frameCount) {
    auto* cap = static_cast<MiniAudioCapture*>(pDevice->pUserData);
    if (!cap || !pInput || frameCount == 0) return;
    const ma_uint32 channels = pDevice->capture.channels > 0 ? pDevice->capture.channels : 1;
    std::vector<int16_t> mono(frameCount);
    if (pDevice->capture.format == ma_format_s16) {
        const auto* src = static_cast<const int16_t*>(pInput);
        double s16_energy = 0.0;
        for (unsigned int i = 0; i < frameCount; ++i) {
            mono[i] = src[i * channels];
            s16_energy += static_cast<double>(mono[i]) * static_cast<double>(mono[i]);
        }
        // PipeWire/Pulse inside distrobox may report s16 while passing s32-shaped data
        // whose lower 16 bits are zero. In that case, use the high 16 bits instead.
        if (s16_energy < 1.0) {
            const auto* src32 = static_cast<const int32_t*>(pInput);
            double s32_energy = 0.0;
            for (unsigned int i = 0; i < frameCount; ++i) {
                const auto v = static_cast<int16_t>(src32[i * channels] >> 16);
                mono[i] = v;
                s32_energy += static_cast<double>(v) * static_cast<double>(v);
            }
            if (s32_energy < 1.0) std::fill(mono.begin(), mono.end(), 0);
        }
    } else if (pDevice->capture.format == ma_format_s32) {
        const auto* src = static_cast<const int32_t*>(pInput);
        for (unsigned int i = 0; i < frameCount; ++i) mono[i] = static_cast<int16_t>(src[i * channels] >> 16);
    } else if (pDevice->capture.format == ma_format_f32) {
        const auto* src = static_cast<const float*>(pInput);
        for (unsigned int i = 0; i < frameCount; ++i) {
            const float v = std::clamp(src[i * channels], -1.0f, 1.0f);
            mono[i] = static_cast<int16_t>(v * 32767.0f);
        }
    } else {
        return;
    }
    cap->on_frames(mono.data(), frameCount);
}

void MiniAudioCapture::on_frames(const int16_t* frames, unsigned int frame_count) {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    if (_source_lost.load()) return;
    std::vector<int16_t> adjusted(frames, frames + frame_count);
    apply_agc_locked(adjusted);
    _pcm_buffer.insert(_pcm_buffer.end(), adjusted.begin(), adjusted.end());
    // Safety cap: keep at most 30s of audio (16000 * 30 = 480000 samples)
    constexpr size_t kMaxSamples = 480000;
    if (_pcm_buffer.size() > kMaxSamples) {
        _pcm_buffer.erase(_pcm_buffer.begin(),
                          _pcm_buffer.begin() + static_cast<std::ptrdiff_t>(
                              _pcm_buffer.size() - kMaxSamples));
    }
}

void MiniAudioCapture::apply_agc_locked(std::vector<int16_t>& samples) {
    if (samples.empty()) return;
    static constexpr double kTargetRms = 0.08 * 32768.0; // ~-22dBFS
    static constexpr double kMaxGain = 60.0;
    static constexpr double kMinGain = 0.3;
    static constexpr double kSilenceRms = 1.0;
    static constexpr double kRmsSmooth = 0.08;
    static constexpr double kAttackCoeff = 0.02;
    static constexpr double kReleaseCoeff = 0.15;

    double sum = 0.0;
    for (int16_t s : samples) sum += static_cast<double>(s) * static_cast<double>(s);
    double raw_rms = std::sqrt(sum / static_cast<double>(samples.size()));
    if (_agc_smoothed_rms < 1.0 && raw_rms > 1.0)
        _agc_smoothed_rms = raw_rms;
    else
        _agc_smoothed_rms += kRmsSmooth * (raw_rms - _agc_smoothed_rms);

    double target_gain = 1.0;
    if (raw_rms >= kSilenceRms && _agc_smoothed_rms > 1.0)
        target_gain = kTargetRms / _agc_smoothed_rms;
    target_gain = std::max(kMinGain, std::min(kMaxGain, target_gain));
    const double coeff = (target_gain < _agc_gain) ? kReleaseCoeff : kAttackCoeff;
    _agc_gain += coeff * (target_gain - _agc_gain);

    for (auto& s : samples) {
        double v = static_cast<double>(s) * _agc_gain;
        if (v > 32767.0) v = 32767.0;
        if (v < -32768.0) v = -32768.0;
        s = static_cast<int16_t>(v);
    }
}

int MiniAudioCapture::initialize(std::string& err_msg) {
    return initialize("", err_msg);
}

int MiniAudioCapture::initialize(const std::string& device_id, std::string& err_msg) {
    if (_impl->device_initialized) {
        err_msg = "already initialized";
        return -1;
    }

    // Init context
    if (!_impl->context_initialized) {
        if (ma_context_init(nullptr, 0, nullptr, &_impl->context) != MA_SUCCESS) {
            err_msg = "miniaudio: context init failed";
            return -1;
        }
        _impl->context_initialized = true;
    }

    // Device config: mono, s32, 16000 Hz.
    // PipeWire/Pulse inside distrobox can report s16 while delivering s32-shaped data;
    // requesting s32 keeps the callback interpretation stable, then we down-convert to s16.
    ma_device_config cfg = ma_device_config_init(ma_device_type_capture);
    cfg.capture.format   = ma_format_s32;
    cfg.capture.channels = 1;
    cfg.sampleRate       = 16000;
    cfg.dataCallback     = MiniAudioCapture::data_callback;
    cfg.notificationCallback = Impl::notification_callback;
    cfg.pUserData        = this;

    // An empty selection explicitly opts into the normal default device.
    // A named selection must resolve uniquely; never record a different input.
    ma_device_id selected_device{};
    if (!device_id.empty()) {
        ma_device_info* capture_infos = nullptr;
        ma_uint32 capture_count = 0;
        if (ma_context_get_devices(&_impl->context, nullptr, nullptr,
                                    &capture_infos, &capture_count) != MA_SUCCESS ||
            (capture_count > 0 && capture_infos == nullptr)) {
            err_msg = "miniaudio: capture device enumeration failed";
            return -1;
        }
        bool found = false;
        for (ma_uint32 i = 0; i < capture_count; ++i) {
            if (device_id == capture_infos[i].name) {
                if (found) {
                    err_msg = "miniaudio: capture device name is ambiguous";
                    return -1;
                }
                selected_device = capture_infos[i].id;
                found = true;
            }
        }
        if (!found) {
            err_msg = "miniaudio: requested capture device not found";
            return -1;
        }
        cfg.capture.pDeviceID = &selected_device;
    }

    if (ma_device_init(&_impl->context, &cfg, &_impl->device) != MA_SUCCESS) {
        err_msg = "miniaudio: device init failed";
        return -1;
    }
    _impl->device_initialized = true;
    _impl->selected_name = device_id;
    return 0;
}

int MiniAudioCapture::start(std::string& err_msg) {
    if (!_impl->device_initialized) {
        err_msg = "not initialized";
        return -1;
    }
    if (_running.load()) return 0; // already running

    _source_lost.store(false);
    _impl->stopped_notification.store(false);
    _impl->watching.store(true);
    if (ma_device_start(&_impl->device) != MA_SUCCESS) {
        _impl->watching.store(false);
        err_msg = "miniaudio: device start failed";
        return -1;
    }
    _running.store(true);
    return 0;
}

void MiniAudioCapture::stop() {
    if (!_running.load()) return;
    _impl->watching.store(false); // Ignore the notification generated by our own Stop.
    if (_impl->device_initialized) {
        ma_device_stop(&_impl->device);
    }
    _running.store(false);
}

bool MiniAudioCapture::source_lost() {
    if (_source_lost.load()) return true;
    if (!_running.load() || !_impl->stopped_notification.load() || _impl->selected_name.empty()) return false;
    // Pulse maps suspension to "stopped", which alone is not a disconnect.
    // Enumerate only after that event, never on the backend callback thread.
    ma_device_info* infos = nullptr;
    ma_uint32 count = 0;
    if (ma_context_get_devices(&_impl->context, nullptr, nullptr, &infos, &count) != MA_SUCCESS ||
        (count > 0 && !infos)) return false;
    for (ma_uint32 i = 0; i < count; ++i)
        if (_impl->selected_name == infos[i].name) return false;
    _source_lost.store(true);
    return true;
}

void MiniAudioCapture::release() {
    stop();
    if (_impl->device_initialized) {
        ma_device_uninit(&_impl->device);
        _impl->device_initialized = false;
    }
    if (_impl->context_initialized) {
        ma_context_uninit(&_impl->context);
        _impl->context_initialized = false;
    }
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    _pcm_buffer.clear();
}

std::vector<int16_t> MiniAudioCapture::get_samples() const {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    return _pcm_buffer;
}

void MiniAudioCapture::clear_samples() {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    _pcm_buffer.clear();
}

void MiniAudioCapture::discard_samples(size_t count) {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    if (count >= _pcm_buffer.size()) {
        _pcm_buffer.clear();
    } else {
        _pcm_buffer.erase(_pcm_buffer.begin(),
                          _pcm_buffer.begin() + static_cast<std::ptrdiff_t>(count));
    }
}

static double compute_rms(const int16_t* data, size_t count) {
    if (count == 0) return 0.0;
    double sum = 0.0;
    for (size_t i = 0; i < count; ++i) {
        double s = data[i];
        sum += s * s;
    }
    return std::sqrt(sum / static_cast<double>(count));
}

double MiniAudioCapture::get_rms_level() const {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    return compute_rms(_pcm_buffer.data(), _pcm_buffer.size());
}

double MiniAudioCapture::get_recent_rms_level(size_t max_samples) const {
    std::lock_guard<std::mutex> lk(_buffer_mutex);
    size_t n = _pcm_buffer.size();
    size_t start = (n > max_samples) ? (n - max_samples) : 0;
    return compute_rms(_pcm_buffer.data() + start, n - start);
}

} // namespace mobmate

#endif // !_WIN32
