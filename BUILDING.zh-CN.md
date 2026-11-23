# 构建方面的静态调查

[English](BUILDING.md) | [日本語](BUILDING.ja.md) | [简体中文](BUILDING.zh-CN.md)

本文记录了对基于固定源筛选出的候选项目配置和文件的阅读结果。
以下命令是**今后的拟议操作步骤**；此候选在 Windows/Linux 上均未进行实际构建、
应用启动、CI 或设备验证。请另行确认外部材料的许可证、兼容版本、可获取性以及能否构建和运行。
仅凭以下内容不能断言可以完成与官方产品等同的分发包。

## 如何使用本文

- 一般开发请先查看候选内容和共同前提，再阅读 Windows 或 Linux 的 Debug 拟议步骤。外部依赖仍需另行确认。
- 测试与待解决事项还记录了依赖外部 fixture、原始环境或历史预约记录的检查。这些不是编辑或构建 Fork 的一般要求，仅在复现相应检查时参考。

## 此候选的内容与缺失项

- `src/` 包含 .NET 应用，`native/` 包含 C++ bridge，`web/` 包含 HTML/JS，
  `libs/preset/` 包含 UI/目录配置，`tests/` 包含原始测试源代码。
  由于排除了来源未经确认的 `app.ico`，只从 `src/MobMate.App.WebHost/MobMate.App.WebHost.csproj`
  删除了 `ApplicationIcon` 设置。
- 不包含 Steamworks SDK/runtime（如 `libs_other/steam_api64.dll`）、模型及模型相关数据、
  二进制文件、原始分发中的图标和图像、`src/MobMate.Core/Tts/OnsetAssets/*.wav` 和
  `web/audio/asr-accepted-a.wav`。合成音、确认音等行为与原版不一致。
  `tools/generate-asr-acceptance-cues.py` 是原有生成器的源代码，但本文不保证
  音频的使用和再分发权利。
