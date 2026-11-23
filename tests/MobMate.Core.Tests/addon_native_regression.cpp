// Test the actual shared native finalizer and manifest consumer without a model or capture device.
// Seed the capture buffer without starting WASAPI; production capture layout is unchanged.
#include "mobmate_native_core/audio_capture.h"
#include "mobmate_native_core/silero_vad.h"
#include <future>

static double onset_fixture_vad_score(silero_vad_handle*, const int16_t*, int, int);
static void onset_fixture_vad_reset(silero_vad_handle*);
#define silero_vad_score onset_fixture_vad_score
#define silero_vad_reset onset_fixture_vad_reset
#include "../../native/mobmate_native_core/src/native_api.cpp"
#undef silero_vad_score
#undef silero_vad_reset
#include <iostream>
#include <stdexcept>

static bool fail_callback = false;
static int joint_callback_status = 0;
static std::string joint_callback_text = "代替文";
static int callback_count = 0;
static bool fail_output = false;
static std::vector<int16_t> callback_pcm;
static std::string callback_engine, callback_language, callback_mode;
static int mock_transcriber_v2(const char* engine, const char* language, const char* mode,
    const int16_t* pcm, int count, int rate, char* text, int capacity, int64_t* ms);
static int mock_transcriber(const int16_t* pcm, int count, int rate, char* text, int capacity, int64_t* ms) {
    ++callback_count;
    if (rate != 16000 || capacity < 16) return -1;
    callback_pcm.assign(pcm, pcm + count);
    if (fail_callback) return -1;
    std::strcpy(text, "試験用");
    *ms = 17;
    return 0;
}
static int mock_joint_transcriber(const int16_t* pcm, int count, int rate, char* text, int capacity, int64_t* ms) {
    ++callback_count;
    if (rate != 16000 || capacity <= static_cast<int>(joint_callback_text.size())) return -1;
    callback_pcm.assign(pcm, pcm + count);
    if (joint_callback_status) return joint_callback_status;
    std::strcpy(text, joint_callback_text.c_str());
    *ms = 17;
    return 0;
}
static int mock_transcriber_v2(const char* engine, const char* language, const char* mode,
    const int16_t* pcm, int count, int rate, char* text, int capacity, int64_t* ms) {
    callback_engine = engine; callback_language = language; callback_mode = mode;
    return mock_transcriber(pcm, count, rate, text, capacity, ms);
}

static void require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }

static auto* const onset_fixture_vad = reinterpret_cast<silero_vad_handle*>(1);
static double onset_fixture_vad_score(silero_vad_handle* handle, const int16_t* pcm, int count, int rate) {
    if (handle != onset_fixture_vad) return silero_vad_score(handle, pcm, count, rate);
    int64_t energy = 0;
    for (int i = 0; i < count; ++i) energy += std::abs(static_cast<int>(pcm[i]));
    return energy > static_cast<int64_t>(count) * 200 ? 0.99 : 0.0;
}
static void onset_fixture_vad_reset(silero_vad_handle* handle) {
    if (handle != onset_fixture_vad) silero_vad_reset(handle);
}

namespace mobmate {
struct WasapiCaptureTestAccess {
    static void seed(WasapiCapture& capture, const std::vector<int16_t>& audio) {
        capture._pcm_buffer.assign(audio.begin(), audio.begin() + 16384);
        capture._pcm_buffer.insert(capture._pcm_buffer.end(), 4096, 0);
    }
};
}

struct OnsetObservation {
    std::vector<uint64_t> sequences;
    std::vector<int> transcriptions_at_notification;
};
static void observe_onset(uint64_t sequence, void* context) {
    auto& observation = *static_cast<OnsetObservation*>(context);
    observation.sequences.push_back(sequence);
    observation.transcriptions_at_notification.push_back(callback_count);
}

struct OnsetDrain {
    std::promise<void> entered;
    std::shared_future<void> release;
};
static void blocking_onset_for_drain_test(uint64_t, void* context) {
    auto& drain = *static_cast<OnsetDrain*>(context);
    drain.entered.set_value();
    drain.release.wait(); // Only the test intentionally blocks, to verify unregister's boundary.
}

