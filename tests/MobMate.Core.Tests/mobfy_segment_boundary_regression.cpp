// Device-free control tests, not speech/model/quality/real-time performance tests.
#include "mobmate_native_core/moonshine_bridge.h"
#include "mobmate_native_core/voice_conversion.h"
#include "../../native/mobmate_native_core/src/vc_onset_filler.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <filesystem>
#include <future>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
using Result = mobmate::MoonshineBridge::TranscribeResult;
struct Control {
    std::atomic<int> mode{0}; // 0=text, 1=normal empty, 2=failed result, 3=no result, 4=init fault, 5=model fault
    std::atomic<int> asr_calls{0}, vc_calls{0};
    std::atomic<int> asr_permits{0}, vc_permits{0};
    std::atomic<size_t> conversion_lengths[16]{};
    std::atomic<bool> conversion_ok{true};
    std::atomic<int> init_calls{0}, load_calls{0};
    std::atomic<bool> asr_input_zero{false}, conversion_input_zero{false};
    bool real_asr = false;
    std::string load_path;
    int load_arch = -1;
    mobmate::MoonshineBridge::LoadOptions load_options;
    bool load_success = false;
    std::atomic<double> vad_probability{0.99};
    std::atomic<size_t> samples{0};
    std::mutex mutex;
    std::condition_variable cv;
    std::vector<std::string> events;
    int done = 0, ready = 0, cancelled = 0, route_unloads = 0, route_stops = 0;
} *control = nullptr;

void require(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
template<class F> void wait_for(F predicate) {
    std::unique_lock<std::mutex> lock(control->mutex);
    require(control->cv.wait_for(lock, std::chrono::seconds(control->real_asr ? 30 : 8), predicate), "fixture barrier timed out");
}
template<class F> void notify_control_update(F update) {
    {
        std::lock_guard<std::mutex> lock(control->mutex);
        update();
    }
    control->cv.notify_all();
}
void permit(int asr_count, int vc_count) {
    notify_control_update([&] {
        control->asr_permits.store(asr_count);
        control->vc_permits.store(vc_count);
    });
}
void permit(int count) { permit(count, count); }
class VcBoundaryAsrBridge {
    std::unique_ptr<mobmate::MoonshineBridge> real_;
public:
    bool init(const std::string& dir) {
        ++control->init_calls;
        if (!control->real_asr) return control->mode.load() != 4;
        real_ = std::make_unique<mobmate::MoonshineBridge>();
        return real_->init(dir);
    }
    bool load_model(const std::string& path, int arch, const mobmate::MoonshineBridge::LoadOptions& options) {
        const bool success = control->real_asr ? real_->load_model(path, arch, options) : control->mode.load() != 5;
        std::lock_guard<std::mutex> lock(control->mutex);
        ++control->load_calls;
        control->load_path = path;
        control->load_arch = arch;
        control->load_options = options;
        control->load_success = success;
        return success;
    }
    std::vector<Result> transcribeAccurate(const float* pcm, int count, int rate, int leading, int tail) {
        require(rate == 16000, "recognizer sample rate");
        int call = 0;
        notify_control_update([&] { call = ++control->asr_calls; });
        wait_for([&] { return control->asr_permits.load() >= call; });
        if (control->real_asr) {
            control->asr_input_zero.store(count >= 4000 && std::all_of(pcm, pcm + count, [](float v) { return v == 0.0f; }));
            return real_->transcribeAccurate(pcm, count, rate, leading, tail);
        }
        int mode = control->mode.load();
        if (mode == 3) return {};
        Result result{};
        result.success = mode != 2;
        result.text = mode == 0 ? "abcdef" : ""; // fabricated text, not an audio transcript
        result.error = mode == 2 ? "simulated result failure" : "";
        result.confidence_score = 0.95f;
        return {result};
    }
};
bool vc_boundary_test_convert(int16_t* samples, size_t count, int rate) {
    require(samples && count && rate == 48000, "converter input");
    if (control->real_asr)
        control->conversion_input_zero.store(std::all_of(samples, samples + count, [](int16_t v) { return v == 0; }));
    int call = 0;
    notify_control_update([&] {
        call = ++control->vc_calls;
        require(call < 16, "fixture conversion capacity");
        control->conversion_lengths[call].store(count);
    });
    wait_for([&] { return control->vc_permits.load() >= call; });
    if (!control->conversion_ok.load()) return false;
    std::fill(samples, samples + count, static_cast<int16_t>(700 + call));
    return true;
}
double vc_boundary_test_vad() { return control->vad_probability.load(); }
void vc_boundary_test_event(const char* event, const char* outcome, const char* decision) {
    if (!control) return;
    std::lock_guard<std::mutex> lock(control->mutex);
    control->events.push_back(std::string(event) + ":" + outcome + ":" + decision +
        ":input_ms=" + std::to_string(control->samples.load() * 1000 / 48000));
    if (std::string(event) == "job_done") ++control->done;
    if (std::string(event) == "asr_ready") ++control->ready;
    if (std::string(event) == "cancelled") ++control->cancelled;
    if (std::string(event) == "route_unloaded") ++control->route_unloads;
    if (std::string(event) == "route_stopped") ++control->route_stops;
    control->cv.notify_all();
}
}

#define MOBMATE_VC_BOUNDARY_TEST
#ifndef MOBMATE_VC_SOURCE
#define MOBMATE_VC_SOURCE "../../native/mobmate_native_core/src/voice_conversion.cpp"
#endif
#if !defined(_WIN32) && !defined(MOBMATE_VC_BOUNDARY_BASELINE)
// Candidate-only miniaudio seam. The default is the existing context failure;
// lifecycle tests use callback-only fake devices, never an audio backend.
#include "miniaudio.h"
static bool fake_devices = false;
static std::atomic<bool> fake_model_load_ok{false};
static ma_device *fake_capture = nullptr, *fake_playback = nullptr;
static ma_device_data_proc fake_capture_callback = nullptr, fake_playback_callback = nullptr;
static std::vector<int16_t> first_playback;
static ma_result vc_boundary_test_context_init(const ma_backend*, ma_uint32,
                                                const ma_context_config*, ma_context*) {
    return fake_devices ? MA_SUCCESS : MA_ERROR;
}
static ma_result vc_boundary_test_context_uninit(ma_context*) { return MA_SUCCESS; }
static ma_result vc_boundary_test_get_devices(ma_context*, ma_device_info** playback, ma_uint32* playback_count,
                                              ma_device_info** capture, ma_uint32* capture_count) {
    *playback = nullptr; *playback_count = 0; *capture = nullptr; *capture_count = 0;
    return MA_SUCCESS;
}
static ma_result vc_boundary_test_device_init(ma_context*, const ma_device_config* cfg, ma_device* dev) {
    dev->pUserData = cfg->pUserData;
    if (cfg->deviceType == ma_device_type_capture) {
        fake_capture = dev; fake_capture_callback = cfg->dataCallback;
    } else {
        fake_playback = dev; fake_playback_callback = cfg->dataCallback;
    }
    return MA_SUCCESS;
}
static ma_result vc_boundary_test_device_info(ma_device*, ma_device_type, ma_device_info*) { return MA_ERROR; }
static ma_result vc_boundary_test_device_start(ma_device* dev) {
    if (dev == fake_capture) {
        std::vector<int16_t> input(1536, 3000);
        fake_capture_callback(dev, nullptr, input.data(), static_cast<ma_uint32>(input.size()));
    } else if (dev == fake_playback) {
        first_playback.assign(1536, -1234);
        fake_playback_callback(dev, first_playback.data(), nullptr, static_cast<ma_uint32>(first_playback.size()));
    }
    return MA_SUCCESS;
}
static ma_result vc_boundary_test_device_stop(ma_device*) { return MA_SUCCESS; }
static void vc_boundary_test_device_uninit(ma_device*) {}
static int vc_boundary_test_load_onnx(mobmate::vc::VcOnnxPipeline* pipeline);
#define ma_context_init vc_boundary_test_context_init
#define ma_context_uninit vc_boundary_test_context_uninit
#define ma_context_get_devices vc_boundary_test_get_devices
#define ma_device_init vc_boundary_test_device_init
#define ma_device_get_info vc_boundary_test_device_info
#define ma_device_start vc_boundary_test_device_start
#define ma_device_stop vc_boundary_test_device_stop
#define ma_device_uninit vc_boundary_test_device_uninit
#endif
#include MOBMATE_VC_SOURCE
#if !defined(_WIN32) && !defined(MOBMATE_VC_BOUNDARY_BASELINE)
#undef ma_context_init
#undef ma_context_uninit
#undef ma_context_get_devices
#undef ma_device_init
#undef ma_device_get_info
#undef ma_device_start
#undef ma_device_stop
#undef ma_device_uninit
static int vc_boundary_test_load_onnx(mobmate::vc::VcOnnxPipeline* pipeline) {
    if (!fake_model_load_ok.load()) return -77;
    pipeline->loaded = true;
    return 0;
}
#endif