- `libs/preset/voiceconv_catalog.json` 中 `tsukuyomi-rvc` 的“bundled”描述原始分发，
  本候选不包含该模型。转换后模型能否再分发以及输出音频的条件，请查看
  [官方条件](https://tyc.rei-yumesaki.net/work/software/rvc/terms/)。
  Moonshine 的条件因模型而异，部分英语以外的旧版非流式模型受 Community License 约束。
  请确认[固定 revision 的上游条件](https://github.com/moonshine-ai/moonshine/blob/d8d8fa29d05ca67441a7719e70099f9f48917a93/LICENSE)
  及实际获取的模型自身的条件。
- ORT 的 MIT 头文件、sherpa-onnx 的 Apache-2.0 头文件、miniaudio 的 MIT-0/Unlicense
  头文件连同各自通知一并包含。运行用 DLL/SO 和 Windows import lib 不包含在内。
  `THIRD_PARTY_LICENSES.txt` 保持原样，因此仍提及原始分发中有但此候选未包含的材料。
- Windows WebHost 引用 WebView2。Linux 除了画面用 Python helper 外，
  根据所运行功能还需要 PipeWire/PulseAudio、pactl、浏览器和兼容模型。
  Whisper、Moonshine、sherpa-onnx、ONNX Runtime、Piper 等 runtime 与模型的获取条件各不相同。
  由于没有 SDK/runtime，Steam 相关功能和 entitlement 尚未验证。

### Steamworks 静态边界

`SteamAchievementService` 从 Windows/Linux 应用调用成就 API，
`AppConfigLoader` 和 `VulkanEntitlementGate` 检查 DLC 使用权。
Launcher 的 `SteamEntitlementProvider` 检查 Supporter/Legacy 订阅。
这些组件在运行时加载 `steam_api64.dll` 或 `libsteam_api.so`。
Core 和 Launcher 的 project 没有 Steamworks NuGet 引用，但 API 调用仍然存在；
缺少该库时无法使用相关功能。使用权检查还受在线状态、缓存等条件影响。
不附带 SDK 或动态加载并不能证明与 GPL 兼容、拥有 SDK 使用或再分发权限，
也不能证明官方 Steam 二进制文件或 Fork 可以再分发。
请分别确认各分发形式及适用条件。

## 共同前提

- x64 .NET 8 SDK 和 NuGet 访问权限。`MobMate.Core.csproj` 引用
  Microsoft.Extensions.Logging 8.0.0、Microsoft.ML.OnnxRuntime 1.31.1、
  Microsoft.ML.Tokenizers 2.0.0、org.k2fsa.sherpa.onnx 2.0.1。
- CMake 3.25 或更高版本及 C++20 编译器，由 `native/CMakeLists.txt` 要求。
  `native/CMakePresets.json` 为 Windows 指定 Visual Studio 17 2022，为 Linux 指定 Ninja。
- 可选 voice-neutralizer 扩展 `native/voice_neutralizer_ext` 不包含在通常的 native preset 中，
  需要另外的 RNNoise、WORLD、WebRTC Audio Processing 源代码或库。
  获取版本、许可证和组装步骤尚未确定。
- `Directory.Build.props` 的开发用 Debug 配置中秘密值为空。Launcher Release 在
  `MobMateSecretKey` 为空时拒绝构建。请勿共享或提交秘密值。
  以下仅针对 Debug，不需要 Steam 分发或签名用值。

## Windows x64 — Debug 拟议组装步骤

准备 Visual Studio 2022 C++ x64 工具链、Windows SDK、.NET 8 SDK、
NuGet 连接及 CMake 3.25 或更高版本，在 PowerShell 中打开候选根目录。
WebHost 的目标为 `net8.0-windows10.0.17763.0`，启用 WPF/Windows Forms，
并引用 WebView2 1.0.3124.44。

Windows native bridge 配置固定引用 `libs/sherpa-onnx/sherpa-onnx-c-api.lib`。
候选中没有此 import lib，因此须在确认使用条件后获取并检查兼容性。
原始固定配置使用 sherpa-onnx 2.0.1，并列出以下上游归档和哈希。
如果工作位置的 `temp` 已有同名文件，请勿覆盖，另选空目录。

```powershell
dotnet build .\src\MobMate.App.WebHost\MobMate.App.WebHost.csproj -c Debug
dotnet build .\src\MobMate.Launcher\MobMate.Launcher.csproj -c Debug -r win-x64
dotnet build .\src\MobMate.Cli\MobMate.Cli.csproj -c Debug
```

`MobMateV2.sln` 只注册了 Core、CLI 和 WebHost，不包含 Launcher 和 Linux 应用。
需要分别显式指定项目来构建。`dotnet build` 会执行 NuGet restore。

Windows Hearing 要求在配置的 native executable 的父目录的上一级放置 `MobMateLoopbackPcm.exe`。
此候选既不包含该可执行文件也不包含其源代码，上述 `dotnet build` 也不会生成它。
旧公开版有[源代码](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/Program.cs)和
[project](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/MobMateLoopbackPcm.csproj)，
但该 project 引用了候选外的 `NAudio`、`NAudio.Core`、`NAudio.Wasapi`、
`NAudio.WinMM` DLL。需要另外获取、构建，并确认依赖条件及放置位置。
[NAudio 上游许可证](https://github.com/naudio/NAudio/blob/master/license.txt)为 MIT，
但请确认实际获取版本的条件及依赖。从旧源代码实际构建以及 Hearing 运行均未验证。

```powershell
# 以下の場所は例。既設 CMake の実体に合わせて設定。
$Cmake = 'C:\Program Files\CMake\bin\cmake.exe'
$archive = '.\temp\sherpa-onnx-v2.0.1-win-x64-shared-MD-Release-lib.tar.bz2'
$unpack = '.\temp\sherpa-onnx-v2.0.1-unpack'
if ((Test-Path $archive) -or (Test-Path $unpack)) { throw 'Choose unused paths' }
New-Item -ItemType Directory -Force .\temp | Out-Null
New-Item -ItemType Directory $unpack | Out-Null
Invoke-WebRequest 'https://github.com/k2-fsa/sherpa-onnx/releases/download/v2.0.1/sherpa-onnx-v2.0.1-win-x64-shared-MD-Release-lib.tar.bz2' -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'c3c78220db8bebf97d5f932496165bb3897b163410427d7c021013439c80f183') { throw 'Archive hash mismatch' }
tar -xjf $archive -C $unpack
if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed' }
$lib = @(Get-ChildItem $unpack -File -Recurse -Filter 'sherpa-onnx-c-api.lib')
if ($lib.Count -ne 1 -or (Get-FileHash $lib[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'edf4b780d0ab4f0320d210f9cfc7e96e01d9b0dbee7234746f6cb6b422065997') { throw 'Import library missing or hash mismatch' }
if (Test-Path .\libs\sherpa-onnx\sherpa-onnx-c-api.lib) { throw 'Import library already exists' }
Copy-Item $lib[0].FullName .\libs\sherpa-onnx\sherpa-onnx-c-api.lib
Push-Location .\native
& $Cmake --preset windows-debug
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }
& $Cmake --build --preset windows-debug
if ($LASTEXITCODE -ne 0) { throw 'CMake build failed' }
Pop-Location
```

第一行注释中的路径只是示例，请设置为已安装 CMake 的实际可执行文件。
这些是编译步骤。要运行或分发，还需按照 `AppContext.BaseDirectory` 另行放置兼容 DLL、
WebView2 Runtime、模型、web/preset 等。原有非公开 package workflow 不包含在内，
原样完成的目录布局尚未确定。

## Linux x86_64 — Debug 拟议组装步骤

准备 .NET 8 SDK、CMake 3.25 或更高版本、Ninja、支持 C++20 的 GCC/Clang、pthread、dl、math。
`src/MobMate.App.Linux/MobMate.App.Linux.csproj` 引用 `net8.0`、`web/`、
必要的 `libs/preset/` 和画面用 Python helper。Linux 应用未注册到 solution 中，
因此需要另行指定 project。

```sh
dotnet build src/MobMate.App.Linux/MobMate.App.Linux.csproj -c Debug
dotnet build src/MobMate.Launcher/MobMate.Launcher.csproj -c Debug -r linux-x64
dotnet build src/MobMate.Cli/MobMate.Cli.csproj -c Debug
```

native bridge 需要 sherpa 头文件及兼容库。
`scripts/linux/prepare-asr-runtime.sh` 是现有步骤，用于获取并构建与
`libs/asr-library-versions.json` 中固定 revision 和 SHA256 对应的 Whisper、Moonshine、sherpa 和 ONNX Runtime 1.31.1。
它要求 Linux x86_64、CMake/C++ toolchain、curl、tar、unzip、已安装 Vulkan SDK 的 headers、loader、`glslc`、
SPIRV-Headers CMake config 及到依赖源的网络连接；即使仅使用 CPU 也要求 Vulkan 输入。
获取和构建前请确认各依赖及模型的许可证。请勿覆盖现有 runtime，
应指定新的绝对路径和已有 cache 目录。

```sh
# 下記は例。各値を自分の既設ツール・SDK の実在する絶対パスに置き換える。
export MOBMATE_CMAKE=/usr/bin/cmake
export MOBMATE_VULKAN_INCLUDE=/absolute/path/to/vulkan/include
export MOBMATE_VULKAN_LIBRARY=/absolute/path/to/libvulkan.so
export MOBMATE_VULKAN_GLSLC=/absolute/path/to/glslc
export MOBMATE_SPIRV_HEADERS=/absolute/path/to/SPIRV-Headers/cmake
mkdir -p "$HOME/.cache/mobmate-oss-downloads"
bash scripts/linux/prepare-asr-runtime.sh "$HOME/mobmate-oss-asr-candidate" "$HOME/.cache/mobmate-oss-downloads"
cd native
"$MOBMATE_CMAKE" --preset linux-debug -DSHERPA_ONNX_LIB="$HOME/mobmate-oss-asr-candidate/runtime/shared/libsherpa-onnx-c-api.so"
"$MOBMATE_CMAKE" --build --preset linux-debug
cd ..
```

第一行注释中的值只是示例，请替换为本机已安装工具和 SDK 的实际绝对路径。
如果第一个 candidate 路径已存在，脚本会停止。上游构建也可能需要额外下载依赖，
或存在各平台 ABI 要求。
找不到 sherpa 时 CMake 会警告，但 bridge 源代码引用该 API，因此不能仅凭警告就认为能够链接或运行。
运行时 Linux 主机会在 `libs/sherpa-onnx/` 中查找 `libonnxruntime.so` 和
`libsherpa-onnx-c-api.so`，native executable 和 bridge 使用同一目录。
包含 runtime、模型、web 等的完整布局尚未验证。

## 测试与待解决事项

原有 `tests/` 已尽可能保留，但测试本身可能依赖构建、应用启动、外部连接、
真实模型、设备或特定主机路径。请勿擅自全部运行。
例如 `settings_interaction_regression.js` 要求已排除确认音的哈希，
`MobMate.LanguageSmoke/OnsetRouterSmoke.cs` 会读取已排除的音频。
`addon_asr_ui_regression.js` 的 fixture 通常调用 `dotnet run`。
需要在确认缺失项、权利和环境后处理；没有通过删除测试来制造 PASS 假象。

### 依赖原始环境或外部 fixture 的检查

以下七个文件中对实际主机的引用已改为参数或环境变量。测试条件和期望值未变，
此次也没有在满足以下前提的情况下执行。

- `piper_language_regression.py` 和 `piper_session_isolation_regression.py` 可通过
  `--dotnet` 选择 .NET SDK 可执行文件，省略时从 PATH 查找 `dotnet`。
  后者遵循原有步骤，先比较用 `--prepare-only` 生成的输入与 binary，然后执行。
  `--router-before` 需要对应变更前的 Git `HEAD`；此候选没有固定源的变更前历史，仅凭它无法复现。
- `addon_language_http_smoke.py` 指定 `--dotnet` 和 `--native-dir`。
  后者是包含现有 `mobmate-native` 和 `libmobmate_native_bridge.so` 的目录。
  此外还需要候选外的 Linux Debug build 输出，并涉及启动和 HTTP。
- `addon_linux_connection_regression.py` 需要 `--dotnet-root`，普通模式需要 `--node`，
  full 模式需要 `--native-build-dir` 及现有 Core DLL/native build。
  `--windows-final-segment-only` 和 `--standalone-failure-only` 还需要原有上限记录
  （`--budget-json`、`--pulse-json`、`--retained-tts-seconds`）、预约历史目录
  （`--windows-reservations`，后者还需要 `--standalone-reservations`），
  以及原有 `--capped-output` 辅助脚本。预约历史放在此 checkout 的 `temp/` 下，
  `--run-dir` 应为所选预约目录直属的新路径。standalone 的变更后侧还需要
  `--native-archive` 和 `--sherpa-dir`。这些历史记录、helper 和 build 不包含在候选中。
  不得新建预约值来重置过去的上限消耗。
  sandbox 假定 .NET SDK 8.0.427 和 8.0.33 reference pack/runtime。
- `realtime_gate_padding_regression.py` 引用现有 native build 的 CMake cache。
  cache 中的 `/deps/sherpa` 是 sandbox 虚拟路径。如果主机无法看到该路径，
  请通过 `--sherpa-library` 指定与该 build 对应的同一库。
- `QwenRealSmoke.cs` 除原有 `--qwen-real <output> <store>` 外，
  还通过 `MOBMATE_QWEN_JA_WAV` 和 `MOBMATE_QWEN_EN_WAV` 指定现有真实音频。
  原有日语“こんにちは”、英语“Open the map now.”、日语的顺序及比较值不变。
  音频、模型和 runtime 均未包含，此处无法验证是否与原音频相同。
  由于测试会移动、重新展开 store 中的 runtime 并修改验证文件，
  请勿指定原件或现用 store，仅使用有权限的隔离副本。
- `NemoServeBoundaryTests.cs` 是 Windows 测试，通过 `MOBMATE_TEST_PYTHON` 指定
  现有 Python 可执行文件。未指定或文件不存在均为失败，不视为成功。

`/scratch`、`/dotnet`、`/fixtures` 是上述 sandbox 中的虚拟路径，并非开发主机的专有路径。
另一个 `mobfy_segment_boundary_regression.py` 也曾包含 Windows 实际主机引用，
因此改为显式指定 `--cmake`、`--baseline-ref` 及原始 baseline source 的 `--baseline-sha256`。
固定源的非公开历史不在候选中，若无法确认原 baseline 则无法复现此比较。
请勿把使用其他 baseline 的结果当作原始回归结果。
这些输入变更后的执行、build 及各 OS 上的可行性均未验证。
没有获取或附带外部 fixture，也没有进行使失败看似 PASS 的修改。

### 先前观察到的静态检查

本次静态回归检查中，`whisper_turbo_ui_regression.js` 在原候选和此候选上均为 exit 0。
`settings_interaction_regression.js` 在原候选为 exit 0，在此候选因排除的
`web/audio/asr-accepted-a.wav` 导致 `ENOENT` 而 exit 1，后续部分尚未确认。
这不能证明实现、应用或真实音频的行为。
`addon_asr_ui_regression.js` 在原候选普通模式下从内部尝试调用 `dotnet run`，
但因找不到 `dotnet` 而 exit 1，在实际构建和启动前停止。当时在原候选被忽略的
`temp/asr-candidates/` 中生成了 fixture 和失败日志。
之后使用不调用构建的 `MOBMATE_START_PROFILE_STATIC_ONLY=1`，在原候选和此候选上
均确认 `TypeError: Cannot read properties of undefined (reading 'First')`、exit 1。
随后更新了此候选的 fixture，使其能够有限地评估集合/字符串操作和输入检查，
并在相同静态模式下确认后续 UI 回归至 exit 0。检查失败时 cleanup 的案例
也会输出预期的 `stop failed` 日志。这仅是使用模拟输入的 JavaScript 侧有限检查，
不能证明 C# 编译、真实 HTTP、音频、模型或 Windows/Linux 运行。

要在不构建和启动应用的情况下尝试相同检查，需要 Node.js。检查时使用
Node.js v22.23.4。可在候选根目录执行以下命令。为了避免其他限定模式优先
导致跳过 start profile 检查，仅在此命令内清除这些设置。
普通模式调用 `dotnet run`，在上述限制下不使用。

```sh
MOBMATE_FINAL_SEGMENT_ONLY= MOBMATE_ADDON_PLATFORM_ONLY= MOBMATE_STATE_UI_ONLY= MOBMATE_POLL_ONLY= MOBMATE_START_PROFILE_STATIC_ONLY=1 node tests/MobMate.Core.Tests/addon_asr_ui_regression.js
```

在 Windows PowerShell 中，可使用以下示例而不改变父 shell 的环境变量。

```powershell
cmd /c "set MOBMATE_FINAL_SEGMENT_ONLY=&& set MOBMATE_ADDON_PLATFORM_ONLY=&& set MOBMATE_STATE_UI_ONLY=&& set MOBMATE_POLL_ONLY=&& set MOBMATE_START_PROFILE_STATIC_ONLY=1&& node tests/MobMate.Core.Tests/addon_asr_ui_regression.js"
```

为减少开发时放置外部材料导致误添加，`.gitignore` 列出了主要模型、runtime、
音频目录，但它并非穷尽性的安全措施。添加和提交前请自行检查差异和权利。

公开前请确认主程序实际可按 GPL 公开及单独许可的权利范围、Steamworks 源代码、官方 Steam 版及
Fork 的各项条件、各外部材料的获取及再分发条件，以及代码中的敏感信息。
CLA 是草案，开始收集外部 PR 的同意或进行合并之前必须确定接收主体。
两个 OS 的实际构建和启动仍未验证。
公开、push、Steam 分发或 visibility 变更不在本次调查范围内。