static void check_onset_before_same_poll_transcription(const std::vector<int16_t>& audio) {
    RealtimeSession session;
    struct DetachFixtureVad {
        RealtimeSession& session;
        ~DetachFixtureVad() { session.vad_silero = nullptr; }
    } detach{session};
    session.realtime_engine = "haas_ja";
    session.language = "ja";
    session.scene = "talk_clean";
    session.state = RealtimeState::kRecording;
    session.vad_silence_duration_ms = 100;
    session.vad_silero = onset_fixture_vad;
    OnsetObservation observation;
    require(mmv2_realtime_set_vad_start_callback(nullptr, observe_onset, &observation) == -1, "onset rejects null handle");
    require(mmv2_realtime_set_vad_start_callback(&session, observe_onset, &observation) == 0, "onset registration");
    auto poll_segment = [&](bool notification_expected) {
        mobmate::WasapiCaptureTestAccess::seed(session.capture, audio);
        const int before = callback_count;
        const char* json = nullptr;
        require(mmv2_realtime_poll(&session, &json) == 0 && json, "onset poll");
        mmv2_free_string(json);
        require(callback_count > before, "same poll invoked transcriber");
        if (notification_expected)
            require(!observation.transcriptions_at_notification.empty() &&
                observation.transcriptions_at_notification.back() == before, "onset precedes same-poll transcription");
    };
    poll_segment(true);
    require(observation.sequences == std::vector<uint64_t>({1}), "first onset exactly once");
    poll_segment(true);
    require(observation.sequences == std::vector<uint64_t>({1, 2}), "onset sequence survives segment reset");
    require(mmv2_realtime_set_vad_start_callback(&session, nullptr, nullptr) == 0, "onset unregister");
    poll_segment(false);
    require(observation.sequences == std::vector<uint64_t>({1, 2}), "unregistered onset remains silent");
    require(mmv2_realtime_set_vad_start_callback(&session, observe_onset, &observation) == 0, "onset re-register");
    poll_segment(true);
    require(observation.sequences == std::vector<uint64_t>({1, 2, 4}), "sequence advances while unregistered");
}

static void check_rms_onset_transitions() {
    RealtimeSession session;
    session.vad_rms_threshold = 100;
    session.vad_silence_duration_ms = 50;
    update_vad_state(&session, 200, 10);
    require(session.vad_state == VadState::kSpeech && session.vad_speech_count == 1, "RMS onset without handler");
    OnsetObservation observation;
    require(mmv2_realtime_set_vad_start_callback(&session, observe_onset, &observation) == 0, "RMS onset registration");
    update_vad_state(&session, 200, 20);
    require(observation.sequences.empty(), "ongoing RMS speech does not notify again");
    update_vad_state(&session, 0, 100);
    require(session.vad_state == VadState::kEndDetected, "RMS silence ends speech");
    update_vad_state(&session, 200, 110);
    require(observation.sequences == std::vector<uint64_t>({2}), "RMS next onset sequence");
}

static void check_onset_unregister_drains() {
    RealtimeSession session;
    std::promise<void> release;
    OnsetDrain drain{std::promise<void>{}, release.get_future().share()};
    auto entered = drain.entered.get_future();
    require(mmv2_realtime_set_vad_start_callback(&session, blocking_onset_for_drain_test, &drain) == 0, "drain registration");
    std::thread callback([&]() { notify_vad_start(&session); });
    const bool callback_entered = entered.wait_for(std::chrono::seconds(2)) == std::future_status::ready;
    if (!callback_entered) {
        release.set_value();
        callback.join();
        require(false, "drain callback entered");
    }
    std::promise<void> unregister_started;
    auto started = unregister_started.get_future();
    auto unregister = std::async(std::launch::async, [&]() {
        unregister_started.set_value();
        return mmv2_realtime_set_vad_start_callback(&session, nullptr, nullptr);
    });
    started.wait();
    const bool waited = unregister.wait_for(std::chrono::milliseconds(50)) == std::future_status::timeout;
    release.set_value();
    callback.join();
    const bool returned = unregister.wait_for(std::chrono::seconds(2)) == std::future_status::ready;
    require(waited && returned && unregister.get() == 0, "unregister drains in-flight onset");
    notify_vad_start(&session); // A freed context is not touched after unregister returns.
}
static std::string read_all(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(input), {});
}

