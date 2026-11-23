// 実capture sourceの選択/初期化境界。miniaudio呼出しだけを置換し、deviceは開かない。
#include "../../native/vendor/miniaudio.h"
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <vector>

static std::vector<ma_device_info> fixture_devices;
static ma_result fixture_enumeration = MA_SUCCESS, fixture_init = MA_SUCCESS, fixture_start = MA_SUCCESS;
static int enumeration_calls, init_calls, start_calls, stop_calls;
static bool default_selected;
static std::string selected_id;
static ma_device_notification_proc fixture_notification;
static ma_device_data_proc fixture_data;
static ma_device* fixture_device;
static bool stop_during_start;
static void notify(ma_device_notification_type type) {
    ma_device_notification event{}; event.pDevice = fixture_device; event.type = type;
    fixture_notification(&event);
}
static ma_result fixture_context_init(const ma_backend*, ma_uint32, const ma_context_config*, ma_context*) { return MA_SUCCESS; }
static ma_result fixture_context_uninit(ma_context*) { return MA_SUCCESS; }
static ma_result fixture_get_devices(ma_context*, ma_device_info**, ma_uint32*, ma_device_info** devices, ma_uint32* count) {
    ++enumeration_calls;
    *devices = fixture_devices.data(); *count = static_cast<ma_uint32>(fixture_devices.size());
    return fixture_enumeration;
}
static ma_device_config fixture_config(ma_device_type type) { ma_device_config config{}; config.deviceType = type; return config; }
static ma_result fixture_device_init(ma_context*, const ma_device_config* config, ma_device* device) {
    ++init_calls;
    default_selected = config->capture.pDeviceID == nullptr;
    selected_id = default_selected ? "" : config->capture.pDeviceID->pulse;
    device->pUserData = config->pUserData;
    device->capture.format = ma_format_s32; device->capture.channels = 1;
    fixture_device = device; fixture_notification = config->notificationCallback; fixture_data = config->dataCallback;
    return fixture_init;
}
static void fixture_device_uninit(ma_device*) { }
static ma_result fixture_device_start(ma_device*) { ++start_calls; if (stop_during_start) notify(ma_device_notification_type_stopped); return fixture_start; }
static ma_result fixture_device_stop(ma_device*) { ++stop_calls; notify(ma_device_notification_type_stopped); return MA_SUCCESS; }

// miniaudio宣言の実型を使い、実装guardだけ閉じる。製品へ注入APIは追加しない。
#define miniaudio_c
#define ma_context_init fixture_context_init
#define ma_context_uninit fixture_context_uninit
#define ma_context_get_devices fixture_get_devices
#define ma_device_config_init fixture_config
#define ma_device_init fixture_device_init
#define ma_device_uninit fixture_device_uninit
#define ma_device_start fixture_device_start
#define ma_device_stop fixture_device_stop
#include "../../native/mobmate_native_core/src/audio_capture_linux.cpp"

