#ifdef _WIN32

#include "mobmate_native_core/audio_capture.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <Functiondiscoverykeys_devpkey.h>

// 100-nanoseconds per second
static constexpr LONGLONG REFTIMES_PER_SEC = 10000000;

namespace mobmate {

// CoInitializeEx flags
static constexpr DWORD COINIT_MULTITHREADED_VALUE = 0x0;

WasapiCapture::WasapiCapture() = default;

WasapiCapture::~WasapiCapture() {
    stop();
    release();
}

int WasapiCapture::initialize(std::string& err_msg) {
    return initialize("", err_msg);
}

static std::string wide_to_utf8(const wchar_t* value) {
    if (!value) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) return "";
    std::string out(static_cast<size_t>(size), '\0');
    if (WideCharToMultiByte(CP_UTF8, 0, value, -1, out.data(), size, nullptr, nullptr) <= 0) return "";
    out.resize(static_cast<size_t>(size - 1));
    return out;
}

static std::string device_friendly_name(IMMDevice* device) {
    IPropertyStore* props = nullptr;
    HRESULT hr = device->OpenPropertyStore(STGM_READ, &props);
    if (FAILED(hr) || !props) return "";
    PROPVARIANT var_name;
    PropVariantInit(&var_name);
    std::string name;
    hr = props->GetValue(PKEY_Device_FriendlyName, &var_name);
    if (SUCCEEDED(hr) && var_name.vt == VT_LPWSTR) {
        name = wide_to_utf8(var_name.pwszVal);
    }
    PropVariantClear(&var_name);
    props->Release();
    return name;
}

std::vector<AudioDeviceInfo> enumerate_capture_devices(std::string& err_msg) {
    std::vector<AudioDeviceInfo> devices;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED_VALUE);
    bool com_initialized = SUCCEEDED(hr);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        err_msg = "CoInitializeEx failed: 0x" + std::to_string(hr);
        return devices;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                          CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                          reinterpret_cast<void**>(&enumerator));
    if (FAILED(hr) || !enumerator) {
        err_msg = "CoCreateInstance(MMDeviceEnumerator) failed: 0x" + std::to_string(hr);
        if (com_initialized) CoUninitialize();
        return devices;
    }

    std::string default_id;
    IMMDevice* default_device = nullptr;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &default_device)) && default_device) {
        LPWSTR id = nullptr;
        if (SUCCEEDED(default_device->GetId(&id)) && id) {
            default_id = wide_to_utf8(id);
            CoTaskMemFree(id);
        }
        default_device->Release();
    }

    IMMDeviceCollection* collection = nullptr;
    hr = enumerator->EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, &collection);
    if (FAILED(hr) || !collection) {
        err_msg = "EnumAudioEndpoints failed: 0x" + std::to_string(hr);
        enumerator->Release();
        if (com_initialized) CoUninitialize();
        return devices;
    }

    UINT count = 0;
    collection->GetCount(&count);
    for (UINT i = 0; i < count; ++i) {
        IMMDevice* device = nullptr;
        if (FAILED(collection->Item(i, &device)) || !device) continue;
        LPWSTR id_w = nullptr;
        std::string id;
        if (SUCCEEDED(device->GetId(&id_w)) && id_w) {
            id = wide_to_utf8(id_w);
            CoTaskMemFree(id_w);
        }
        std::string name = device_friendly_name(device);
        devices.push_back({id, name.empty() ? id : name, !default_id.empty() && id == default_id});
        device->Release();
    }

    collection->Release();
    enumerator->Release();
    if (com_initialized) CoUninitialize();
    return devices;
}

