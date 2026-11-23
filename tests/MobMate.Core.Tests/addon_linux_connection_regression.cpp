// Actual Linux poll/finalizer, with buffered PCM only: no device initialization or capture.
#include <cstdint>
#include <vector>
#include <string>
#include <mutex>
#include <atomic>
#include <thread>
#include <functional>
// Test-only access to the existing buffer; do not add a product injection API.
#define private public
#include "mobmate_native_core/audio_capture_linux.h"
#undef private
// Keep the fake at the Silero C boundary: the public poll and capture buffer
// below are the real implementation, with no model or audio device involved.
#define silero_vad_score fixture_silero_vad_score
#define silero_vad_reset fixture_silero_vad_reset
#define silero_vad_free fixture_silero_vad_free
#include "../../native/mobmate_native_core/src/native_api.cpp"
#undef silero_vad_score
#undef silero_vad_reset
#undef silero_vad_free
#include <iostream>
#include <stdexcept>

struct FakeSilero {
    std::vector<std::vector<int16_t>> frames;
    std::vector<double> probabilities;
    int resets = 0;
};
extern "C" double fixture_silero_vad_score(silero_vad_handle* handle, const int16_t* pcm, int count, int rate) {
    if (!handle || count != 512 || rate != 16000) throw std::runtime_error("invalid Silero frame");
    auto& fake = *reinterpret_cast<FakeSilero*>(handle);
    fake.frames.emplace_back(pcm, pcm + count);
    const size_t index = fake.frames.size() - 1;
    return index < fake.probabilities.size() ? fake.probabilities[index] : 0.0;
}
extern "C" void fixture_silero_vad_reset(silero_vad_handle* handle) {
    ++reinterpret_cast<FakeSilero*>(handle)->resets;
}
extern "C" void fixture_silero_vad_free(silero_vad_handle*) {}