static int mock_output(const char* poll, const char* language, const char* scene,
    char* text, int text_capacity, char* engine, int engine_capacity, char* reason, int reason_capacity) {
    const std::string input(poll);
    require(input.find("\"finalPcm16Base64\":") != std::string::npos, "managed output PCM");
    require(input.find("\"expected\"") == std::string::npos && input.find("\"aliases\"") == std::string::npos,
        "output callback must not see oracle");
    require(std::string(language) == "ja" && std::string(scene) == "talk_clean", "managed row context");
    if (fail_output) return -1;
    if (text_capacity < 32 || engine_capacity < 32 || reason_capacity < 32) return -1;
    std::strcpy(text, "整形結果");
    std::strcpy(engine, "phoneme_ctc");
    std::strcpy(reason, "phonetic_assist_filler");
    return 0;
}

static void check_wav_error_rows() {
    { std::ofstream broken("broken.wav"); broken << "not a wav"; }
    { std::ofstream manifest("wav-errors.csv"); manifest << "wav,expected,language,scene\nmissing.wav,試験用,ja,talk_clean\nbroken.wav,試験用,ja,talk_clean\n"; }
    const char* summary = nullptr;
    require(mmv2_realtime_bench_manifest("wav-errors.csv", "wav-errors.jsonl", "{\"engine\":\"haas_ja\",\"language\":\"ja\"}", &summary) == 0,
        "WAV failure bench consumer");
    const std::string summary_json(summary ? summary : "");
    mmv2_free_string(summary);
    require(summary_json.find("\"errorCount\":2") != std::string::npos, "WAV failure summary count");
    std::ifstream results("wav-errors.jsonl");
    std::string row;
    int count = 0;
    while (std::getline(results, row)) {
        std::cout << row << "\n"; // Also consumed by an independent JSON parser in the focused check.
        require(row.find(",,") == std::string::npos, "WAV failure row has an extra JSON separator");
        require(row.find("\"transcript\":\"\"") != std::string::npos && row.find("\"latency_ms\":-1") != std::string::npos,
            "WAV failure row must retain the established empty result");
        require(row.find("\"error\":\"WAV metadata failed:") != std::string::npos && row.find("\"wall_ms\":") != std::string::npos,
            "WAV failure row must retain its error and wall time");
        ++count;
    }
    require(count == 2, "WAV failure rows must not be dropped");
}

static void check_standalone_create_without_base() {
    const auto v1 = g_addon_transcriber.load();
    const auto v2 = g_addon_transcriber_v2.load();
    const auto pcm = callback_pcm;
    for (const auto* engine : { "haas_ja", "parakeet_ja", "qwen3_asr_ja", "nemotron_3_5" }) {
        const bool haas = std::string(engine) == "haas_ja";
        g_addon_transcriber.store(haas ? mock_transcriber : nullptr);
        g_addon_transcriber_v2.store(haas ? nullptr : mock_transcriber_v2);
        const std::string options = "{\"engine\":\"" + std::string(engine) + "\",\"language\":\"ja\"}";
        void* handle = nullptr;
        require(mmv2_realtime_create(options.c_str(), &handle) == 0 && handle, "standalone create without base");
        auto* session = static_cast<RealtimeSession*>(handle);
        require(!session->moonshine && !session->engine, "standalone loaded unselected base");
        const char* json = nullptr;
        require(mmv2_realtime_poll(handle, &json) == 0 && json, "standalone poll");
        require(std::string(json).find("\"engineLoaded\":true") != std::string::npos, "native dispatch available");
        mmv2_free_string(json);
        g_addon_transcriber.store(nullptr); g_addon_transcriber_v2.store(nullptr);
        session->final_text = "previous"; session->has_transcript = true;
        require(transcribe_realtime_samples(session, pcm, true, 0, 0) == 0 && session->final_text.empty() &&
            !session->has_transcript && session->decision_reason == "addon_inference_failed", "lost callback final");
        require(mmv2_realtime_poll(handle, &json) == 0 && json, "lost callback poll");
        require(std::string(json).find("\"engineLoaded\":false") != std::string::npos, "native dispatch lost");
        mmv2_free_string(json);
        const int calls = callback_count;
        require(mmv2_realtime_stop(handle) == 0 && callback_count == calls && session->final_text.empty(), "empty Stop without base");
        mmv2_realtime_destroy(handle);
        require(mmv2_realtime_create(options.c_str(), &handle) != 0 && !handle, "standalone no callback create");
    }
    g_addon_transcriber.store(v1); g_addon_transcriber_v2.store(v2);
}