using namespace mobmate::vc;
namespace {
#ifdef MOBMATE_VC_BOUNDARY_BASELINE
constexpr int fault_release = 1;
#else
constexpr int fault_release = 0;
#endif
struct Fixture {
    Control ctl;
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
    VcOnsetFiller filler;
#endif
    VcAudioState state;
    VcRtRvc rvc;
    size_t nonzero = 0, callbacks = 0, playback_callbacks = 0;
    size_t boundary_segment_samples = 0, boundary_job_samples = 0, boundary_preroll_samples = 0;
    std::vector<int16_t> released;
    explicit Fixture(int mode = 0, bool raw = false, bool assist = true,
                     const std::string& language = "ja", const std::string& dll_dir = "fixture-only-no-dll",
                     bool real_asr = false) {
        control = &ctl;
        ctl.mode.store(mode);
        ctl.real_asr = real_asr;
        state.debug_dump_enabled = false;
        state.wav_output_enabled = false;
        state.dsp.enabled = false; // isolate control, not DSP/model quality
        state.output_auto_gain_enabled = false;
        state.sample_rate = rvc.sample_rate = 48000;
        state.ring.init(48000); // The non-RVC case uses the normal Start-initialized route.
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
        state.filler = &filler;
        state.filler_monitor_ring.init(48000);
#endif
        state.vad = reinterpret_cast<silero_vad_handle*>(this); // only the test scorer sees it
        state.vad_hold_frames = 0;
        state.rt_rvc = &rvc;
        state.raw_rvc_mode = raw;
        state.laugh_asr_assist_enabled = assist;
        state.laugh_asr_moonshine_dll_dir = dll_dir;
        state.laugh_asr_moonshine_model_root = "fixture-only-no-model";
        state.laugh_asr_language = language;
        rvc.post_ring.init(48000 * 20);
        rvc.running.store(true); // no RT inference thread and no ma_device_start
        state.capture_dev.pUserData = &state;
        state.playback_dev.pUserData = &state;
        state.start_laugh_asr_worker();
    }
    ~Fixture() {
        permit(1000);
        state.stop_laugh_asr_worker();
        state.vad = nullptr;
        state.rt_rvc = nullptr;
        rvc.running.store(false);
        control = nullptr;
    }
    void feed(int frames, bool speech, int waveform = 0, bool zero = false) {
        ctl.vad_probability.store(speech ? 0.99 : 0.0);
        for (int frame = 0; frame < frames; ++frame) {
            std::vector<int16_t> pcm(1536, 0); // exactly 512 VAD samples, 32ms at 48kHz
            for (size_t i = 0; i < pcm.size(); ++i) {
                if (speech && !zero) pcm[i] = static_cast<int16_t>(3000 * std::sin(6.283185307 * 180 * i / 48000));
                else if (waveform == 1 && i == 128) pcm[i] = 9000;
                else if (waveform == 2) pcm[i] = ((i * 1103515245u + 12345u) >> 16) % 1600 - 800;
            }
            ctl.samples.fetch_add(pcm.size());
            ++callbacks;
            VcAudioState::on_capture(&state.capture_dev, nullptr, pcm.data(), static_cast<ma_uint32>(pcm.size()));
        }
    }
    void final_segment(int speech_frames = 20) { feed(speech_frames, true); feed(1, false); }
    void finish(int count = 1) {
        permit(count);
        wait_for([&] { return ctl.done >= count; });
    }
    void drain() {
        // Invoke the actual output callback, including its short-read silence,
        // without opening/starting a device. A sentinel catches unfilled output.
        const size_t available = rvc.post_ring.available();
        std::vector<int16_t> pcm(available + 1536, -1234);
        VcAudioState::on_playback(&state.playback_dev, pcm.data(), nullptr, static_cast<ma_uint32>(pcm.size()));
        ++playback_callbacks;
        require(std::all_of(pcm.begin() + available, pcm.end(), [](int16_t v) { return v == 0; }),
                "playback underrun did not produce silence");
        for (auto sample : pcm) {
            if (!sample) continue; // callback keeps the playback clock fed with silence
            require(sample >= 701 && sample <= 710, "raw/unconverted PCM reached isolated sink");
            ++nonzero;
            released.push_back(sample);
        }
    }
    void record_boundary() {
        boundary_segment_samples = state.speech_buffer.size();
        boundary_job_samples = resample_linear_i16(state.speech_buffer.data(), state.speech_buffer.size(), 48000, 16000).size();
    }
    std::string outcome() {
        std::lock_guard<std::mutex> lock(state.laugh_asr_mutex);
#ifdef MOBMATE_VC_BOUNDARY_BASELINE
        return "baseline_unclassified";
#else
        return state.laugh_asr_last_outcome;
#endif
    }
    void report(const char* name, int expected_releases, const char* expected_outcome,
                int policy_violation = 0, int positive_candidate_suppressed = 0) {
        drain();
        int releases = 0;
        for (int marker = 701; marker <= 710; ++marker) {
            auto n = std::count(released.begin(), released.end(), marker);
            if (n) {
                require(static_cast<size_t>(n) == ctl.conversion_lengths[marker - 700].load(),
                        "duplicate/truncated converted segment at sink");
                ++releases;
            }
        }
        require(releases == expected_releases, "release-count regression (including positive control)");
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
        require(outcome() == expected_outcome, "ASR outcome classification");
#endif
        std::lock_guard<std::mutex> lock(ctl.mutex);
        std::cout << "{\"case\":\"" << name << "\",\"callbacks\":" << callbacks
            << ",\"synthetic_samples\":" << ctl.samples.load()
            << ",\"asr_calls\":" << ctl.asr_calls.load() << ",\"vc_calls\":" << ctl.vc_calls.load()
            << ",\"releases\":" << releases << ",\"nonzero_samples\":" << nonzero
            << ",\"sink\":\"device_free_on_playback\",\"playback_callbacks\":" << playback_callbacks
            << ",\"gate_accepts\":" << state.asr_gate_accept_count.load()
            << ",\"gate_rejects\":" << state.asr_gate_reject_count.load()
            << ",\"boundary_segment_samples_48k\":" << boundary_segment_samples
            << ",\"boundary_job_samples_16k\":" << boundary_job_samples
            << ",\"boundary_preroll_samples_48k\":" << boundary_preroll_samples
            << ",\"hold_frames\":" << state.vad_hold_frames
            << ",\"decision\":\"" << state.laugh_asr_last_decision << "\",\"outcome\":\"" << outcome()
            << "\",\"known_safety_gap\":" << policy_violation
            << ",\"positive_candidate_suppressed\":" << positive_candidate_suppressed
            << ",\"conversion_samples\":[";
        for (int call = 1; call <= ctl.vc_calls.load(); ++call) {
            if (call > 1) std::cout << ',';
            std::cout << ctl.conversion_lengths[call].load();
        }
        std::cout << "],\"events\":[";
        for (size_t i = 0; i < ctl.events.size(); ++i) {
            if (i) std::cout << ',';
            std::cout << '"' << ctl.events[i] << '"';
        }
        std::cout << "]}\n";
    }
};

void check_fixture_notification_serialization() {
    Control ctl;
    control = &ctl;
    std::promise<void> started;
    auto started_future = started.get_future();
    std::future<void> update;
    bool serialized = false;
    {
        // A waiter owns this mutex between checking its predicate and waiting.
        // Permit updates must not pass it and notify inside that interval.
        std::unique_lock<std::mutex> lock(ctl.mutex);
        update = std::async(std::launch::async, [&] {
            started.set_value();
            permit(1);
        });
        started_future.get();
        serialized = update.wait_for(std::chrono::milliseconds(100)) == std::future_status::timeout;
    }
    update.get();
    control = nullptr;
    require(serialized, "fixture permit update bypassed the waiter mutex");
    require(ctl.asr_permits.load() == 1 && ctl.vc_permits.load() == 1, "fixture permits lost after handoff");
}

void ordinary_cases() {
    {
        Fixture f;
        f.state.monitor_ok = true;
        f.state.monitor_gain = 1.0f;
        f.state.monitor_ring.init(48000);
        f.state.monitor_dev.pUserData = &f.state;
        f.feed(120, false);
        require(f.rvc.post_available() == 0, "VAD idle silence occupied the RVC body ring");
        const auto underruns = f.state.underrun_count.load();
        f.drain();
        require(f.state.underrun_count.load() == underruns, "intentional idle silence counted as underrun");
        std::vector<int16_t> monitor(1536, -1234);
        VcAudioState::on_monitor_playback(&f.state.monitor_dev, monitor.data(), nullptr,
                                         static_cast<ma_uint32>(monitor.size()));
        require(std::all_of(monitor.begin(), monitor.end(), [](int16_t v) { return v == 0; }),
                "idle monitor output was not silence");
        f.final_segment();
        f.finish();
        require(f.rvc.post_available() == f.ctl.conversion_lengths[1].load(),
                "released body was not the only ready RVC output");
        f.report("idle_silence_before_released_body", 1, "text");
    }
    for (int waveform = 0; waveform < 3; ++waveform) {
        Fixture f;
        f.feed(20, false, waveform);
        require(f.ctl.vc_calls.load() == 0 && f.ctl.asr_calls.load() == 0, "noncandidate entered worker");
        f.report(waveform == 0 ? "silence" : waveform == 1 ? "click_vad_negative" : "noise_vad_negative", 0, "idle");
    }
    const char* names[] = {"recognized_text", "successful_empty", "failed_result", "no_result", "init_failure", "model_failure"};
    const char* outcomes[] = {"text", "normal_empty", "result_failed", "no_result", "init_failed", "model_load_failed"};
    for (int mode = 0; mode < 6; ++mode) {
        Fixture f(mode);
        f.final_segment();
        f.finish();
        f.report(names[mode], mode == 0 ? 1 : mode >= 4 ? fault_release : 0,
                 outcomes[mode], mode >= 4 ? fault_release : 0);
    }
    { Fixture f(0, true); f.final_segment(); f.finish();
      require(f.ctl.asr_calls.load() == 0, "raw opt-in invoked ASR");
      f.report("explicit_raw", 1, "bypassed_raw"); }
    { Fixture f(0, false, false); f.final_segment();
      require(f.ctl.asr_calls.load() == 0, "disabled assist invoked ASR");
      f.report("assist_disabled", 0, "idle"); }
    { Fixture f; f.feed(3, true); f.record_boundary(); f.feed(1, false);
      require(f.boundary_segment_samples == 4608 && f.boundary_job_samples == 1536, "96ms sample accounting");
      require(f.state.asr_gate_reject_count.load() == 1 && f.ctl.asr_calls.load() == 0, "short queue boundary");
      f.report("short_96ms_no_preroll", 0, "idle", 0, 1); }
    { Fixture f; f.ctl.conversion_ok.store(false); f.final_segment(); f.finish();
      f.report("conversion_failed_no_raw_fallback", 0, "text"); }
    { Fixture f; f.final_segment();
      wait_for([&] { return f.ctl.asr_calls.load() == 1; });
      f.drain(); require(f.nonzero == 0, "unresolved ASR released PCM");
      f.state.finish_speech_segment(0); // repeated completion must not queue twice
      f.finish(); f.state.stop_laugh_asr_worker();
      require(f.ctl.vc_calls.load() == 1, "duplicate finalization");
      f.report("pending_then_exactly_once", 1, "text"); }
}

void speculative_cases() {
    { Fixture f; f.state.vad_hold_frames = 24; f.feed(20, true); f.feed(12, false);
      require(f.state.spec_pending_gen == 1, "speculative queue missing");
      permit(1, 0);
      wait_for([&] { return f.ctl.ready == 1; });
      f.drain(); require(f.nonzero == 0, "uncommitted speculative PCM released");
      f.feed(13, false); f.finish(); f.state.stop_laugh_asr_worker();
      require(f.ctl.vc_calls.load() == 1, "spec commit queued duplicate");
      f.report("spec_commit", 1, "text"); }
    { Fixture f; f.state.vad_hold_frames = 24; f.feed(20, true); f.feed(12, false);
      require(f.state.spec_pending_gen == 1, "speculative queue missing");
      f.feed(4, true); permit(1);
      wait_for([&] { return f.ctl.cancelled == 1; });
      f.drain(); require(f.nonzero == 0, "cancelled late result released");
      f.feed(25, false); permit(2);
      wait_for([&] { return f.ctl.done == 1; }); // cancelled job is not a completed/released job
      f.state.stop_laugh_asr_worker();
      require(f.ctl.vc_calls.load() == 2, "resumed segment did not requeue");
      f.report("spec_cancel_resume_final", 1, "text"); }
    { Fixture f; f.state.vad_hold_frames = 24; f.feed(20, true); f.feed(12, false);
      // Request stop while the actual worker is waiting for speculative fate.
      permit(1); wait_for([&] { return f.ctl.ready == 1; });
      f.state.stop_laugh_asr_worker();
      f.report("stop_pending_spec", 0, "pending"); }
    { Fixture f(5); f.final_segment(); f.finish(); f.drain();
      require(f.nonzero == fault_release * f.ctl.conversion_lengths[1].load(), "fault segment leaked before recovery");
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
      require(f.outcome() == "model_load_failed", "fault outcome lost before recovery");
#endif
      f.ctl.mode.store(0); f.final_segment(); f.finish(2); f.state.stop_laugh_asr_worker();
      f.report("model_fault_then_recovery", 1 + fault_release, "text", fault_release);
      require(std::count(f.released.begin(), f.released.end(), 701) ==
                  static_cast<std::ptrdiff_t>(fault_release * f.ctl.conversion_lengths[1].load()),
              "old fault segment released after recovery");
      size_t delivered = f.nonzero; f.drain();
      require(f.nonzero == delivered, "completed segment replayed"); }
    { Fixture f; f.final_segment(); f.finish(); f.drain();
      f.final_segment(); f.finish(2); f.state.stop_laugh_asr_worker();
      f.report("consecutive_segments", 2, "text"); }
    for (int mode : {4, 5}) {
      Fixture f(mode); f.state.vad_hold_frames = 24; f.feed(20, true); f.feed(12, false);
      permit(1); wait_for([&] { return f.ctl.ready == 1; });
      f.feed(4, true); wait_for([&] { return f.ctl.cancelled == 1; });
      f.drain(); require(f.nonzero == 0, "cancelled fault conversion released");
      f.ctl.mode.store(0); f.feed(25, false); permit(2);
      wait_for([&] { return f.ctl.done == 1; }); f.drain();
      // Resuming within hold extends the same physical segment. Conversion 2
      // contains the faulted input again, even though it has a fresh marker.
      require(!f.state.collecting_speech, "faulted physical segment did not end");
      require(f.nonzero == fault_release * f.ctl.conversion_lengths[2].load(),
              "faulted physical segment requeued and released after recovery");
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
      require(f.outcome() == "text", "retry recognizer outcome was overwritten by release policy");
      { std::lock_guard<std::mutex> lock(f.state.laugh_asr_mutex);
        require(f.state.laugh_asr_last_decision == (mode == 4 ? "moonshine_init_failed" : "moonshine_model_load_failed"),
                "physical segment fault disposition lost after retry"); }
#endif
      f.feed(20, true); f.feed(25, false); permit(3);
      wait_for([&] { return f.ctl.done == 2; }); f.state.stop_laugh_asr_worker();
      f.report(mode == 4 ? "spec_init_fault_cancel_then_recovery" : "spec_fault_cancel_then_recovery",
               1 + fault_release, "text", fault_release);
      require(std::count(f.released.begin(), f.released.end(), 701) == 0, "stale speculative fault released");
      require(std::count(f.released.begin(), f.released.end(), 703) ==
                  static_cast<std::ptrdiff_t>(f.ctl.conversion_lengths[3].load()),
              "new healthy physical segment did not recover"); }
}

void queue_boundary_cases() {
    // Isolate the unchanged 4000-sample admission threshold from ASR content
    // policy using the established explicit raw opt-in (still converted PCM).
    for (size_t count : {size_t(3999), size_t(4000)}) {
        Fixture f(0, true);
        f.boundary_job_samples = count;
        f.boundary_segment_samples = count * 3;
        bool queued = f.state.queue_laugh_asr_job(std::vector<int16_t>(count, 3000), {},
            std::vector<int16_t>(count * 3, 3000), 48000);
        require(queued == (count == 4000), "250ms admission threshold changed");
        if (queued) f.finish();
        else require(f.ctl.vc_calls.load() == 0, "subthreshold job entered converter");
        f.report(count == 3999 ? "queue_249_9375ms" : "queue_250ms", queued ? 1 : 0,
                 queued ? "bypassed_raw" : "idle");
    }
    { Fixture f(0, true); f.feed(5, false);
      f.boundary_preroll_samples = f.state.pre_roll_buffer.size();
      f.feed(3, true); f.record_boundary();
      require(f.boundary_preroll_samples == 7680 && f.boundary_job_samples == 4096,
              "96ms plus 160ms preroll sample accounting");
      f.feed(1, false); f.finish();
      f.report("short_96ms_with_160ms_preroll", 1, "bypassed_raw"); }
    { Fixture f(0, true); f.state.vad_hold_frames = 5;
      f.feed(3, true); f.feed(5, false); f.record_boundary();
      require(f.boundary_segment_samples == 12288 && f.boundary_job_samples == 4096,
              "96ms plus 160ms hold sample accounting");
      f.feed(1, false); f.finish();
      f.report("short_96ms_with_160ms_hold", 1, "bypassed_raw"); }
}

// Changes only this test process, with no active worker when changed unless a
// successfully loaded worker is deliberately being tested for retention.
class ScopedTinyModelEnv {
    static constexpr const char* name = "MOBMATE_MOBFY_JA_TINY_MODEL_DIR";
    bool was_set_;
    std::string old_;
    static int update(const char* value) {
#ifdef _WIN32
        return _putenv_s(name, value ? value : "");
#else
        return value ? setenv(name, value, 1) : unsetenv(name);
#endif
    }
public:
    explicit ScopedTinyModelEnv(const char* value) : was_set_(std::getenv(name) != nullptr),
        old_(was_set_ ? std::getenv(name) : "") { set(value); }
    void set(const char* value) { require(update(value) == 0, "test environment update failed"); }
    ~ScopedTinyModelEnv() { (void)update(was_set_ ? old_.c_str() : nullptr); }
};

#ifndef MOBMATE_VC_BOUNDARY_BASELINE
struct FillerBank {
    std::array<std::vector<int16_t>, 5> pcm;
    std::array<mmv2_vc_filler_clip, 5> clips;
    mmv2_vc_filler_update update{};
    FillerBank() {
        for (size_t i = 0; i < clips.size(); ++i) {
            pcm[i].assign(4096, static_cast<int16_t>(1100 + i * 100));
            clips[i] = {i + 1, pcm[i].data(), static_cast<uint32_t>(pcm[i].size()), 0};
        }
        update = {sizeof(update), 1, 1, 1, 48000, 5, 0, clips.data(), 0};
    }
    void publish(Fixture& f, uint64_t generation, bool enabled = true, uint32_t count = 5) {
        update.generation = generation;
        update.enabled = enabled ? 1 : 0;
        update.clip_count = count;
        require(VcOnsetFiller::valid(update, 48000), "fixture filler update");
        auto prepared = VcOnsetFiller::prepare(update);
        std::lock_guard<std::mutex> lock(f.state.route_mutex);
        f.filler.install(update, prepared);
        f.state.filler_monitor_ring.clear();
    }
};

void filler_boundary_cases() {
    static_assert(sizeof(mmv2_vc_filler_snapshot) == 64);
    static_assert(sizeof(void*) != 8 || sizeof(mmv2_vc_filler_clip) == 24);
    static_assert(sizeof(void*) != 8 || sizeof(mmv2_vc_filler_update) == 48);
    static_assert(offsetof(mmv2_vc_filler_update, clips) == 32);
    static_assert(offsetof(mmv2_vc_filler_snapshot, started_clip_id) == 48);
    FillerBank bank;
    void* handle = nullptr;
    require(mmv2_vc_create("{}", &handle) == 0 && handle, "filler ABI create");
    mmv2_vc_filler_snapshot snapshot{sizeof(snapshot), 1};
    require(mmv2_vc_filler_exchange(nullptr, &bank.update, &snapshot) == -1 &&
            mmv2_vc_filler_exchange(handle, nullptr, nullptr) == -1, "filler invalid handles");
    snapshot.version = 2;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == -1, "filler snapshot version");
    snapshot.version = 1;
    --snapshot.size;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == -1, "filler snapshot size");
    ++snapshot.size;
    auto invalid = bank.update;
    for (int kind = 0; kind < 8; ++kind) {
        invalid = bank.update;
        if (kind == 0) --invalid.size;
        if (kind == 1) invalid.version = 2;
        if (kind == 2) invalid.generation = 0;
        if (kind == 3) invalid.enabled = 2;
        if (kind == 4) invalid.sample_rate = 16000;
        if (kind == 5) invalid.clip_count = 6;
        if (kind == 6) invalid.reserved = 1;
        if (kind == 7) invalid.clips = nullptr;
        require(mmv2_vc_filler_exchange(handle, &invalid, nullptr) == -1, "filler update validation");
    }
    const auto original = bank.clips[1];
    for (int kind = 0; kind < 6; ++kind) {
        bank.clips[1] = original;
        if (kind == 0) bank.clips[1].id = 0;
        if (kind == 1) bank.clips[1].id = bank.clips[0].id;
        if (kind == 2) bank.clips[1].samples = nullptr;
        if (kind == 3) bank.clips[1].num_samples = 0;
        if (kind == 4) bank.clips[1].num_samples = 144001;
        if (kind == 5) bank.clips[1].reserved = 1;
        require(mmv2_vc_filler_exchange(handle, &bank.update, nullptr) == -1, "filler clip validation");
    }
    bank.clips[1] = original;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == 0 &&
            snapshot.prepared_count == 5 && !snapshot.active && !snapshot.started_serial &&
            !snapshot.utterance && snapshot.generation == 1, "filler prepare-only ABI");
    require(mmv2_vc_filler_exchange(handle, &bank.update, nullptr) == -2, "filler repeated generation");
    bank.update.generation = 2;
    bank.update.enabled = 0;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == 0 &&
            snapshot.prepared_count == 5 && !snapshot.active, "filler OFF retains bank");
    bank.update.generation = 1;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == -2, "filler old generation");
    require(mmv2_vc_stop(handle) == 0 && mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 &&
            snapshot.generation == 2 && snapshot.prepared_count == 5 && !snapshot.active,
            "filler stopped snapshot");
    bank.update.generation = 3;
    bank.update.clip_count = 0;
    bank.update.clips = nullptr;
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == 0 && !snapshot.prepared_count,
            "filler empty bank replacement");
    mmv2_vc_destroy(handle); // No start and no audio backend on either OS.
    std::cout << "PASS filler_ABI_validation_prestart_stop" << std::endl;

    auto silent = [](const std::vector<int16_t>& pcm) {
        return std::all_of(pcm.begin(), pcm.end(), [](int16_t v) { return !v; });
    };
    auto playback = [](Fixture& f, size_t frames = 128) {
        std::vector<int16_t> pcm(frames, -1234);
        VcAudioState::on_playback(&f.state.playback_dev, pcm.data(), nullptr, static_cast<ma_uint32>(frames));
        return pcm;
    };
    auto monitor = [](Fixture& f, size_t frames = 128) {
        std::vector<int16_t> pcm(frames, -1234);
        VcAudioState::on_monitor_playback(&f.state.monitor_dev, pcm.data(), nullptr, static_cast<ma_uint32>(frames));
        return pcm;
    };
    {
        Fixture f(0, false, false);
        FillerBank clips;
        clips.update.previous_clip_id = 1;
        clips.publish(f, 1);
        f.state.monitor_ok = true;
        f.state.monitor_gain = 1.0f;
        f.state.monitor_ring.init(48000);
        f.state.monitor_dev.pUserData = &f.state;
        require(silent(playback(f)) && !f.filler.started_serial, "prepared filler played without onset");
        f.feed(1, true);
        require(f.filler.active() && !f.filler.started_serial && silent(monitor(f)),
                "filler ACK/monitor ran before main PCM");
        const auto main = playback(f);
        require(!silent(main) && monitor(f) == main && f.filler.started_serial == 1 &&
                f.filler.started_generation == 1 && f.filler.started_utterance == 1 &&
                f.filler.started_clip_id != 1 &&
                main.front() == 1000 + f.filler.started_clip_id * 100, "filler main/monitor selection or seed");
        const uint64_t first_id = f.filler.started_clip_id;
        f.feed(2, true);
        playback(f);
        require(f.filler.started_serial == 1 && f.filler.utterance == 1, "filler repeated within VAD activity");
        // New accepted edge invalidates the old monitor tail, not body monitor.
        const std::vector<int16_t> body_monitor(128, 777);
        f.state.monitor_ring.write(body_monitor.data(), body_monitor.size());
        f.feed(1, false); f.feed(1, true);
        require(f.state.filler_monitor_ring.available() == 0 && monitor(f) == body_monitor,
                "new utterance flushed body monitor or kept old filler");
        playback(f);
        require(f.filler.started_serial == 2 && f.filler.started_clip_id != first_id &&
                f.filler.started_utterance == 2, "filler last-started repeat/new utterance");
        const auto second_id = f.filler.started_clip_id;
        clips.update.previous_clip_id = second_id == 1 ? 2 : 1;
        clips.publish(f, 2);
        require(f.filler.previous_id == second_id && !f.filler.active() && silent(playback(f)) &&
                silent(monitor(f)), "config rearmed current utterance/overwrote real last-started");
        f.feed(1, false); f.feed(1, true);
        playback(f);
        const auto serial = f.filler.started_serial;
        require(serial == 3, "new generation did not arm next onset");
        // Nonempty body wins even if every body sample is zero.
        const std::vector<int16_t> zero_body(128, 0);
        f.rvc.post_write(zero_body.data(), zero_body.size());
        require(silent(playback(f)) && !f.filler.active() && silent(monitor(f)) &&
                f.filler.started_serial == serial, "zero-valued body did not interrupt filler");
        f.feed(1, false); f.feed(1, true); playback(f);
        f.rvc.post_write(zero_body.data(), zero_body.size());
        require(silent(monitor(f)) && !f.filler.active() && f.rvc.post_available() == zero_body.size(),
                "monitor bodyready did not cancel filler or consumed body");
        playback(f);
        f.feed(1, false); f.feed(1, true); playback(f);
        clips.publish(f, 3, false);
        require(!f.filler.active() && silent(playback(f)) && silent(monitor(f)), "OFF left filler tail");
        f.feed(1, false); f.feed(1, true);
        require(!f.filler.active(), "OFF bank armed onset");
        clips.publish(f, 4);
        f.feed(1, true);
        require(silent(playback(f)), "late ON publication rearmed consumed onset");
        f.feed(1, false); f.feed(1, true); playback(f);
        const auto stopped_serial = f.filler.started_serial, stopped_id = f.filler.started_clip_id;
        {
            std::lock_guard<std::mutex> lock(f.state.route_mutex);
            f.state.output_stopped = true;
            f.state.cancel_filler();
            f.filler.invalidate();
        }
        require(silent(playback(f)) && silent(monitor(f)) &&
                f.filler.started_serial == stopped_serial && f.filler.started_clip_id == stopped_id,
                "stop did not cancel filler/preserve actual-start ACK");
    }
    {
        Fixture f(0, false, false);
        FillerBank clips;
        f.feed(1, true); // Unprepared onset is consumed.
        clips.publish(f, 1);
        for (auto& pcm : clips.pcm) std::fill(pcm.begin(), pcm.end(), -999);
        f.feed(1, true);
        require(silent(playback(f)) && !f.filler.started_serial, "late bank armed current utterance");
        f.feed(1, false); f.feed(1, true);
        const auto main = playback(f);
        require(!silent(main) && main.front() >= 1100 && main.front() <= 1500,
                "filler did not retain owned PCM copy");
        f.feed(1, false);
        f.state.rt_rvc = nullptr;
        f.feed(1, true);
        require(!f.filler.active(), "non-RVC onset armed filler");
        f.state.rt_rvc = &f.rvc;
        f.state.ring.clear();
        require(silent(playback(f)), "route recovery replayed non-RVC onset");
        f.feed(1, false);
        f.state.vad = nullptr;
        require(silent(playback(f)), "no VAD replayed filler");
        f.state.vad = reinterpret_cast<silero_vad_handle*>(&f);
    }
    {
        Fixture f(0, false, false);
        FillerBank clips;
        clips.update.clip_count = 1;
        clips.publish(f, 1, true, 1);
        f.feed(1, true);
        f.feed(1, false); f.feed(1, true); // First armed choice never actually started.
        require(!f.filler.started_serial && !silent(playback(f)) && f.filler.started_serial == 1 &&
                f.filler.started_utterance == 2, "unstarted choice incorrectly became last-started");
        playback(f, 5000);
        require(!f.filler.active() && silent(playback(f)), "finished filler replayed in the same utterance");
        f.feed(1, false); f.feed(1, true);
        require(silent(playback(f)) && f.filler.started_serial == 1,
                "single-clip bank repeated last actual start");
    }
    {
        Fixture f;
        FillerBank clips;
        clips.publish(f, 1);
        f.feed(20, true); playback(f);
        f.feed(1, false); f.finish();
        require(!f.filler.active() && f.rvc.post_available() > 0,
                "accepted conversion release did not cancel filler");
        const auto serial = f.filler.started_serial;
        playback(f);
        require(f.filler.started_serial == serial, "body callback started another filler");
    }
    std::cout << "PASS filler_onset_body_priority_monitor_OFF_generation_owned_PCM" << std::endl;
}