int WasapiCapture::initialize(const std::string& device_id, std::string& err_msg) {
    // Initialize COM
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED_VALUE);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) {
        err_msg = "CoInitializeEx failed: 0x" + std::to_string(hr);
        return -1;
    }
    _com_initialized = SUCCEEDED(hr);

    // Create device enumerator
    IMMDeviceEnumerator* enumerator = nullptr;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr,
                          CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                          reinterpret_cast<void**>(&enumerator));
    if (FAILED(hr)) {
        err_msg = "CoCreateInstance(MMDeviceEnumerator) failed: 0x" + std::to_string(hr);
        return -1;
    }

    IMMDevice* device = nullptr;
    if (device_id.empty()) {
        hr = enumerator->GetDefaultAudioEndpoint(eCapture, eConsole, &device);
    } else {
        int wide_size = MultiByteToWideChar(CP_UTF8, 0, device_id.c_str(), -1, nullptr, 0);
        if (wide_size <= 1) {
            enumerator->Release();
            err_msg = "Invalid UTF-8 capture device ID";
            return -1;
        }
        std::wstring wide_id(static_cast<size_t>(wide_size), L'\0');
        if (MultiByteToWideChar(CP_UTF8, 0, device_id.c_str(), -1, wide_id.data(), wide_size) <= 0) {
            enumerator->Release();
            err_msg = "Failed to convert capture device ID to UTF-16";
            return -1;
        }
        hr = enumerator->GetDevice(wide_id.c_str(), &device);
    }
    enumerator->Release();
    if (FAILED(hr)) {
        err_msg = (device_id.empty() ? "GetDefaultAudioEndpoint failed: 0x" : "GetDevice failed: 0x") + std::to_string(hr);
        return -1;
    }

    // Activate audio client
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                          reinterpret_cast<void**>(&_audio_client));
    device->Release();
    if (FAILED(hr)) {
        err_msg = "Activate(IAudioClient) failed: 0x" + std::to_string(hr);
        return -1;
    }

    // Get device mix format (shared mode requires this)
    WAVEFORMATEX* device_format = nullptr;
    hr = _audio_client->GetMixFormat(&device_format);
    if (FAILED(hr)) {
        err_msg = "GetMixFormat failed: 0x" + std::to_string(hr);
        return -1;
    }

    // Store device format info for capture loop
    _device_channels = device_format->nChannels;
    _device_sample_rate = device_format->nSamplesPerSec;
    _device_bits_per_sample = device_format->wBitsPerSample;
    _device_bytes_per_sample = _device_channels > 0 ? device_format->nBlockAlign / _device_channels : 0;
    _device_is_float = device_format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    if (device_format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && device_format->cbSize >= 22) {
        auto* ext = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(device_format);
        _device_is_float = IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    }
    if (_device_channels == 0 || _device_sample_rate == 0 || _device_bytes_per_sample == 0) {
        CoTaskMemFree(device_format);
        err_msg = "Unsupported capture mix format";
        return -1;
    }

    // Initialize audio client with device's mix format
    REFERENCE_TIME duration = REFTIMES_PER_SEC / 10; // 100ms buffer
    hr = _audio_client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, duration, 0,
                                    device_format, nullptr);
    CoTaskMemFree(device_format);
    if (FAILED(hr)) {
        err_msg = "IAudioClient::Initialize failed: 0x" + std::to_string(hr);
        return -1;
    }

    // Get capture client
    hr = _audio_client->GetService(__uuidof(IAudioCaptureClient),
                                    reinterpret_cast<void**>(&_capture_client));
    if (FAILED(hr)) {
        err_msg = "GetService(IAudioCaptureClient) failed: 0x" + std::to_string(hr);
        return -1;
    }

    return 0;
}

int WasapiCapture::start(std::string& err_msg) {
    if (_running.load()) {
        err_msg = "Capture already running";
        return -1;
    }

    if (!_audio_client || !_capture_client) {
        err_msg = "Capture is not initialized";
        return -1;
    }
    HRESULT hr = _audio_client->Start();
    if (FAILED(hr)) {
        err_msg = "IAudioClient::Start failed: 0x" + std::to_string(hr);
        return -1;
    }

    _running.store(true);
    _should_stop.store(false);
    {
        std::lock_guard<std::mutex> lock(_buffer_mutex);
        _pcm_buffer.clear();
        _resample_pending.clear();
        _resample_pos = 0.0;
    }

    try {
        _capture_thread = std::thread([this]() {
            try { capture_loop(); }
            catch (...) {
                std::fprintf(stderr, "[WASAPI] capture thread terminated by C++ exception\n");
                std::fflush(stderr);
                _should_stop.store(true);
                _running.store(false);
            }
        });
    } catch (...) {
        _audio_client->Stop();
        _running.store(false);
        _should_stop.store(true);
        err_msg = "Failed to create capture thread";
        return -1;
    }

    return 0;
}

void WasapiCapture::stop() {
    _should_stop.store(true);
    if (_capture_thread.joinable()) {
        _capture_thread.join();
    }
    if (_audio_client) {
        _audio_client->Stop();
    }
    _running.store(false);
}

void WasapiCapture::release() {
    stop();
    if (_capture_client) {
        _capture_client->Release();
        _capture_client = nullptr;
    }
    if (_audio_client) {
        _audio_client->Release();
        _audio_client = nullptr;
    }
    if (_com_initialized) {
        CoUninitialize();
        _com_initialized = false;
    }
}