static void check_joint_production_replay(const std::vector<int16_t>& audio, const std::string& mock_dir) {
    require(mmv2_set_addon_joint_transcriber(mock_joint_transcriber) == 0, "register joint callback");
    for (const bool omit_whisper_dir : { false, true }) {
        const std::string missing_options = "{\"engine\":\"joint_auto\",\"language\":\"ja\","
            "\"moonshineDllDir\":\"" + mock_dir + "\",\"moonshineModelRoot\":\"unused\"," +
            (omit_whisper_dir ? "\"modelPath\":\"unused\"}" : "\"whisperDllDir\":\"" + mock_dir + "\"}");
        void* incomplete = nullptr;
        require(mmv2_realtime_create(missing_options.c_str(), &incomplete) != 0 && !incomplete,
            "JointAUTO must reject missing Whisper configuration, not create Moonshine-only");
    }
    const std::string options = "{\"engine\":\"joint_auto\",\"language\":\"ja\",\"scene\":\"talk_clean\","
        "\"addonJointRequested\":true,\"productionReplay\":true,\"usePrefilter\":false,"
        "\"moonshineDllDir\":\"" + mock_dir + "\",\"moonshineModelRoot\":\"unused\","
        "\"whisperDllDir\":\"" + mock_dir + "\",\"modelPath\":\"unused\"}";
    void* handle = nullptr;
    require(mmv2_realtime_create(options.c_str(), &handle) == 0 && handle, "joint create");
    auto* session = static_cast<RealtimeSession*>(handle);
    mobmate::ManifestRow row{};
    row.language = "ja";
    row.scene = "talk_clean";
    const auto stats = compute_pcm_stats(audio);
    auto live = [&](int seg) {
        reset_realtime_bench_session(session, row);
        std::string gate_reason;
        require(!should_skip_realtime_final_input_with_profile(session, stats, gate_reason), "live input gate");
        auto segment = audio;
        return finalize_accepted_realtime_segment(session, segment, stats, seg, stats.rms);
    };
    require(live(0) == 0, "joint live");
    require(session->joint_execution_order == std::vector<std::string>({"moonshine", "whisper", "haas_ja"}), "joint execution order");
    require(session->joint_moonshine.success && session->joint_whisper.success && callback_pcm == audio, "same input and successful candidates");
    require(session->selected_engine == "moonshine" && session->final_text == "代替文" &&
            session->joint_selection_reason == "haas_supports_moonshine", "limited disagreement selection");
    const char* joint_poll = nullptr;
    require(mmv2_realtime_poll(session, &joint_poll) == 0 && joint_poll, "joint poll consumer");
    const std::string poll_json(joint_poll);
    mmv2_free_string(joint_poll);
    require(poll_json.find("\"selected_engine\":\"moonshine\"") != std::string::npos &&
            poll_json.find("\"jointSelectionReason\":\"haas_supports_moonshine\"") != std::string::npos &&
            poll_json.find("\"addonAddedWaitMs\":") != std::string::npos, "joint poll output");

    require(mobmate::write_wav_pcm16("joint.wav", audio.data(), static_cast<int>(audio.size()), 16000), "write joint fixture");
    { std::ofstream manifest("joint.csv"); manifest << "wav,expected,language,scene\njoint.wav,代替文,ja,talk_clean\n"; }
    const char* summary = nullptr;
    require(mmv2_realtime_bench_manifest("joint.csv", "joint.jsonl", options.c_str(), &summary) == 0, "production replay");
    mmv2_free_string(summary);
    const auto replay = read_all("joint.jsonl");
    std::cout << replay;
    require(callback_pcm == audio && replay.find("\"mode\":\"native_segment_replay\"") != std::string::npos, "replay source input and mode");
    require(replay.find("\"execution_order\":[\"moonshine\",\"whisper\",\"haas_ja\"]") != std::string::npos &&
            replay.find("\"selected_engine\":\"moonshine\"") != std::string::npos &&
            replay.find("\"selection_reason\":\"haas_supports_moonshine\"") != std::string::npos &&
            replay.find("\"haas_added_wait_ms\":") != std::string::npos, "replay order and selected result");
    require(mmv2_realtime_bench_manifest_processed("joint.csv", "processed.jsonl", options.c_str(), mock_output, &summary) == 0,
        "managed production replay");
    mmv2_free_string(summary);
    const auto processed = read_all("processed.jsonl");
    std::cout << processed;
    require(processed.find("\"mode\":\"production_replay\"") != std::string::npos &&
        processed.find("\"native_transcript\":\"代替文\"") != std::string::npos &&
        processed.find("\"transcript\":\"整形結果\"") != std::string::npos &&
        processed.find("\"selected_engine\":\"phoneme_ctc\"") != std::string::npos &&
        processed.find("\"exact\":false") != std::string::npos &&
        processed.find("\"managed_output_wall_ms\":") != std::string::npos,
        "managed output must precede scoring and preserve native candidate");
    fail_output = true;
    require(mmv2_realtime_bench_manifest_processed("joint.csv", "processed-error.jsonl", options.c_str(), mock_output, &summary) == 0,
        "managed failure replay");
    const std::string failed_summary(summary);
    mmv2_free_string(summary);
    const auto processed_error = read_all("processed-error.jsonl");
    std::cout << processed_error;
    require(failed_summary.find("\"errorCount\":1") != std::string::npos &&
        processed_error.find("\"transcript\":\"\"") != std::string::npos &&
        processed_error.find("\"error\":\"managed realtime output processing failed\"") != std::string::npos,
        "managed callback failure must not silently use native output");
    fail_output = false;
    auto replay_once = [&]() {
        const char* replay_summary = nullptr;
        require(mmv2_realtime_bench_manifest("joint.csv", "joint.jsonl", options.c_str(), &replay_summary) == 0, "replay call");
        mmv2_free_string(replay_summary);
        return read_all("joint.jsonl");
    };

    joint_callback_text = "代替語";
    require(live(1) == 0 && session->selected_engine == "whisper" &&
            session->joint_selection_reason == "existing_agrees_with_haas", "agreement preserves existing selection");
    joint_callback_text = "別の言葉";
    require(live(2) == 0 && session->selected_engine == "whisper" &&
            session->joint_selection_reason == "existing_no_comparable_agreement", "all disagreement preserves existing selection");
    joint_callback_status = -2;
    require(live(3) == 0 && session->selected_engine == "whisper" && session->joint_addon_status == "Timeout", "timeout preserves existing selection");
    const auto timed_replay = replay_once();
    require(timed_replay.find("\"selected_engine\":\"whisper\"") != std::string::npos &&
            timed_replay.find("\"haas_status\":\"Timeout\"") != std::string::npos, "replay timeout preserves existing selection");
    joint_callback_status = -1;
    require(live(4) == 0 && session->selected_engine == "whisper" && session->joint_addon_status == "InferenceFailure", "failure preserves existing selection");
    joint_callback_status = 0;
    joint_callback_text.clear();
    require(live(5) == 0 && session->selected_engine == "whisper" && session->joint_addon_status == "EmptyRecognition", "empty preserves existing selection");
    joint_callback_text = "代替文";

    row.scene = "short_command";
    require(live(6) == 0 && session->joint_moonshine.attempted && !session->joint_whisper.attempted &&
            session->joint_execution_order == std::vector<std::string>({"moonshine", "haas_ja"}), "fastpath must not invent whisper");
    row.scene = "talk_clean";
    const auto module = GetModuleHandleW((std::filesystem::path(mock_dir) / "moonshine.dll").wstring().c_str());
    auto set_fail = reinterpret_cast<void (*)(int)>(GetProcAddress(module, "mobmate_mock_set_moon_fail"));
    require(set_fail != nullptr, "moon mock control");
    set_fail(1);
    require(live(7) == 0 && session->selected_engine == "haas_ja" && session->joint_existing_status == "InferenceFailure" &&
            session->joint_selection_reason == "haas_rescue", "inference failure rescue");
    const auto rescued_replay = replay_once();
    require(rescued_replay.find("\"selected_engine\":\"haas_ja\"") != std::string::npos &&
            rescued_replay.find("\"existing_joint_status\":\"InferenceFailure\"") != std::string::npos &&
            rescued_replay.find("\"execution_order\":[\"moonshine\",\"haas_ja\"]") != std::string::npos, "replay failure rescue");
    set_fail(0);

    reset_realtime_bench_session(session, row);
    session->speculative_final_candidate = {12, "代替文確認", 10, "hold_overlap", 0.9f};
    auto speculative_audio = audio;
    require(finalize_accepted_realtime_segment(session, speculative_audio, stats, 12, stats.rms) == 0 &&
            session->selected_engine == "moonshine" && session->joint_execution_order ==
            std::vector<std::string>({"moonshine_speculative", "haas_ja"}) && !session->joint_whisper.attempted &&
            session->joint_existing_wall_ms == -1, "speculative fastpath retains original selection without inventing candidates");
    RealtimeWhisperValidationResult async_result{};
    async_result.generation = session->whisper_validation_generation;
    async_result.pcm_samples = audio;
    async_result.text = "代替語";
    async_result.accepted = true;
    async_result.reason = "joint_low_pseudo_async_whisper";
    session->whisper_validation_results.push_back(async_result);
    publish_realtime_whisper_validation(session);
    require(session->selected_engine == "whisper" && session->joint_whisper.attempted && !session->joint_moonshine.attempted &&
            session->joint_execution_order == std::vector<std::string>({"whisper", "haas_ja"}), "async selection preserves prior confidence rule");
    const int before_async_reject = callback_count;
    async_result.accepted = false;
    async_result.reason = "whisper_confidence_gate:test";
    session->whisper_validation_results.push_back(async_result);
    publish_realtime_whisper_validation(session);
    require(callback_count == before_async_reject && session->joint_existing_status == "RejectedByExistingGate" &&
            !session->addon_candidate_attempted, "async confidence rejection cannot be rescued");

    const int before = callback_count;
    std::vector<int16_t> silence(16000, 0);
    reset_realtime_bench_session(session, row);
    require(transcribe_realtime_samples(session, silence, true, 8, 0.0) == 0 && callback_count == before &&
            session->joint_existing_status == "RejectedByExistingGate", "silence gate");
    require(mobmate::write_wav_pcm16("silent.wav", silence.data(), static_cast<int>(silence.size()), 16000), "write silence");
    { std::ofstream manifest("silent.csv"); manifest << "wav,expected,language,scene\nsilent.wav,代替文,ja,talk_clean\n"; }
    const char* silent_summary = nullptr;
    require(mmv2_realtime_bench_manifest("silent.csv", "silent.jsonl", options.c_str(), &silent_summary) == 0, "replay silence");
    mmv2_free_string(silent_summary);
    require(read_all("silent.jsonl").find("\"existing_joint_status\":\"RejectedByExistingGate\"") != std::string::npos &&
            callback_count == before, "replay input gate must not invoke addon");
    session->noise_profile.ready = true;
    session->noise_profile.noise_floor_rms = stats.rms * 2.0 + 1500.0;
    require(live(9) == 0 && callback_count == before && session->joint_existing_status == "RejectedByExistingGate", "noise gate");
    session->noise_profile.ready = false;
    row.language = "en";
    require(live(10) == 0 && callback_count == before, "non-JA exclusion");
    row.language = "ja";
    session->addon_joint_requested = false;
    require(live(11) == 0 && callback_count == before, "participation off");
    mmv2_realtime_destroy(handle);
}