static void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
static void reset(std::initializer_list<const char*> names) {
    fixture_devices.clear();
    int id = 0;
    for (const char* name : names) {
        ma_device_info info{};
        std::strcpy(info.name, name);
        std::strcpy(info.id.pulse, ("fixture-" + std::to_string(++id)).c_str());
        fixture_devices.push_back(info);
    }
    fixture_enumeration = fixture_init = fixture_start = MA_SUCCESS;
    enumeration_calls = init_calls = start_calls = stop_calls = 0;
    default_selected = false; selected_id.clear();
    fixture_notification = nullptr; fixture_data = nullptr; fixture_device = nullptr; stop_during_start = false;
}
int main() {
    int failed = 0;
    auto check = [&](const char* name, auto test) {
        try { test(); std::cout << "PASS " << name << '\n'; }
        catch (const std::exception& error) { ++failed; std::cerr << "FAIL " << name << ": " << error.what() << '\n'; }
    };
    check("空指定の正規default opt-inを維持", [] {
        reset({}); mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize(error) == 0 && enumeration_calls == 0 && init_calls == 1 && default_selected, "default初期化を変えた");
        require(capture.start(error) == 0 && start_calls == 1 && capture.is_running(), "defaultの明示Startが失敗");
        capture.stop(); require(!capture.is_running() && stop_calls == 1, "Stopが失敗");
    });
    check("一意な明示名は正確なIDだけを開く", [] {
        reset({"Other", "Fixture"}); mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize("Fixture", error) == 0 && enumeration_calls == 1 && init_calls == 1 && !default_selected && selected_id == "fixture-2", "明示IDが一致しない");
        require(capture.start(error) == 0 && start_calls == 1, "有効な明示Startが失敗");
    });
    for (const auto* scenario : {"missing", "enumeration", "ambiguous"}) {
        check(scenario, [=] {
            reset(std::string(scenario) == "ambiguous" ? std::initializer_list<const char*>{"Fixture", "Fixture"} : std::initializer_list<const char*>{"Other"});
            if (std::string(scenario) == "enumeration") fixture_enumeration = MA_ERROR;
            mobmate::MiniAudioCapture capture; std::string error;
            require(capture.initialize("Fixture", error) != 0 && !error.empty() && init_calls == 0, "不正明示入力からdevice初期化へ進んだ");
            require(capture.start(error) != 0 && start_calls == 0 && !capture.is_running(), "不正明示入力で録音を開始した");
        });
    }
    check("列挙後のdevice消滅は別ID/defaultへ再試行しない", [] {
        reset({"Fixture"}); fixture_init = MA_ERROR;
        mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize("Fixture", error) != 0 && init_calls == 1 && !default_selected && selected_id == "fixture-1", "device初期化失敗をfallbackした");
        require(capture.start(error) != 0 && start_calls == 0 && !capture.is_running(), "初期化失敗後に録音した");
    });
    check("初期化後のStart失敗は自動再接続しない", [] {
        reset({"Fixture"}); fixture_start = MA_ERROR;
        mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize("Fixture", error) == 0 && capture.start(error) != 0, "Start失敗が伝わらない");
        require(init_calls == 1 && start_calls == 1 && !capture.is_running() && !default_selected, "Start失敗をdefault/再接続へ置換した");
    });
    check("source loss needs notification and a missing named source", [] {
        reset({"Fixture"}); mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize("Fixture", error) == 0 && capture.start(error) == 0, "start failed");
        int32_t silence[4]{}; fixture_data(fixture_device, nullptr, silence, 4);
        require(capture.get_samples().size() == 4 && !capture.source_lost(), "silence must not be treated as loss");
        fixture_devices.clear();
        require(!capture.source_lost(), "enumeration alone is not a stop notification");
        fixture_devices.push_back(ma_device_info{}); std::strcpy(fixture_devices[0].name, "Fixture");
        notify(ma_device_notification_type_stopped);
        require(!capture.source_lost(), "suspended but present source is not a disconnect");
        fixture_enumeration = MA_ERROR; fixture_devices.clear();
        require(!capture.source_lost(), "enumeration failure is not proof of loss");
        fixture_enumeration = MA_SUCCESS;
        require(capture.source_lost(), "missing selected source after stop not detected");
        const auto count = capture.get_samples().size();
        notify(ma_device_notification_type_started);
        fixture_data(fixture_device, nullptr, silence, 4);
        require(capture.source_lost() && capture.get_samples().size() == count, "loss must latch and stop accumulating PCM");
        capture.stop();
        require(stop_calls == 1 && !capture.is_running(), "control-side stop did not release device");
    });
    check("start-time notification and explicit Stop", [] {
        reset({"Fixture"}); stop_during_start = true; mobmate::MiniAudioCapture capture; std::string error;
        require(capture.initialize("Fixture", error) == 0 && capture.start(error) == 0, "start failed");
        fixture_devices.clear();
        require(capture.source_lost(), "notification during start was missed");
        capture.stop();
        reset({"Fixture"}); mobmate::MiniAudioCapture normal;
        require(normal.initialize("Fixture", error) == 0 && normal.start(error) == 0, "restart failed");
        normal.stop(); fixture_devices.clear();
        require(!normal.source_lost(), "explicit stop was reported as source loss");
    });
    return failed ? 1 : 0;
}