void WasapiCapture::capture_loop() {
    HRESULT com_hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED_VALUE);
    const bool thread_com_initialized = SUCCEEDED(com_hr);
    while (!_should_stop.load()) {
        if (!_capture_client) break;

        BYTE* buffer = nullptr;
        DWORD flags = 0;

        UINT32 num_frames_u32 = 0;
        HRESULT hr = _capture_client->GetBuffer(&buffer, &num_frames_u32, &flags, nullptr, nullptr);
        if (FAILED(hr)) {
            Sleep(10);
            continue;
        }

        // AUDCLNT_S_BUFFER_EMPTY: no packet was acquired, so ReleaseBuffer must not be called.
        if (num_frames_u32 == 0) {
            Sleep(5);
            continue;
        }

        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || !buffer) {
            // A successful silent packet may legally return a null data pointer.
            std::vector<int16_t> silence(num_frames_u32, 0);
            {
                std::lock_guard<std::mutex> lock(_buffer_mutex);
                append_mono_samples_locked(silence);
            }
            _capture_client->ReleaseBuffer(num_frames_u32);
            continue;
        }

        if (num_frames_u32 > 0) {
            // Convert to mono 16-bit PCM by downmixing channels
            // Assuming interleaved format
            UINT32 channels = _device_channels;
            UINT32 bits = _device_bits_per_sample;
            std::vector<int16_t> mono_samples;
            mono_samples.reserve(num_frames_u32);

            const UINT32 bytes = _device_bytes_per_sample;
            if (bits == 16 && bytes == 2) {
                auto* src = reinterpret_cast<int16_t*>(buffer);
                for (UINT32 i = 0; i < num_frames_u32; ++i) {
                    // Simple downmix: average all channels
                    int32_t sum = 0;
                    for (UINT32 ch = 0; ch < channels; ++ch) {
                        sum += src[i * channels + ch];
                    }
                    mono_samples.push_back(static_cast<int16_t>(sum / channels));
                }
            } else if (bits == 32 && bytes == 4 && _device_is_float) {
                auto* src = reinterpret_cast<float*>(buffer);
                for (UINT32 i = 0; i < num_frames_u32; ++i) {
                    double sum = 0.0;
                    for (UINT32 ch = 0; ch < channels; ++ch) {
                        sum += static_cast<double>(src[i * channels + ch]);
                    }
                    double avg = sum / channels;
                    if (avg > 1.0) avg = 1.0;
                    if (avg < -1.0) avg = -1.0;
                    mono_samples.push_back(static_cast<int16_t>(avg * 32767.0));
                }
            } else if (bytes == 4) {
                auto* src = reinterpret_cast<int32_t*>(buffer);
                for (UINT32 i = 0; i < num_frames_u32; ++i) {
                    int64_t sum = 0;
                    for (UINT32 ch = 0; ch < channels; ++ch) {
                        sum += src[i * channels + ch] >> 16;
                    }
                    int32_t avg = static_cast<int32_t>(sum / channels);
                    // Clamp to 16-bit range
                    if (avg > 32767) avg = 32767;
                    if (avg < -32768) avg = -32768;
                    mono_samples.push_back(static_cast<int16_t>(avg));
                }
            } else if (bytes == 3) {
                for (UINT32 i = 0; i < num_frames_u32; ++i) {
                    int32_t sum = 0;
                    for (UINT32 ch = 0; ch < channels; ++ch) {
                        const BYTE* p = buffer + (static_cast<size_t>(i) * channels + ch) * 3;
                        int32_t sample = static_cast<int32_t>(p[0]) |
                                         (static_cast<int32_t>(p[1]) << 8) |
                                         (static_cast<int32_t>(p[2]) << 16);
                        if (sample & 0x00800000) sample |= static_cast<int32_t>(0xFF000000);
                        sum += sample >> 8;
                    }
                    mono_samples.push_back(static_cast<int16_t>(sum / channels));
                }
            } else if (bytes == 1) {
                for (UINT32 i = 0; i < num_frames_u32; ++i) {
                    int32_t sum = 0;
                    for (UINT32 ch = 0; ch < channels; ++ch)
                        sum += (static_cast<int32_t>(buffer[i * channels + ch]) - 128) << 8;
                    mono_samples.push_back(static_cast<int16_t>(sum / channels));
                }
            } else {
                mono_samples.assign(num_frames_u32, 0);
            }

            std::lock_guard<std::mutex> lock(_buffer_mutex);
            append_mono_samples_locked(mono_samples);
        }

        _capture_client->ReleaseBuffer(num_frames_u32);
    }
    if (thread_com_initialized) CoUninitialize();
    _running.store(false);
}