#ifndef _WIN32
void filler_lifecycle_cases() {
    Control ctl;
    control = &ctl;
    fake_devices = true;
    fake_model_load_ok.store(true);
    void* handle = nullptr;
    require(mmv2_vc_create("{\"modelPath\":\"fixture\",\"laughAsrAssistEnabled\":false}", &handle) == 0,
            "filler lifecycle create");
    FillerBank bank;
    mmv2_vc_filler_snapshot snapshot{sizeof(snapshot), 1};
    require(mmv2_vc_filler_exchange(handle, &bank.update, &snapshot) == 0 &&
            mmv2_vc_load_models(handle) == 0 && mmv2_vc_start(handle) == 0,
            "filler prestart bank lifecycle");
    auto* state = static_cast<VcAudioState*>(fake_capture->pUserData);
    state->vad = reinterpret_cast<silero_vad_handle*>(state);
    state->vad_hold_frames = 0;
    state->agc_enabled = false;
    state->dsp.enabled = false;
    state->output_auto_gain_enabled = false;
    std::vector<int16_t> input(1536);
    for (size_t i = 0; i < input.size(); ++i)
        input[i] = static_cast<int16_t>(3000 * std::sin(6.283185307 * 180 * i / 48000));
    std::vector<int16_t> output(128, -1234);
    fake_capture_callback(fake_capture, nullptr, input.data(), static_cast<ma_uint32>(input.size()));
    require(mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 &&
            snapshot.active && !snapshot.started_serial, "prestart bank not visible at accepted onset");
    fake_playback_callback(fake_playback, output.data(), nullptr, static_cast<ma_uint32>(output.size()));
    require(mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 &&
            snapshot.started_serial == 1 && snapshot.started_generation == 1 && snapshot.started_clip_id,
            "filler actual-start lifecycle ACK");
    const uint64_t id = snapshot.started_clip_id, utterance = snapshot.started_utterance;
    state->vad = nullptr; // fixture scorer, not an allocated model
    require(mmv2_vc_stop(handle) == 0 && mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 &&
            !snapshot.active && snapshot.prepared_count == 5 && snapshot.started_serial == 1 &&
            snapshot.started_clip_id == id && snapshot.started_utterance == utterance,
            "filler stop lost actual-start ACK");
    require(mmv2_vc_filler_exchange(handle, &bank.update, nullptr) == -2,
            "stop reopened stale bank generation");
    require(mmv2_vc_start(handle) == 0, "filler restart");
    state = static_cast<VcAudioState*>(fake_capture->pUserData);
    state->vad = reinterpret_cast<silero_vad_handle*>(state);
    state->vad_hold_frames = 0;
    state->agc_enabled = false;
    state->dsp.enabled = false;
    fake_capture_callback(fake_capture, nullptr, input.data(), static_cast<ma_uint32>(input.size()));
    require(mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 && !snapshot.active &&
            snapshot.started_serial == 1, "restart reused stopped generation");
    bank.update.generation = 2;
    require(mmv2_vc_filler_exchange(handle, &bank.update, nullptr) == 0,
            "filler restart newer generation");
    fake_capture_callback(fake_capture, nullptr, input.data(), static_cast<ma_uint32>(input.size()));
    require(mmv2_vc_filler_exchange(handle, nullptr, &snapshot) == 0 && !snapshot.active,
            "restart publish rearmed an already-consumed utterance");
    state->vad = nullptr;
    require(mmv2_vc_stop(handle) == 0, "filler restart stop");
    mmv2_vc_destroy(handle);
    fake_devices = false;
    control = nullptr;
    std::cout << "PASS filler_public_lifecycle_prestart_ACK_stop_restart" << std::endl;
}