static int linux_callback_count = 0, linux_callback_code = 0;
static std::string linux_callback_text = "First command";
static std::string linux_callback_engine, linux_callback_language, linux_callback_mode;
static std::vector<int16_t> linux_callback_pcm;
static mobmate::MiniAudioCapture* lose_during_callback = nullptr;
static int linux_qwen_callback(const char* engine, const char* language, const char* mode, const int16_t* pcm, int count,
    int rate, char* text, int capacity, int64_t* ms) {
    ++linux_callback_count;
    linux_callback_engine = engine; linux_callback_language = language; linux_callback_mode = mode;
    linux_callback_pcm.assign(pcm, pcm + count);
    if (lose_during_callback) lose_during_callback->_source_lost.store(true);
    if (std::string(engine) != "qwen3_asr_ja" || (std::string(language) != "en" && std::string(language) != "ja") ||
        rate != 16000 || capacity <= static_cast<int>(linux_callback_text.size())) return -1;
    if (linux_callback_code) return linux_callback_code;
    std::strcpy(text, linux_callback_text.c_str());
    *ms = 17;
    return 0;
}
static void linux_require(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
static std::string linux_poll(RealtimeSession& session) {
    const char* json = nullptr;
    linux_require(mmv2_realtime_poll(&session, &json) == 0 && json, "public poll failed");
    std::string result(json);
    mmv2_free_string(json);
    return result;
}
static std::vector<int16_t> linux_numbered_pcm(size_t count) {
    std::vector<int16_t> pcm(count);
    for (size_t i = 0; i < count; ++i) pcm[i] = static_cast<int16_t>(i + 1);
    return pcm;
}
static void linux_require_frames(const FakeSilero& fake, const std::vector<int16_t>& pcm,
                                 size_t first, size_t count) {
    linux_require(fake.frames.size() == count, "wrong Silero frame count");
    for (size_t i = 0; i < count; ++i)
        linux_require(fake.frames[i] == std::vector<int16_t>(pcm.begin() + first + i * 512,
                                                            pcm.begin() + first + (i + 1) * 512),
                      "Silero PCM frame duplicated, skipped, or shifted");
}
static int linux_check_silero_poll_continuity() {
    int failures = 0;
    auto check = [&](const char* name, auto work) {
        try { work(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    };
    check("Silero subframe polls and multiple frames retain exact PCM", [&] {
        FakeSilero fake;
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording;
        const auto pcm = linux_numbered_pcm(2048);
        auto append_to = [&](size_t end) { s.capture._pcm_buffer.assign(pcm.begin(), pcm.begin() + end); linux_poll(s); };
        append_to(200);
        linux_require(fake.frames.empty() && s.chunk_samples == 0, "first short poll consumed unprocessed PCM");
        append_to(511);
        linux_require(fake.frames.empty() && s.chunk_samples == 0, "second short poll consumed unprocessed PCM");
        append_to(512);
        linux_require(s.chunk_samples == 512, "completed first frame did not advance cursor");
        linux_require_frames(fake, pcm, 0, 1);
        append_to(1823);
        linux_require(s.chunk_samples == 1536, "multiple frames or pending tail cursor incorrect");
        linux_require_frames(fake, pcm, 0, 3);
        linux_poll(s);
        linux_require(s.chunk_samples == 1536, "unchanged poll consumed pending tail");
        linux_require_frames(fake, pcm, 0, 3);
        append_to(2048);
        linux_require(s.chunk_samples == 2048, "completed tail did not advance cursor");
        linux_require_frames(fake, pcm, 0, 4);
    });
    check("Silero score error retains energy fallback and frame position", [&] {
        FakeSilero fake;
        fake.probabilities = {-1.0, -1.0};
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording;
        std::vector<int16_t> pcm(1024, 0);
        std::fill(pcm.begin() + 512, pcm.end(), 2000);
        s.capture._pcm_buffer.assign(pcm.begin(), pcm.begin() + 512);
        const auto silence = linux_poll(s);
        linux_require(s.chunk_samples == 512 && s.vad_state == VadState::kSilence &&
            silence.find("\"vadState\":\"silence\"") != std::string::npos, "score error silence fallback changed");
        s.capture._pcm_buffer = pcm;
        const auto speech = linux_poll(s);
        linux_require(s.chunk_samples == 1024 && s.vad_state == VadState::kSpeech &&
            s.vad_start_prob == s.vad_silero_continue_threshold &&
            speech.find("\"vadState\":\"speech\"") != std::string::npos, "score error energy fallback changed");
        linux_require_frames(fake, pcm, 0, 2);
    });
    check("Silero VAD end break advances only the scored frames", [&] {
        FakeSilero fake;
        fake.probabilities = {0.9, 0.0};
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording; s.vad_silence_duration_ms = 32;
        const auto pcm = linux_numbered_pcm(8300);
        s.capture._pcm_buffer.assign(pcm.begin(), pcm.begin() + 1700);
        linux_poll(s);
        linux_require(s.vad_state == VadState::kEndDetected && s.vad_end_sample == 1024,
                      "VAD end boundary changed");
        linux_require(s.chunk_samples == 1024 && fake.resets == 0, "break advanced beyond scored frame");
        linux_require_frames(fake, pcm, 0, 2);
        linux_poll(s);
        linux_require(s.vad_state == VadState::kEndDetected && s.vad_end_sample == 1024 &&
            s.chunk_samples == 1024 && fake.frames.size() == 2, "pending end was lost before minimum segment size");
        s.capture._pcm_buffer = pcm;
        linux_poll(s);
        linux_require(fake.resets == 1 && s.chunk_samples == 0 &&
            s.capture.get_samples() == std::vector<int16_t>(pcm.begin() + 1024, pcm.end()),
            "pending end failed to finalize/discard at minimum size");
        linux_poll(s);
        linux_require(s.chunk_samples == 14 * 512, "pending end leftover cursor incorrect");
        linux_require_frames(fake, pcm, 0, 16);
    });
    check("Silero discard resets cursor and preserves unscored buffer", [&] {
        FakeSilero fake;
        fake.probabilities = {0.9, 0.0};
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording; s.vad_silence_duration_ms = 32;
        const auto pcm = linux_numbered_pcm(10000);
        s.capture._pcm_buffer = pcm;
        linux_poll(s);
        linux_require(fake.resets == 1 && s.chunk_samples == 0 &&
            s.capture.get_samples() == std::vector<int16_t>(pcm.begin() + 1024, pcm.end()),
            "final discard did not preserve the unscored suffix/reset");
        linux_require_frames(fake, pcm, 0, 2);
        fake.frames.clear(); fake.probabilities.clear();
        linux_poll(s);
        linux_require(s.chunk_samples == 17 * 512 && fake.resets == 1, "leftover cursor incorrect after reset");
        linux_require_frames(fake, pcm, 1024, 17);
    });
    check("Silero pending tail is discarded on capture loss", [&] {
        FakeSilero fake;
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording;
        s.capture._pcm_buffer = linux_numbered_pcm(700);
        linux_poll(s);
        linux_require(s.chunk_samples == 512, "expected pending tail before capture loss");
        s.capture_initialized = true; s.capture._running.store(true); s.capture._source_lost.store(true);
        const auto lost = linux_poll(s);
        linux_require(s.state == RealtimeState::kFailed && s.chunk_samples == 0 &&
            s.capture.get_samples().empty() && fake.resets == 1 && fake.frames.size() == 1 &&
            lost.find("capture_device_lost") != std::string::npos, "loss failed to discard/reset pending PCM");
    });
    check("Silero Stop does not resume pending input", [&] {
        FakeSilero fake;
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording;
        s.capture._pcm_buffer = linux_numbered_pcm(700);
        linux_poll(s);
        linux_require(mmv2_realtime_stop(&s) == 0 && fake.resets == 1 &&
            s.state == RealtimeState::kCompleted, "Stop did not reset Silero or complete");
        s.capture._pcm_buffer.insert(s.capture._pcm_buffer.end(), 400, 7);
        linux_poll(s);
        linux_require(s.state == RealtimeState::kCompleted && fake.frames.size() == 1,
            "poll resumed Silero after Stop");
    });
    check("RMS poll still detects speech and end without Silero", [&] {
        RealtimeSession s;
        s.state = RealtimeState::kRecording; s.vad_silence_duration_ms = 50;
        s.capture._pcm_buffer.assign(1600, 2000);
        const auto speech = linux_poll(s);
        linux_require(s.vad_state == VadState::kSpeech && s.vad_speech_count == 1 &&
            speech.find("\"vadState\":\"speech\"") != std::string::npos, "RMS speech detection changed");
        s.capture._pcm_buffer.insert(s.capture._pcm_buffer.end(), 1600, 0);
        const auto end = linux_poll(s);
        linux_require(s.vad_state == VadState::kEndDetected && s.vad_speech_count == 1 &&
            end.find("\"vadState\":\"end_detected\"") != std::string::npos, "RMS end detection changed");
    });
    return failures ? 1 : 0;
}
static std::vector<int16_t> linux_audio() {
    std::vector<int16_t> audio(32000);
    uint32_t random = 12345;
    for (size_t i = 0; i < audio.size() - 3200; ++i) {
        random = random * 1664525u + 1013904223u;
        const double envelope = 0.5 + 0.45 * std::sin(i * 0.0009);
        audio[i] = static_cast<int16_t>(envelope * (7000.0 * std::sin(i * 0.061) + (static_cast<int>(random >> 20) - 2048)));
    }
    return audio;
}
static int linux_check_silero_pre_roll() {
    try {
        FakeSilero fake;
        fake.probabilities.resize(80, 0.0);
        std::fill(fake.probabilities.begin() + 35, fake.probabilities.begin() + 55, 0.9);
        std::fill(fake.probabilities.begin() + 59, fake.probabilities.begin() + 77, 0.9);
        std::vector<int16_t> pcm(80 * 512);
        for (size_t i = 0; i < pcm.size(); ++i)
            pcm[i] = static_cast<int16_t>((0.7 + 0.2 * std::sin(i * 0.001)) *
                (7000.0 * std::sin(i * 0.041) + 1300.0 * std::sin(i * 0.093)));
        RealtimeSession s;
        s.vad_silero = reinterpret_cast<silero_vad_handle*>(&fake);
        s.state = RealtimeState::kRecording; s.vad_silence_duration_ms = 32;
        s.realtime_engine = s.addon_engine_id = "qwen3_asr_ja";
        s.language = "en"; s.addon_language_mode = "requested";
        linux_callback_count = linux_callback_code = 0; linux_callback_text = "First command";
        mmv2_set_addon_transcriber_v2(linux_qwen_callback);
        s.capture._pcm_buffer = pcm;
        const auto first_poll = linux_poll(s);
        const size_t first_end = 56 * 512;
        const size_t first_start = 35 * 512 - RealtimeSession::ASR_PRE_ROLL_MAX_SAMPLES;
        linux_require(linux_callback_count == 1, "first segment was not passed to the callback");
        linux_require(linux_callback_pcm == std::vector<int16_t>(pcm.begin() + first_start, pcm.begin() + first_end) &&
            s.last_final_pcm16 == linux_callback_pcm, "first segment must start exactly at the capped pre-roll, with no duplicate samples");
        linux_require(linux_callback_engine == "qwen3_asr_ja" && linux_callback_language == "en" &&
            linux_callback_mode == "requested" && s.final_text == "First command" && s.selected_engine == "qwen3_asr_ja" &&
            s.decision_reason == "addon_selected" && first_poll.find("\"success\":true") != std::string::npos,
            "pre-roll changed callback selection or final metadata");
        linux_require(s.capture.get_samples() == std::vector<int16_t>(pcm.begin() + first_end, pcm.end()) &&
            s.chunk_samples == 0, "first segment consumed the unscored suffix");
        linux_callback_text = "Next command";
        linux_poll(s);
        const size_t second_end = 78 * 512;
        linux_require(linux_callback_count == 2 && linux_callback_pcm ==
            std::vector<int16_t>(pcm.begin() + first_end, pcm.begin() + second_end) &&
            s.last_final_pcm16 == linux_callback_pcm && s.final_text == "Next command",
            "next segment must start at the remaining suffix without replaying the previous segment");
        linux_require(s.capture.get_samples() == std::vector<int16_t>(pcm.begin() + second_end, pcm.end()),
            "second segment consumed its unscored suffix");
        linux_require(mmv2_realtime_stop(&s) == 0 && s.state == RealtimeState::kCompleted,
            "Stop failed after pre-roll segments");
        linux_poll(s);
        linux_require(linux_callback_count == 2 && s.state == RealtimeState::kCompleted,
            "Stop resumed or retranscribed pending input");
        FakeSilero idle_fake;
        RealtimeSession idle;
        idle.vad_silero = reinterpret_cast<silero_vad_handle*>(&idle_fake);
        idle.state = RealtimeState::kRecording;
        const auto long_idle = linux_numbered_pcm(320512);
        idle.capture._pcm_buffer = long_idle;
        linux_poll(idle);
        linux_require(idle.capture.get_samples() == std::vector<int16_t>(long_idle.end() - 16000, long_idle.end()) &&
            idle.chunk_samples == 31 * 512 && idle_fake.resets == 1,
            "existing idle buffer cap did not retain and score the last second");
        std::cout << "PASS Silero bounded pre-roll, suffix continuity and Stop\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL Silero bounded pre-roll, suffix continuity and Stop: " << error.what() << '\n';
        return 1;
    }
}
static std::string linux_poll_segment(RealtimeSession& session, const std::vector<int16_t>& audio) {
    session.capture._pcm_buffer = audio;
    session.state = RealtimeState::kRecording;
    session.vad_state = VadState::kEndDetected;
    session.vad_speech_count = 1;
    session.vad_end_sample = static_cast<int64_t>(audio.size());
    const char* json = nullptr;
    linux_require(mmv2_realtime_poll(&session, &json) == 0 && json, "public poll failed");
    std::string result(json);
    mmv2_free_string(json);
    return result;
}
static std::vector<int16_t> linux_gate_padding(const std::vector<int16_t>& audio) {
    std::vector<int16_t> padded(48000, 0);
    padded.insert(padded.end(), audio.begin(), audio.end());
    padded.resize(padded.size() + 16000, 0);
    return padded;
}
static std::vector<int16_t> linux_voiced_gate_fixture(bool sparse) {
    std::vector<int16_t> audio(32000);
    for (size_t i = 0; i < audio.size(); ++i) {
        // Fully voiced shape, or sparse hum bursts spanning the same two seconds.
        // This is a gate fixture, not a claim of real speech or ASR accuracy.
        if (sparse && i >= 1280 && i < 30720) continue;
        const double envelope = 0.75 + 0.20 * std::sin(i * 0.001);
        audio[i] = static_cast<int16_t>(envelope * (7000.0 * std::sin(i * 0.03926990817) +
            1200.0 * std::sin(i * 0.07853981634)));
    }
    return audio;
}
static bool linux_gate_accepts(const std::vector<int16_t>& audio, const std::string& text, std::string& reason) {
    const auto stats = compute_pcm_stats(audio);
    return !should_skip_realtime_final_input(stats, reason) &&
        should_accept_realtime_final_text(text, stats, "qwen3_asr_ja", "ja", reason, &audio, 0.0);
}
static int linux_check_gate_padding() {
    int failures = 0;
    auto check = [&](const char* name, auto work) {
        try { linux_callback_count = linux_callback_code = 0; work(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    };
    mmv2_set_addon_transcriber_v2(linux_qwen_callback);
    const auto voiced = linux_voiced_gate_fixture(false);
    const auto padded = linux_gate_padding(voiced);
    check("accepted voiced region is not a hum because of outer silence", [&] {
        std::string reason;
        linux_require(linux_gate_accepts(voiced, "こんにちは", reason), "unpadded baseline must be accepted");
        reason.clear();
        const bool accepted = linux_gate_accepts(padded, "こんにちは", reason);
        linux_require(accepted, reason.c_str());
    });
    check("public Qwen-only poll preserves candidate and metadata with padding", [&] {
        RealtimeSession session;
        session.realtime_engine = session.addon_engine_id = "qwen3_asr_ja"; session.language = "ja";
        linux_callback_text = "こんにちは";
        const auto poll = linux_poll_segment(session, padded);
        linux_require(linux_callback_count == 1 && session.addon_candidate_success &&
            session.final_text == linux_callback_text && session.selected_engine == "qwen3_asr_ja" &&
            session.decision_reason == "addon_selected" && session.latency_ms == 17 &&
            session.last_final_pcm16 == padded && poll.find("\"success\":true") != std::string::npos,
            "padding discarded successful single-engine candidate or changed its metadata");
        linux_callback_code = -1;
        linux_poll_segment(session, padded);
        linux_require(session.final_text.empty() && !session.has_transcript && session.selected_engine == "none" &&
            session.decision_reason == "addon_inference_failed", "failed callback must not fallback or revive final");
        linux_callback_code = 0; linux_callback_text.clear();
        linux_poll_segment(session, padded);
        linux_require(session.final_text.empty() && !session.has_transcript && session.selected_engine == "none",
            "empty callback must not fallback or revive final");
    });
    check("sparse hum remains rejected with and without padding", [&] {
        const auto hum = linux_voiced_gate_fixture(true);
        for (const auto& input : {hum, linux_gate_padding(hum)}) {
            std::string reason;
            linux_require(!linux_gate_accepts(input, "こんにちは", reason) &&
                reason.find("acoustic_hallucination") == 0, "sparse hum was admitted");
        }
    });
    check("silence, boilerplate, wrong script and decode-confidence rejection stay intact", [&] {
        std::string reason;
        linux_require(!linux_gate_accepts(std::vector<int16_t>(32000), "こんにちは", reason) && reason == "no_speech",
            "silence admitted");
        reason.clear();
        linux_require(!linux_gate_accepts(padded, "ご視聴ありがとうございました", reason) && reason == "boilerplate_hallucination",
            "boilerplate admitted");
        reason.clear();
        linux_require(!linux_gate_accepts(padded, "230", reason) && reason == "language_script_mismatch",
            "wrong script admitted");
        linux_require(whisper_confidence_rejects_final(0.9f, 0.9f, "こんにちは", reason), "confidence gate weakened");
    });
    check("existing high-ZCR and diffuse-air rejection use their unchanged whole-segment density", [&] {
        RtAsrGateAnalysis noise{};
        noise.duration_ms = 1600; noise.rms = 1800; noise.peak = 12000;
        noise.zcr = 0.270f; noise.periodicity = 0.30f; noise.start_prob = 0.403f;
        noise.active_ratio = 0.230f; noise.voiced_active_ratio = 0.118f; noise.air_active_ratio = 0.0f;
        linux_require(!rt_asr_gate_accepts_nonliteral_text(noise), "high-ZCR noise admitted");
        noise.zcr = 0.09f; noise.periodicity = 0.10f; noise.air_active_ratio = 0.50f;
        linux_require(!rt_asr_gate_accepts_nonliteral_text(noise), "diffuse air admitted");
    });
    return failures ? 1 : 0;
}
static int linux_check_standalone_failure() {
    try {
        mmv2_set_addon_transcriber_v2(linux_qwen_callback);
        mmv2_set_addon_joint_transcriber_v2(linux_qwen_callback);
        const auto audio = linux_audio();
        for (const auto* language : {"ja", "en"}) {
            RealtimeSession s;
            s.realtime_engine = s.addon_engine_id = "qwen3_asr_ja"; s.language = language;
            linux_callback_count = linux_callback_code = 0;
            linux_callback_text = std::string(language) == "ja" ? "最初の指示" : "First command";
            linux_poll_segment(s, audio);
            linux_require(s.has_transcript && s.final_text == linux_callback_text, "prior standalone final");
            linux_callback_code = -1;
            const auto poll = linux_poll_segment(s, audio);
            linux_require(linux_callback_count == 2 && s.final_text.empty() && !s.has_transcript &&
                s.selected_engine == "none" && s.decision_reason == "addon_inference_failed" &&
                s.state == RealtimeState::kRecording && s.realtime_engine == "qwen3_asr_ja" &&
                poll.find("\"success\":false") != std::string::npos, "standalone failure returned stale or alternative output");
        }
        RealtimeSession joint;
        joint.realtime_engine = "joint_auto"; joint.addon_joint_requested = true;
        joint.addon_engine_id = "qwen3_asr_ja"; joint.language = joint.addon_joint_language = "en";
        joint.final_text = "Valid base"; joint.has_transcript = true; joint.selected_engine = "whisper";
        linux_callback_count = 0; linux_callback_code = -1;
        apply_joint_addon(&joint, audio, true, nullptr, 0, {});
        linux_require(linux_callback_count == 1 && joint.final_text == "Valid base" && joint.selected_engine == "whisper" &&
            joint.joint_addon_status == "InferenceFailure", "joint addon failure lost valid base");
        reset_joint_trace(&joint); joint.final_text.clear(); joint.has_transcript = false; joint.selected_engine = "none";
        linux_callback_code = 0; linux_callback_text = "Rescued command";
        apply_joint_addon(&joint, audio, true, nullptr, 0, {});
        linux_require(joint.final_text == linux_callback_text && joint.selected_engine == "qwen3_asr_ja" &&
            joint.joint_selection_reason == "addon_rescue", "authorized joint rescue changed");
        reset_joint_trace(&joint); joint.addon_joint_language = "ja";
        const int before = linux_callback_count;
        apply_joint_addon(&joint, audio, true, nullptr, 0, {});
        linux_require(linux_callback_count == before, "joint language consent inherited");
        std::cout << "PASS standalone failure and related joint boundaries (current native_api.cpp)\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "FAIL standalone failure: " << error.what() << '\n'; return 1;
    }
}
#ifdef STANDALONE_FAILURE_REGRESSION
int main() { return linux_check_standalone_failure(); }
#else
int main(int argc, char** argv) {
    if (argc == 3 && std::string(argv[1]) == "--joint-create-fixture") {
        const std::string runtime = std::filesystem::absolute(argv[2]).generic_string();
        auto create = [&](const std::string& engine, const std::string& moon_dir,
                          const std::string& moon_root, const std::string& whisper_dir, bool success,
                          const std::string& model = "unused") {
            const std::string options = "{\"engine\":\"" + engine + "\",\"language\":\"ja\","
                "\"moonshineDllDir\":\"" + moon_dir + "\",\"moonshineModelRoot\":\"" + moon_root + "\","
                "\"whisperDllDir\":\"" + whisper_dir + "\",\"modelPath\":\"" + model + "\"}";
            void* handle = nullptr;
            const int result = mmv2_realtime_create(options.c_str(), &handle);
            linux_require((result == 0 && handle != nullptr) == success, "unexpected native create result");
            if (!success) linux_require(result != 0 && handle == nullptr, "failed create retained a handle");
            if (success) {
                auto* session = static_cast<RealtimeSession*>(handle);
                linux_require(session->realtime_engine == engine && session->language == "ja", "native selection changed");
                if (engine == "joint_auto") linux_require(session->moonshine && session->engine, "JointAUTO omitted a base engine");
                mmv2_realtime_destroy(handle);
            }
        };
        try {
            create("joint_auto", runtime, "unused", runtime, true);
            create("joint_auto", runtime + "/missing", "unused", runtime, false);
            create("joint_auto", runtime, "fail-load", runtime, false);
            create("joint_auto", runtime, "unused", runtime + "/missing", false);
            create("joint_auto", runtime, "unused", runtime + "/bad-dependency", false);
            create("joint_auto", runtime, "unused", "", false);
            create("joint_auto", runtime, "unused", runtime, false, "");
            create("whisper", runtime + "/missing", "unused", runtime, true);
            std::cout << "PASS native JointAUTO create and failure boundaries with fake libraries; no capture or inference\n";
            return 0;
        } catch (const std::exception& error) {
            std::cerr << "FAIL native JointAUTO create: " << error.what() << '\n';
            return 1;
        }
    }
    if (argc == 2 && std::string(argv[1]) == "--silero-poll-continuity") {
        const int continuity = linux_check_silero_poll_continuity();
        return continuity | linux_check_silero_pre_roll();
    }
    if (argc == 2 && std::string(argv[1]) == "--gate-padding") return linux_check_gate_padding();
    if (argc == 2 && std::string(argv[1]) == "--standalone-failure-only") return linux_check_standalone_failure();
    if (argc == 4 && std::string(argv[1]) == "--inspect-pcm") {
        std::ifstream input(argv[2], std::ios::binary);
        std::vector<char> bytes{std::istreambuf_iterator<char>(input), {}};
        if (!input || bytes.empty() || bytes.size() % 2) return 2;
        std::vector<int16_t> audio(bytes.size() / 2);
        std::memcpy(audio.data(), bytes.data(), bytes.size());
        std::string original_reason, padded_reason;
        const bool accepted = linux_gate_accepts(audio, argv[3], original_reason);
        const bool padded_accepted = linux_gate_accepts(linux_gate_padding(audio), argv[3], padded_reason);
        std::cout << "{\"accepted\":" << (accepted ? "true" : "false")
            << ",\"padded_accepted\":" << (padded_accepted ? "true" : "false")
            << ",\"reason\":\"" << json_escape(original_reason) << "\",\"padded_reason\":\""
            << json_escape(padded_reason) << "\"}\n";
        return 0;
    }
    if (argc != 1) return 2;
    int failures = 0;
    auto check = [&](const char* name, auto work) {
        try { linux_callback_count = linux_callback_code = 0; lose_during_callback = nullptr; work(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failures; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    };
    mmv2_set_addon_joint_transcriber_v2(linux_qwen_callback);
    const auto audio = linux_audio();
    for (const auto* language : {"ja", "en"}) {
        check(language, [&] {
            RealtimeSession session;
            session.realtime_engine = "joint_auto"; session.language = session.addon_joint_language = language;
            session.addon_engine_id = "qwen3_asr_ja"; session.addon_joint_requested = true;
            linux_callback_text = std::string(language) == "ja" ? "最初の指示" : "First command";
            linux_poll_segment(session, audio);
            linux_require(session.selected_engine == "qwen3_asr_ja", "first unavailable-base rescue failed");
            linux_callback_text = std::string(language) == "ja" ? "次の指示" : "Next command";
            const auto poll = linux_poll_segment(session, audio);
            linux_require(linux_callback_count == 2 && session.final_text == linux_callback_text &&
                session.joint_existing_status == "Unavailable" && poll.find("joint_addon_rescue") != std::string::npos,
                "previous final was treated as this segment's existing result");
            linux_callback_code = -1;
            linux_poll_segment(session, audio);
            linux_require(!session.has_transcript && session.final_text.empty() && session.selected_engine == "none" &&
                session.joint_addon_status == "InferenceFailure", "failed new segment resurrected previous output");
        });
    }
    check("input gate trace", [&] {
        RealtimeSession session;
        session.realtime_engine = "joint_auto"; session.language = session.addon_joint_language = "en";
        session.addon_engine_id = "qwen3_asr_ja"; session.addon_joint_requested = true;
        linux_callback_text = "First command";
        linux_poll_segment(session, audio);
        const auto previous = session.final_text;
        const auto poll = linux_poll_segment(session, std::vector<int16_t>(32000));
        linux_require(linux_callback_count == 1 && !session.addon_candidate_attempted &&
            session.joint_existing_status == "RejectedByExistingGate" &&
            poll.find("\"success\":true") == std::string::npos, "rejected input retained a previous successful addon trace");
        linux_require(session.final_text == previous, "input rejection changed established last-final retention");
    });
    check("disabled participation and base retention", [&] {
        RealtimeSession session;
        session.realtime_engine = "joint_auto"; session.language = "en"; session.addon_engine_id = "qwen3_asr_ja";
        session.final_text = "Previous base"; session.has_transcript = true; session.selected_engine = "whisper";
        linux_poll_segment(session, audio);
        linux_require(linux_callback_count == 0 && session.final_text == "Previous base", "disabled addon changed base behavior");
    });
    check("async acceptance and rejection", [&] {
        RealtimeSession session;
        session.realtime_engine = "joint_auto"; session.language = session.addon_joint_language = "en";
        session.addon_engine_id = "qwen3_asr_ja"; session.addon_joint_requested = true;
        RealtimeWhisperValidationResult result{};
        result.generation = session.whisper_validation_generation; result.pcm_samples = audio;
        result.accepted = true; result.text = "Current whisper"; result.reason = "joint_low_pseudo_async_whisper";
        session.whisper_validation_results.push_back(result);
        publish_realtime_whisper_validation(&session);
        linux_require(linux_callback_count == 1 && session.final_text == result.text && session.selected_engine == "whisper",
            "accepted async base result changed");
        result.accepted = false; result.reason = "whisper_confidence_gate:test";
        session.whisper_validation_results.push_back(result);
        publish_realtime_whisper_validation(&session);
        linux_require(linux_callback_count == 1 && !session.addon_candidate_attempted && !session.has_transcript &&
            session.joint_existing_status == "RejectedByExistingGate", "async rejection was rescued");
    });
    check("capture loss discards queued async work, retains previous final", [&] {
        RealtimeSession session;
        session.capture_initialized = true; session.capture._running.store(true);
        session.final_text = "Previously displayed"; session.selected_engine = "whisper";
        session.decision_reason = "previous"; session.has_transcript = true;
        session.partial = "unconfirmed"; session.asr_pre_roll = audio;
        session.capture._pcm_buffer = audio;
        RealtimeWhisperValidationResult late{};
        late.generation = session.whisper_validation_generation; late.accepted = true; late.text = "late async";
        session.whisper_validation_results.push_back(late);
        const auto generation = session.whisper_validation_generation;
        session.capture._source_lost.store(true);
        session.state = RealtimeState::kRecording;
        const char* json = nullptr;
        linux_require(mmv2_realtime_poll(&session, &json) == 0 && json, "loss poll failed");
        std::string output(json); mmv2_free_string(json);
        linux_require(output.find("capture_device_lost") != std::string::npos && output.find("Previously displayed") != std::string::npos &&
            output.find("late async") == std::string::npos && session.selected_engine == "whisper" && session.decision_reason == "previous",
            "loss must retain prior final, not publish pending result");
        linux_require(session.whisper_validation_generation == generation + 1 && session.whisper_validation_results.empty() &&
            session.partial.empty() && session.asr_pre_roll.empty() && session.capture.get_samples().empty() && !session.capture.is_running(),
            "loss did not discard and stop pending capture");
        mmv2_realtime_stop(&session);
        linux_require(session.final_text == "Previously displayed", "Stop changed confirmed final");
    });
    check("capture loss during synchronous inference rolls back that poll", [&] {
        RealtimeSession session;
        session.capture_initialized = true; session.capture._running.store(true);
        session.realtime_engine = "joint_auto"; session.language = session.addon_joint_language = "en";
        session.addon_engine_id = "qwen3_asr_ja"; session.addon_joint_requested = true;
        session.final_text = "Previously displayed"; session.selected_engine = "whisper";
        session.decision_reason = "previous"; session.has_transcript = true;
        linux_callback_text = "interrupted result"; lose_during_callback = &session.capture;
        const auto output = linux_poll_segment(session, audio);
        lose_during_callback = nullptr;
        linux_require(linux_callback_count == 1 && output.find("capture_device_lost") != std::string::npos &&
            output.find("interrupted result") == std::string::npos && session.final_text == "Previously displayed" &&
            session.selected_engine == "whisper" && session.decision_reason == "previous", "in-flight result escaped loss boundary");
    });
    return failures ? 1 : 0;
}
#endif