void WasapiCapture::append_mono_samples_locked(const std::vector<int16_t>& mono_samples) {
    if (mono_samples.empty()) return;

    std::vector<int16_t> adjusted = mono_samples;
    apply_agc_locked(adjusted);

    if (_device_sample_rate == 16000) {
        _pcm_buffer.insert(_pcm_buffer.end(), adjusted.begin(), adjusted.end());
        return;
    }

    _resample_pending.insert(_resample_pending.end(), adjusted.begin(), adjusted.end());
    const double step = static_cast<double>(_device_sample_rate) / 16000.0;
    while (_resample_pos + 1.0 < static_cast<double>(_resample_pending.size())) {
        size_t i = static_cast<size_t>(_resample_pos);
        double frac = _resample_pos - static_cast<double>(i);
        double a = static_cast<double>(_resample_pending[i]);
        double b = static_cast<double>(_resample_pending[i + 1]);
        double v = a + (b - a) * frac;
        if (v > 32767.0) v = 32767.0;
        if (v < -32768.0) v = -32768.0;
        _pcm_buffer.push_back(static_cast<int16_t>(v));
        _resample_pos += step;
    }

    size_t consumed = static_cast<size_t>(_resample_pos);
    if (consumed > 0) {
        _resample_pending.erase(_resample_pending.begin(), _resample_pending.begin() + consumed);
        _resample_pos -= static_cast<double>(consumed);
    }
}

void WasapiCapture::apply_agc_locked(std::vector<int16_t>& samples) {
    if (samples.empty()) return;
    static constexpr double kTargetRms = 0.08 * 32768.0; // ~-22dBFS
    static constexpr double kMaxGain = 60.0;
    static constexpr double kMinGain = 0.3;
    static constexpr double kSilenceRms = 1.0;
    static constexpr double kRmsSmooth = 0.08;
    static constexpr double kAttackCoeff = 0.02;  // gain increase: slow
    static constexpr double kReleaseCoeff = 0.15; // gain decrease: fast

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
    target_gain = (std::max)(kMinGain, (std::min)(kMaxGain, target_gain));
    const double coeff = (target_gain < _agc_gain) ? kReleaseCoeff : kAttackCoeff;
    _agc_gain += coeff * (target_gain - _agc_gain);

    for (auto& s : samples) {
        double v = static_cast<double>(s) * _agc_gain;
        if (v > 32767.0) v = 32767.0;
        if (v < -32768.0) v = -32768.0;
        s = static_cast<int16_t>(v);
    }
}

std::vector<int16_t> WasapiCapture::get_samples() const {
    std::lock_guard<std::mutex> lock(_buffer_mutex);
    return _pcm_buffer;
}

void WasapiCapture::clear_samples() {
    std::lock_guard<std::mutex> lock(_buffer_mutex);
    _pcm_buffer.clear();
}

void WasapiCapture::discard_samples(size_t count) {
    std::lock_guard<std::mutex> lock(_buffer_mutex);
    if (count >= _pcm_buffer.size()) {
        _pcm_buffer.clear();
        return;
    }
    _pcm_buffer.erase(_pcm_buffer.begin(), _pcm_buffer.begin() + static_cast<std::ptrdiff_t>(count));
}

double WasapiCapture::get_rms_level() const {
    std::lock_guard<std::mutex> lock(_buffer_mutex);
    if (_pcm_buffer.empty()) return 0.0;

    double sum_sq = 0.0;
    for (int16_t s : _pcm_buffer) {
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
    }
    return std::sqrt(sum_sq / _pcm_buffer.size());
}

double WasapiCapture::get_recent_rms_level(size_t max_samples) const {
    std::lock_guard<std::mutex> lock(_buffer_mutex);
    if (_pcm_buffer.empty()) return 0.0;

    size_t count = max_samples < _pcm_buffer.size() ? max_samples : _pcm_buffer.size();
    size_t start = _pcm_buffer.size() - count;
    double sum_sq = 0.0;
    for (size_t i = start; i < _pcm_buffer.size(); ++i) {
        int16_t s = _pcm_buffer[i];
        sum_sq += static_cast<double>(s) * static_cast<double>(s);
    }
    return std::sqrt(sum_sq / count);
}

} // namespace mobmate

#endif // _WIN32