void context_init_failure_cases() {
    for (const char* options : {"{\"laughAsrAssistEnabled\":false}", "{}",
                                "{\"mode\":\"rvc_raw\",\"laughAsrAssistEnabled\":false}"}) {
        void* handle = nullptr;
        require(mmv2_vc_create(options, &handle) == 0 && handle, "context failure create");
        for (int attempt = 0; attempt < 2; ++attempt) {
            require(mmv2_vc_start(handle) == -10, "context failure did not return its error");
            const char* status = nullptr;
            const int result = mmv2_vc_poll_status(handle, &status);
            const std::string status_text = status ? status : "";
            std::free(const_cast<char*>(status));
            require(result == 0 && status_text.find("\"running\":false") != std::string::npos &&
                    status_text.find("\"audioPassthroughActive\":false") != std::string::npos,
                    "context failure left session running or audio active");
        }
        require(mmv2_vc_stop(handle) == 0, "context failure stop");
        mmv2_vc_destroy(handle);
        std::cout << "PASS context_init_failure " << options << std::endl;
    }
}

void lifecycle_boundary_cases() {
    for (const auto& item : {
             std::pair<const char*, bool>{"{}", false},
             {"{\"modelId\":\"\"}", false},
             {"{\"engine\":\"passthrough\"}", false},
             {"{\"modelId\":\"selected\"}", true},
             {"{\"id\":\"selected\"}", true},
             {"{\"rvcModelDir\":\"fixture\"}", true},
             {"{\"modelPath\":\"fixture\"}", true},
             {"{\"engine\":\"onnx\"}", true},
             {"{\"engine\":\"seedvc_ncnn\"}", true},
             {"{\"mode\":\"rvc_raw\",\"laughAsrAssistEnabled\":false}", true}}) {
        require(vc_conversion_requested(parse_options(item.first)) == item.second, "conversion selection predicate");
    }
    Control ctl;
    control = &ctl;
    fake_devices = true;
    fake_model_load_ok.store(false);
    void* handle = nullptr;
    const char* options = "{\"modelId\":\"selected\",\"modelPath\":\"fixture\",\"engine\":\"rvc_onnx\"}";
    require(mmv2_vc_create(options, &handle) == 0 && handle, "lifecycle create");
    require(mmv2_vc_start(handle) == 0, "lifecycle start");
    auto silent = [](const std::vector<int16_t>& pcm) {
        return std::all_of(pcm.begin(), pcm.end(), [](int16_t v) { return v == 0; });
    };
    auto status = [&] {
        const char* json = nullptr;
        require(mmv2_vc_poll_status(handle, &json) == 0 && json, "lifecycle status");
        std::string result(json);
        std::free(const_cast<char*>(json));
        return result;
    };
    require(!first_playback.empty() && silent(first_playback), "first playback leaked before model load");
    require(status().find("\"audioPassthroughActive\":false") != std::string::npos &&
            status().find("model not ready; output blocked") != std::string::npos, "blocked status misreported passthrough");
    auto* state = static_cast<VcAudioState*>(fake_capture->pUserData);
    auto playback = [] {
        std::vector<int16_t> pcm(48000, -1234);
        fake_playback_callback(fake_playback, pcm.data(), nullptr, static_cast<ma_uint32>(pcm.size()));
        return pcm;
    };
    auto feed = [&](int frames, bool speech) {
        ctl.vad_probability.store(speech ? 0.99 : 0.0);
        for (int frame = 0; frame < frames; ++frame) {
            std::vector<int16_t> pcm(1536, 0);
            if (speech) for (size_t i = 0; i < pcm.size(); ++i)
                pcm[i] = static_cast<int16_t>(3000 * std::sin(6.283185307 * 180 * i / 48000));
            ctl.samples.fetch_add(pcm.size());
            fake_capture_callback(fake_capture, nullptr, pcm.data(), static_cast<ma_uint32>(pcm.size()));
        }
    };
    require(mmv2_vc_load_models(handle) == -77, "model load failure was not returned");
    feed(2, true);
    require(silent(playback()), "load failure fell back to unconverted audio");
    {
        const std::vector<int16_t> input(1536, 3000);
        std::vector<int16_t> output(input.size(), -1234);
        const char* json = reinterpret_cast<const char*>(1);
        require(mmv2_vc_inject_and_capture(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -3 &&
                json == nullptr && silent(output), "not-ready file-live inject returned raw PCM");
    }
    fake_model_load_ok.store(true);
    require(mmv2_vc_load_models(handle) == 0, "mock model load");
    require(status().find("\"conversionRuntimeAvailable\":true") != std::string::npos,
            "loaded model status");
    state->vad = reinterpret_cast<silero_vad_handle*>(state); // the fixture VAD scorer only
    state->vad_hold_frames = 0;
    state->agc_enabled = false;
    state->dsp.enabled = false;
    state->output_auto_gain_enabled = false;
    state->laugh_asr_moonshine_dll_dir = "fixture-only-no-dll";
    state->laugh_asr_moonshine_model_root = "fixture-only-no-model";
    feed(20, true); feed(1, false);
    wait_for([&] { return ctl.asr_calls.load() == 1 && ctl.vc_calls.load() == 1; });
    require(silent(playback()), "unresolved segment released before ASR");
    // Queue a second old segment and leave another partially collected.
    feed(20, true); feed(1, false); feed(3, true);
    state->monitor_dev.pUserData = state;
    const std::vector<int16_t> old_monitor(100, 799);
    state->monitor_ring.write(old_monitor.data(), old_monitor.size());
    auto monitor = [&] {
        std::vector<int16_t> pcm(200, -1234);
        VcAudioState::on_monitor_playback(&state->monitor_dev, pcm.data(), nullptr, static_cast<ma_uint32>(pcm.size()));
        return pcm;
    };
    auto unload = std::async(std::launch::async, [&] { mmv2_vc_unload_models(handle); });
    wait_for([&] { return ctl.route_unloads == 1; });
    auto reload = std::async(std::launch::async, [&] { return mmv2_vc_load_models(handle); });
    require(reload.wait_for(std::chrono::milliseconds(50)) == std::future_status::timeout,
            "reload bypassed in-flight unload");
    feed(2, true);
    require(silent(playback()) && silent(monitor()), "unload leaked pending or unconverted audio");
    permit(1);
    require(unload.wait_for(std::chrono::seconds(8)) == std::future_status::ready, "unload did not join pending worker");
    unload.get();
    require(reload.wait_for(std::chrono::seconds(8)) == std::future_status::ready && reload.get() == 0,
            "reload after pending unload");
    require(ctl.vc_calls.load() == 1 && !state->collecting_speech && state->speech_buffer.empty(),
            "old queued or partial segment survived unload");
    require(silent(playback()) && silent(monitor()), "old completed result survived reload");
    feed(20, true); feed(1, false);
    permit(2);
    wait_for([&] { return ctl.done >= 2; });
    require(ctl.vc_calls.load() == 2 && !silent(playback()), "new accepted converted segment did not recover");
    feed(20, true); feed(1, false);
    wait_for([&] { return ctl.vc_calls.load() == 3; });
    state->vad = nullptr; // fake VAD is not an allocated model
    auto stop = std::async(std::launch::async, [&] { return mmv2_vc_stop(handle); });
    wait_for([&] { return ctl.route_stops == 1; });
    auto load_during_stop = std::async(std::launch::async, [&] { return mmv2_vc_load_models(handle); });
    require(load_during_stop.wait_for(std::chrono::milliseconds(50)) == std::future_status::timeout,
            "load bypassed in-flight stop");
    require(silent(playback()), "stop leaked pending conversion");
    permit(3);
    require(stop.wait_for(std::chrono::seconds(8)) == std::future_status::ready && stop.get() == 0,
            "stop did not join pending conversion");
    require(load_during_stop.wait_for(std::chrono::seconds(8)) == std::future_status::ready && load_during_stop.get() == 0,
            "load after pending stop");
    require(mmv2_vc_start(handle) == 0, "restart with already-loaded converter");
    require(silent(first_playback), "restart replayed an old result or sent unconverted input");
    require(mmv2_vc_stop(handle) == 0, "restart stop");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_load_unload_stop_reload" << std::endl;

    // No model is the UI's intentional DSP passthrough, not a failed RVC.
    handle = nullptr;
    require(mmv2_vc_create("{\"modelId\":\"\",\"laughAsrAssistEnabled\":false,\"prosodyNeutralizerEnabled\":false,\"pressureEstimatorEnabled\":false,\"noiseCleanup\":\"none\"}", &handle) == 0,
            "passthrough create");
    require(mmv2_vc_start(handle) == 0 && !silent(first_playback), "explicit empty-selection passthrough lost");
    require(mmv2_vc_stop(handle) == 0, "passthrough stop");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_passthrough" << std::endl;

    handle = nullptr;
    require(mmv2_vc_create("{\"modelPath\":\"fixture\",\"laughAsrAssistEnabled\":false}", &handle) == 0,
            "assist-off create");
    require(mmv2_vc_start(handle) == 0 && silent(first_playback), "assist-off sent input before conversion ready");
    require(mmv2_vc_load_models(handle) == 0, "assist-off load");
    state = static_cast<VcAudioState*>(fake_capture->pUserData);
    state->vad = reinterpret_cast<silero_vad_handle*>(state);
    state->vad_hold_frames = 0;
    state->agc_enabled = false;
    state->dsp.enabled = false;
    const int assist_vc = ctl.vc_calls.load();
    const int assist_asr = ctl.asr_calls.load();
    feed(20, true); feed(1, false);
    require(ctl.vc_calls.load() == assist_vc && ctl.asr_calls.load() == assist_asr && silent(playback()),
            "assist-off changed its segment/ASR behavior or emitted raw audio");
    state->vad = nullptr;
    require(mmv2_vc_stop(handle) == 0, "assist-off stop");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_assist_off" << std::endl;

    handle = nullptr;
    require(mmv2_vc_create("{\"engine\":\"seedvc_ncnn\",\"modelPath\":\"fixture\"}", &handle) == 0,
            "unsupported live engine create");
    require(mmv2_vc_start(handle) == 0 && silent(first_playback), "unsupported live engine leaked input");
    require(mmv2_vc_load_models(handle) == -21 && silent(playback()), "unsupported live engine did not fail closed");
    require(mmv2_vc_stop(handle) == 0, "unsupported live engine stop");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_unsupported_live_engine" << std::endl;

    // Raw mode disables ASR, not conversion. Its first callback is still closed.
    handle = nullptr;
    const int prior_vc = ctl.vc_calls.load();
    const int prior_asr = ctl.asr_calls.load();
    require(mmv2_vc_create("{\"mode\":\"rvc_raw\",\"modelPath\":\"fixture\",\"laughAsrAssistEnabled\":false}", &handle) == 0,
            "raw create");
    require(mmv2_vc_start(handle) == 0 && silent(first_playback), "raw mode bypassed conversion before load");
    require(mmv2_vc_load_models(handle) == 0, "raw load");
    state = static_cast<VcAudioState*>(fake_capture->pUserData);
    state->vad = reinterpret_cast<silero_vad_handle*>(state);
    state->vad_hold_frames = 0;
    state->agc_enabled = false;
    state->dsp.enabled = false;
    feed(20, true); feed(1, false);
    permit(prior_vc + 1);
    wait_for([&] { return ctl.vc_calls.load() == prior_vc + 1 && ctl.done >= 4; });
    const auto raw_pcm = playback();
    require(ctl.asr_calls.load() == prior_asr && std::any_of(raw_pcm.begin(), raw_pcm.end(), [prior_vc](int16_t v) {
        return v == 701 + prior_vc;
    }), "raw mode failed to bypass ASR while retaining converted output");
    mmv2_vc_unload_models(handle);
    require(status().find("\"audioPassthroughActive\":false") != std::string::npos && silent(playback()),
            "raw unload status or output fell back to passthrough");
    state->vad = nullptr;
    require(mmv2_vc_stop(handle) == 0, "raw stop");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_raw_conversion" << std::endl;

    // The Windows file callers use different final-segment logic, but must not
    // return or save unconverted PCM when the selected model is unavailable or
    // inference fails. Only synthetic buffers and the mock converter are used.
    Control buffers;
    control = &buffers;
    handle = nullptr;
    require(mmv2_vc_create("{\"modelPath\":\"fixture\"}", &handle) == 0, "buffer create");
    const std::vector<int16_t> input(1536, 3000);
    std::vector<int16_t> output(input.size(), -1234);
    const char* json = reinterpret_cast<const char*>(1);
    require(mmv2_vc_process_buffer(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -3 &&
            json == nullptr && silent(output), "unloaded offline processing returned raw PCM");
    require(mmv2_vc_load_models(handle) == 0, "offline mock load");
    buffers.conversion_ok.store(false);
    permit(1);
    std::fill(output.begin(), output.end(), -1234);
    require(mmv2_vc_process_buffer(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -4 &&
            json == nullptr && silent(output), "failed offline inference returned raw PCM");
    buffers.conversion_ok.store(true);
    permit(2);
    require(mmv2_vc_process_buffer(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) ==
                static_cast<int>(input.size()) && json && std::all_of(output.begin(), output.end(), [](int16_t v) { return v == 702; }),
            "offline converted positive control");
    std::free(const_cast<char*>(json));
    require(mmv2_vc_start(handle) == 0, "file-live start");
    buffers.conversion_ok.store(false);
    permit(3);
    std::fill(output.begin(), output.end(), -1234);
    json = reinterpret_cast<const char*>(1);
    require(mmv2_vc_inject_and_capture(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -4 &&
            json == nullptr && silent(output), "failed file-live inference returned raw PCM");
    require(mmv2_vc_stop(handle) == 0, "buffer stop");
    std::fill(output.begin(), output.end(), -1234);
    require(mmv2_vc_inject_and_capture(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -2 &&
            json == nullptr && silent(output), "stopped file-live inject retained old output");
    mmv2_vc_destroy(handle);
    std::cout << "PASS lifecycle_file_caller_fail_closed" << std::endl;

    // A selected model must not become passthrough just because its JSON uses
    // legal whitespace or a caller supplied a non-string selection value.
    fake_model_load_ok.store(false);
    for (const char* selection : {"\"modelPath\":\n\"fixture\"", "\"mode\":true,\"modelPath\":\r\n\"fixture\"",
                                  "\"modelId\":123", "\"id\":true", "\"modelPath\":[]",
                                  "\"rvcModelDir\":{}", "\"engine\":false", "\"mode\":123"}) {
        const std::string selected_options = std::string("{") + selection +
            ",\"laughAsrAssistEnabled\":false,\"prosodyNeutralizerEnabled\":false,"
            "\"pressureEstimatorEnabled\":false,\"noiseCleanup\":\"none\"}";
        handle = nullptr;
        require(mmv2_vc_create(selected_options.c_str(), &handle) == 0 && handle, "selection boundary create");
        require(mmv2_vc_start(handle) == 0 && silent(first_playback),
                "unreadable selection leaked unconverted PCM at first callback");
        std::fill(output.begin(), output.end(), -1234);
        json = reinterpret_cast<const char*>(1);
        require(mmv2_vc_process_buffer(handle, input.data(), static_cast<int>(input.size()), output.data(), &json) == -3 &&
                json == nullptr && silent(output), "unreadable selection bypassed offline model readiness");
        require(mmv2_vc_load_models(handle) < 0 && silent(playback()), "unreadable selection did not fail closed");
        require(mmv2_vc_stop(handle) == 0, "selection boundary stop");
        mmv2_vc_destroy(handle);
    }
    std::cout << "PASS lifecycle_unreadable_selection_fail_closed" << std::endl;
    for (const char* token : {"mode", "engine", "modelId", "id", "modelPath", "rvcModelDir"}) {
        const std::string passthrough_options = std::string("{\"laughAsrTokens\":\"") + token +
            "\",\"modelId\":\"\",\"laughAsrAssistEnabled\":false,\"prosodyNeutralizerEnabled\":false,"
            "\"pressureEstimatorEnabled\":false,\"noiseCleanup\":\"none\"}";
        handle = nullptr;
        require(mmv2_vc_create(passthrough_options.c_str(), &handle) == 0 && handle, "token passthrough create");
        require(mmv2_vc_start(handle) == 0 && !silent(first_playback),
                "client token value was mistaken for a conversion selection");
        require(mmv2_vc_stop(handle) == 0, "token passthrough stop");
        mmv2_vc_destroy(handle);
    }
    require(parse_options("{\"laughAsrTokens\":\"modelPath\",\"modelPath\":\r\n\"fixture\"}").model_path == "fixture",
            "token value hid a later model path key");
    require(!vc_conversion_requested(parse_options("{\"modelId\":null,\"engine\":null,\"mode\":null}")),
            "null optional selections lost empty-selection compatibility");
    std::cout << "PASS lifecycle_passthrough_retained_tokens" << std::endl;
    fake_devices = false;
    control = nullptr;
}
#endif

void require_model_load(Fixture& f, const std::string& path, int arch, bool success = true) {
    std::lock_guard<std::mutex> lock(f.ctl.mutex);
    const auto& o = f.ctl.load_options;
    require(f.ctl.load_calls.load() == 1 &&
            std::filesystem::path(f.ctl.load_path).lexically_normal() == std::filesystem::path(path).lexically_normal() &&
            f.ctl.load_arch == arch &&
            f.ctl.load_success == success, "model load path/architecture/result");
    require(o.vad_window_duration == 0.15 && o.vad_look_behind_sample_count == 20000 &&
            o.vad_threshold == 0.30 && o.max_tokens_per_second == 13.0 && o.force_word_timestamps,
            "model load options changed");
}

void tiny_selection_cases() {
    namespace fs = std::filesystem;
    const std::string dir = fs::current_path().string();
    const std::string second_dir = fs::current_path().parent_path().string();
    const fs::path root("fixture-only-no-model");
    {
        ScopedTinyModelEnv env(nullptr);
        Fixture f; f.final_segment(); f.finish();
        require_model_load(f, (root / "ja/quantized/base-ja").string(), mobmate::MoonshineBridge::ARCH_BASE);
        f.report("selection_default_ja_base", 1, "text");
    }
    {
        ScopedTinyModelEnv env(dir.c_str());
        Fixture f; f.final_segment(); f.finish();
        require_model_load(f, dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
        f.report("selection_tiny_text", 1, "text");
    }
    {
        ScopedTinyModelEnv env(dir.c_str());
        Fixture f(1); f.final_segment(); f.finish();
        require_model_load(f, dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
        f.report("selection_tiny_empty", 0, "normal_empty");
    }
    {
        ScopedTinyModelEnv env(dir.c_str());
        Fixture f(5); f.final_segment(); f.finish();
        require_model_load(f, dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING, false);
        f.report("selection_tiny_load_failure", 0, "model_load_failed");
        require(f.state.laugh_asr_last_decision == "moonshine_model_load_failed", "load failure decision");
    }
    {
        const std::string missing = (fs::current_path() / "fixture-nonexistent-tiny-model").string();
        require(!fs::exists(missing), "fixture missing model unexpectedly exists");
        ScopedTinyModelEnv env(missing.c_str());
        Fixture f; f.final_segment(); f.finish();
        require(f.ctl.load_calls.load() == 0 && f.ctl.asr_calls.load() == 0, "invalid path reached model or ASR");
        f.report("selection_tiny_invalid_path", 0, "model_load_failed");
        require(f.state.laugh_asr_last_decision == "moonshine_model_load_failed", "invalid path decision");
    }
#ifndef _WIN32
    {
        ScopedTinyModelEnv env("");
        Fixture f; f.final_segment(); f.finish();
        require(f.ctl.load_calls.load() == 0 && f.ctl.asr_calls.load() == 0, "empty opt-in fell back");
        f.report("selection_tiny_empty_path", 0, "model_load_failed");
    }
#endif
    struct OtherLanguage { const char* language; const char* path; int arch; };
    for (const auto& item : {OtherLanguage{"zh", "zh/quantized/base-zh", mobmate::MoonshineBridge::ARCH_BASE},
                             OtherLanguage{"ko", "ko/quantized/tiny-ko", mobmate::MoonshineBridge::ARCH_TINY},
                             OtherLanguage{"en", "en/quantized", mobmate::MoonshineBridge::ARCH_BASE_STREAMING},
                             OtherLanguage{"fr", "ja/quantized/base-ja", mobmate::MoonshineBridge::ARCH_BASE}}) {
        ScopedTinyModelEnv env(dir.c_str());
        Fixture f(0, false, true, item.language); f.final_segment(); f.finish();
        require_model_load(f, (root / item.path).string(), item.arch);
        const std::string label = std::string("selection_other_") + item.language;
        f.report(label.c_str(), 1, "text");
    }
    {
        ScopedTinyModelEnv env(dir.c_str());
        {
            Fixture f; f.final_segment(); f.finish();
            require_model_load(f, dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
            env.set(second_dir.c_str()); // loaded worker keeps its model, not the new environment
            f.final_segment(); f.finish(2);
            require_model_load(f, dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
            f.report("selection_loaded_worker_retains_model", 2, "text");
        }
        {
            Fixture f; f.final_segment(); f.finish();
            require_model_load(f, second_dir, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
            f.report("selection_new_worker_reads_current_model", 1, "text");
        }
    }
    {
        ScopedTinyModelEnv env(dir.c_str());
        Fixture f(0, true); f.final_segment(); f.finish();
        require(f.ctl.init_calls.load() == 0 && f.ctl.load_calls.load() == 0 && f.ctl.asr_calls.load() == 0,
                "raw mode attempted private ASR");
        f.report("selection_tiny_raw_bypass", 1, "bypassed_raw");
    }
}

void real_tiny_zero_case(const std::string& runtime, const std::string& model) {
    ScopedTinyModelEnv env(model.c_str());
    Fixture f(0, false, true, "ja", runtime, true);
    // Zero PCM cannot cross the production energy-start gate. Pre-arm only the
    // mocked VAD's active state so the real callback segments zero samples;
    // the ASR and the subsequent gate, worker and sink remain real code.
    f.state.vad_speech_active.store(true);
    f.feed(20, true, 0, true);
    require(f.state.collecting_speech && f.state.speech_buffer.size() >= 12000, "zero capture did not form segment");
    f.feed(1, false, 0, true);
    f.finish();
    require_model_load(f, model, mobmate::MoonshineBridge::ARCH_TINY_STREAMING);
    require(f.ctl.init_calls.load() == 1 && f.ctl.asr_calls.load() == 1 && f.ctl.vc_calls.load() == 1 &&
            f.ctl.asr_input_zero.load() && f.ctl.conversion_input_zero.load(), "real ASR/zero PCM did not traverse worker");
    require(f.state.laugh_asr_last_decision == "reject_no_asr_text" &&
            f.state.asr_gate_accept_count.load() == 0 && f.state.asr_gate_reject_count.load() == 1,
            "real ASR gate disposition");
    {
        std::lock_guard<std::mutex> lock(f.ctl.mutex);
        require(std::any_of(f.ctl.events.begin(), f.ctl.events.end(), [](const std::string& e) {
            return e.find("asr_ready:normal_empty:reject_no_asr_text:") == 0;
        }), "real ASR ready outcome/decision missing");
    }
    f.report("real_tiny_zero_mobfy", 0, "normal_empty");
    require(f.nonzero == 0 && f.playback_callbacks > 0, "real ASR zero segment reached final sink");
}
#endif
}

int main(int argc, char** argv) {
    try {
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
        if (argc == 2 && std::string(argv[1]) == "--filler-boundary") {
            ScopedTinyModelEnv clear_inherited_env(nullptr);
            filler_boundary_cases();
#ifndef _WIN32
            filler_lifecycle_cases();
#endif
            return 0;
        }
#endif
#if !defined(_WIN32) && !defined(MOBMATE_VC_BOUNDARY_BASELINE)
        if (argc == 2 && std::string(argv[1]) == "--context-init-failure") {
            context_init_failure_cases();
            return 0;
        }
        if (argc == 2 && std::string(argv[1]) == "--lifecycle-boundary") {
            lifecycle_boundary_cases();
            return 0;
        }
#endif
        require(argc == 1 || argc == 3, "expected no arguments or runtime and model directories");
        ScopedTinyModelEnv clear_inherited_env(nullptr);
        check_fixture_notification_serialization();
        ordinary_cases();
        speculative_cases();
        queue_boundary_cases();
#ifndef MOBMATE_VC_BOUNDARY_BASELINE
        tiny_selection_cases();
        filler_boundary_cases();
#ifndef _WIN32
        filler_lifecycle_cases();
#endif
        if (argc == 3) real_tiny_zero_case(argv[1], argv[2]);
#else
        require(argc == 1, "baseline does not run real ASR");
#endif
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "VC boundary regression: " << error.what() << '\n';
        return 1;
    }
}
