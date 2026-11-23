#pragma once

#include <vector>
#include <array>
#include <cstdint>
#include <string>

static constexpr int SPEAKER_FEAT_DIM       = 48;
static constexpr int SPEAKER_MAX_CENTROIDS  = 20;
static constexpr int SPEAKER_MIN_SAMPLES    = 10;

struct SpeakerSample {
    std::array<float, SPEAKER_FEAT_DIM> feat{};
    float rms_energy = 0.0f;
};

struct SpeakerProfile {
    std::vector<SpeakerSample> samples;
    int total_enrolled = 0;
    std::array<float, SPEAKER_FEAT_DIM> ema_centroid{};
    float ema_spread = 1.0f;
    bool ema_ready = false;
    int consecutive_reject = 0;
};

// 特徴量抽出（失敗時は空ベクトル）
std::vector<float> speaker_extract_features(const int16_t* pcm, int n, int sr);

// エンロール（false = 無音/短すぎ）
bool speaker_enroll(SpeakerProfile& p, const int16_t* pcm, int n, int sr);

// マッチング（10サンプル未満は常に true を返す）
float speaker_match(SpeakerProfile& p, const int16_t* pcm, int n, int sr, bool* pass);

// 永続化
bool speaker_save(const SpeakerProfile& p, const std::string& path);
bool speaker_load(SpeakerProfile& p, const std::string& path);

// リセット（samples + EMA + カウンタ全クリア）
void speaker_reset(SpeakerProfile& p);