int main(int argc, char** argv) {
    if (argc != 2) return 2;
    const auto original = std::filesystem::current_path();
    const auto mock_runtime = std::filesystem::absolute(argv[1]);
    const auto scratch = original / "temp/asr-candidates/continuation" / ("addon-native-regression-" + std::to_string(GetCurrentProcessId()));
    try {
        require(std::filesystem::create_directory(scratch), "scratch already exists");
        std::filesystem::current_path(scratch);
        std::vector<int16_t> audio(32000);
        uint32_t random = 12345;
        for (size_t i = 0; i < audio.size(); ++i) {
            random = random * 1664525u + 1013904223u;
            const double envelope = 0.5 + 0.45 * std::sin(static_cast<double>(i) * 0.0009);
            audio[i] = static_cast<int16_t>(envelope * (7000.0 * std::sin(i * 0.061) + (static_cast<int>(random >> 20) - 2048)));
        }
        require(mmv2_set_addon_transcriber(mock_transcriber) == 0, "register callback");
        check_onset_before_same_poll_transcription(audio);
        check_rms_onset_transitions();
        check_onset_unregister_drains();
        {
            RealtimeSession session;
            session.realtime_engine = "haas_ja";
            session.language = "ja";
            session.scene = "talk_clean";
            const auto stats = compute_pcm_stats(audio);
            require(transcribe_realtime_samples(&session, audio, false, 1, stats.rms, &stats) == 0, "live finalizer");
            require(session.addon_candidate_success && session.addon_candidate_text == "試験用", "live candidate");
            if (session.selected_engine != "haas_ja" || session.final_text != "試験用")
            {
                std::string reason;
                should_accept_realtime_final_text("試験用", stats, "haas_ja", "ja", reason, &audio, session.vad_start_prob);
                throw std::runtime_error("live selected result: " + session.decision_reason + ":" + reason);
            }
            const auto live_pcm = callback_pcm;
            const char* poll = nullptr;
            require(mmv2_realtime_poll(&session, &poll) == 0 && poll, "poll consumer");
            const std::string poll_json(poll);
            mmv2_free_string(poll);
            require(poll_json.find("\"confidence_score\":null") != std::string::npos, "unknown confidence");
            require(poll_json.find("\"success\":true") != std::string::npos, "live candidate status");

            require(mobmate::write_wav_pcm16("sample.wav", audio.data(), static_cast<int>(audio.size()), 16000), "write fixture");
            { std::ofstream manifest("manifest.csv"); manifest << "wav,expected,language,scene\nsample.wav,試験用,ja,talk_clean\n"; }
            const std::string options = "{\"engine\":\"haas_ja\",\"language\":\"ja\",\"scene\":\"talk_clean\",\"usePrefilter\":true,\"prefilterProfile\":\"auto\"}";
            const char* summary = nullptr;
            require(mmv2_realtime_bench_manifest("manifest.csv", "result.jsonl", options.c_str(), &summary) == 0, "bench consumer");
            mmv2_free_string(summary);
            const auto bench = read_all("result.jsonl");
            require(callback_pcm == live_pcm, "live and bench PCM differ");
            require(bench.find("\"selected_engine\":\"haas_ja\"") != std::string::npos, "bench selected result");
            require(bench.find("\"addon_candidate\":{\"engine\":\"haas_ja\",\"text\":\"試験用\",\"success\":true") != std::string::npos, "bench candidate");
            require(bench.find("\"latencyMs\":17") != std::string::npos && bench.find("\"wall_ms\":") != std::string::npos, "separate timing");
            fail_callback = true;
            require(transcribe_realtime_samples(&session, audio, false, 2, stats.rms, &stats) == 0, "live standalone failure");
            require(!session.addon_candidate_success && session.selected_engine == "none" && session.final_text.empty() &&
                    !session.has_transcript && session.decision_reason == "addon_inference_failed" &&
                    session.state == RealtimeState::kCompleted && session.realtime_engine == "haas_ja", "standalone failure selection");
            session.final_text = "previous final"; session.has_transcript = true;
            require(transcribe_realtime_samples(&session, audio, true, 3, stats.rms, &stats) == 0 &&
                    session.final_text.empty() && !session.has_transcript && session.selected_engine == "none" &&
                    session.state == RealtimeState::kRecording, "recording failure must not revive the previous final");
            require(mmv2_realtime_bench_manifest("manifest.csv", "fallback.jsonl", options.c_str(), &summary) == 0, "bench standalone failure");
            mmv2_free_string(summary);
            const auto fallback = read_all("fallback.jsonl");
            require(fallback.find("\"selected_engine\":\"none\"") != std::string::npos &&
                    fallback.find("\"transcript\":\"\"") != std::string::npos &&
                    fallback.find("\"addon_inference_failed\"") != std::string::npos &&
                    fallback.find("\"success\":false") != std::string::npos, "bench standalone failure output");
            fail_callback = false;
            const int before = callback_count;
            std::vector<int16_t> silence(16000, 0);
            require(transcribe_realtime_samples(&session, silence, false, 3, 0.0) == 0, "silence guard");
            require(callback_count == before && !session.addon_candidate_attempted, "silence invoked addon");
            void* invalid = nullptr;
            require(mmv2_realtime_create("{\"engine\":\"haas_ja\",\"language\":\"en\"}", &invalid) != 0 && !invalid, "non-JA gate");
        }
        check_standalone_create_without_base();
        check_wav_error_rows();
        check_joint_production_replay(audio, mock_runtime.generic_string());
        require(mmv2_set_addon_transcriber_v2(mock_transcriber_v2) == 0, "register v2 callback");
        for (const auto* engine : { "parakeet_ja", "qwen3_asr_ja", "nemotron_3_5" }) {
            const std::string mode = std::string(engine) == "qwen3_asr_ja" ? "auto" : "requested";
            const std::string options = "{\"engine\":\"" + std::string(engine) + "\",\"language\":\"ja\",\"scene\":\"talk_clean\",\"productionReplay\":true," +
                "\"addonLanguageMode\":\"" + mode + "\",\"addonModelId\":\"q8_0\",\"addonModelRevision\":\"fixed\"}";
            const char* summary = nullptr;
            require(mmv2_realtime_bench_manifest_processed("manifest.csv", "v2-result.jsonl", options.c_str(), mock_output, &summary) == 0, "v2 public replay");
            mmv2_free_string(summary);
            require(callback_engine == engine && callback_language == "ja" && callback_mode == mode, "v2 context identity");
            const auto output = read_all("v2-result.jsonl");
            require(output.find("\"addon_candidate\":{\"engine\":\"" + std::string(engine) + "\"") != std::string::npos, "v2 candidate container");
            require(output.find("\"addon_model_revision\":\"fixed\"") != std::string::npos && output.find("\"addon_wall_ms\":") != std::string::npos, "generic measurements");
            require(output.find("\"native_selected_engine\":\"" + std::string(engine) + "\"") != std::string::npos, "v2 native selected source");
            fail_callback = true;
            require(mmv2_realtime_bench_manifest("manifest.csv", "v2-fallback.jsonl", options.c_str(), &summary) == 0, "v2 standalone failure replay");
            mmv2_free_string(summary);
            const auto failure = read_all("v2-fallback.jsonl");
            require(failure.find("\"selected_engine\":\"none\"") != std::string::npos &&
                    failure.find("\"transcript\":\"\"") != std::string::npos &&
                    failure.find("\"addon_inference_failed\"") != std::string::npos &&
                    failure.find("\"success\":false") != std::string::npos, "v2 standalone failure output");
            fail_callback = false;
            require(mmv2_set_addon_joint_transcriber_v2(mock_transcriber_v2) == 0, "register joint v2");
            const std::string joint_options = std::string("{\"engine\":\"joint_auto\",\"language\":\"ja\",\"scene\":\"talk_clean\",\"productionReplay\":true,") +
                "\"addonJointRequested\":true,\"addonEngineId\":\"" + std::string(engine) + "\",\"addonLanguageMode\":\"" + mode +
                "\",\"moonshineDllDir\":\"" + mock_runtime.generic_string() + "\",\"moonshineModelRoot\":\"unused\",\"whisperDllDir\":\"" +
                mock_runtime.generic_string() + "\",\"modelPath\":\"unused\"}";
            require(mmv2_realtime_bench_manifest("manifest.csv", "v2-joint.jsonl", joint_options.c_str(), &summary) == 0, "joint v2 replay");
            mmv2_free_string(summary);
            const auto joint = read_all("v2-joint.jsonl");
            require(callback_engine == engine && callback_language == "ja" && callback_mode == mode, "joint v2 context");
            require(joint.find("\"haas_status\":\"NotRequested\"") != std::string::npos &&
                joint.find("\"haas_wall_ms\":-1") != std::string::npos, "new Joint measurements must not impersonate Haas");
            require(mmv2_set_addon_joint_transcriber_v2(nullptr) == 0, "unregister joint v2");
        }
        {
            { std::ofstream manifest("language.csv"); manifest << "wav,expected,language,scene\nsample.wav,試験用,en,talk_clean\n"; }
            const std::string options = "{\"engine\":\"qwen3_asr_ja\",\"language\":\"ja\",\"addonLanguageMode\":\"requested\",\"moonshineDllDir\":\"" +
                mock_runtime.generic_string() + "\",\"moonshineModelRoot\":\"unused\"}";
            const char* summary = nullptr;
            require(mmv2_realtime_bench_manifest("language.csv", "language.jsonl", options.c_str(), &summary) == 0, "row language replay");
            mmv2_free_string(summary);
            require(callback_engine == "qwen3_asr_ja" && callback_language == "en" && callback_mode == "requested", "row overrides app language without changing hint policy");
        }
        require(mmv2_set_addon_transcriber_v2(nullptr) == 0, "unregister v2");
        void* invalid = nullptr;
        require(mmv2_realtime_create("{\"engine\":\"parakeet_ja\",\"language\":\"ja\"}", &invalid) != 0 && !invalid, "new engine must not alias v1 Haas");
        std::filesystem::current_path(original);
        std::filesystem::remove_all(scratch);
        std::cout << "addon_native_regression: PASS\n";
        return 0;
    } catch (const std::exception& ex) {
        std::filesystem::current_path(original);
        std::cerr << "addon_native_regression: " << ex.what() << " (scratch retained: " << scratch << ")\n";
        return 1;
    }
}
