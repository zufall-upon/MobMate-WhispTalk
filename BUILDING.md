# Static Investigation for Building

[English](BUILDING.md) | [日本語](BUILDING.ja.md) | [简体中文](BUILDING.zh-CN.md)

This document records a reading of the project settings and files in the candidate selected from the source snapshot.
The commands below are **proposed procedures for future work**. Neither Windows nor Linux has been actually built,
the application launched, CI run, or device testing performed with this candidate.
Separately check the licenses, compatible versions and availability of external materials, and whether building and running succeed.
These steps alone cannot be said to produce a distribution equivalent to the official product.

## Contents and missing items in this candidate

- `src/` contains the .NET application, `native/` the C++ bridge, `web/` HTML/JS,
  `libs/preset/` UI/catalog configuration, and `tests/` the original test sources.
  Only the `ApplicationIcon` setting was removed from `src/MobMate.App.WebHost/MobMate.App.WebHost.csproj`
  because `app.ico`, whose provenance was unconfirmed, was excluded.
- Steamworks SDK/runtime (such as `libs_other/steam_api64.dll`), models and model-related data,
  binaries, icons, images, `src/MobMate.Core/Tts/OnsetAssets/*.wav`, and
  `web/audio/asr-accepted-a.wav` are not included. Synthesized sounds, confirmation sounds, and similar behavior will not match the original.
  `tools/generate-asr-acceptance-cues.py` is the original generator source, but this document does not guarantee
  rights to use or redistribute the audio.
- “bundled” for `tsukuyomi-rvc` in `libs/preset/voiceconv_catalog.json` describes the original distribution;
  the model is not included in this candidate. Check the [official terms](https://tyc.rei-yumesaki.net/work/software/rvc/terms/)
  for redistribution of converted models and conditions on output audio.
  Moonshine terms differ by model, and some older non-streaming models for languages other than English
  are subject to the Community License. Check the [upstream terms at the pinned revision](https://github.com/moonshine-ai/moonshine/blob/d8d8fa29d05ca67441a7719e70099f9f48917a93/LICENSE)
  and the terms of the actual model obtained.
- The ORT MIT headers, sherpa-onnx Apache-2.0 header, and miniaudio MIT-0/Unlicense
  header are included with their notices. Runtime DLLs/SOs and the Windows import library are not included.
  `THIRD_PARTY_LICENSES.txt` remains unchanged and therefore also refers to original distribution items not included in this candidate.
- Windows WebHost references WebView2. On Linux, in addition to the Python helper for the screen,
  PipeWire/PulseAudio, pactl, a browser, and compatible models are needed depending on the functionality used.
  Acquisition terms differ for runtimes and models such as Whisper, Moonshine, sherpa-onnx, ONNX Runtime, and Piper.
  Steam-related functionality and entitlement are unverified without the SDK/runtime.

### Steamworks static boundary

`SteamAchievementService` calls the achievements API from the Windows/Linux applications,
and `AppConfigLoader` and `VulkanEntitlementGate` check DLC entitlements.
The Launcher's `SteamEntitlementProvider` checks Supporter/Legacy subscriptions.
These load `steam_api64.dll` or `libsteam_api.so` at runtime.
The Core and Launcher projects do not have a Steamworks NuGet reference, but API calls remain,
and the related features are unavailable without the library.
Entitlement checks also depend on conditions such as online status and caching.
Omitting the SDK or dynamically loading it does not prove GPL compatibility, rights to use or redistribute the SDK,
or whether official Steam binaries or forks can be redistributed.
Check each distribution form and its applicable terms separately.

## Common prerequisites

- The x64 .NET 8 SDK and access to NuGet. `MobMate.Core.csproj` references
  Microsoft.Extensions.Logging 8.0.0, Microsoft.ML.OnnxRuntime 1.31.1,
  Microsoft.ML.Tokenizers 2.0.0, and org.k2fsa.sherpa.onnx 2.0.1.
- CMake 3.25 or later and a C++20 compiler, as required by `native/CMakeLists.txt`.
  `native/CMakePresets.json` specifies Visual Studio 17 2022 for Windows and Ninja for Linux.
- The optional voice-neutralizer extension `native/voice_neutralizer_ext` is not included in the normal native preset
  and requires separate RNNoise, WORLD, and WebRTC Audio Processing sources/libraries.
  Versions to obtain, licenses, and assembly steps are undecided.
- `Directory.Build.props` has empty secret values in the development Debug configuration. Launcher Release
  refuses to build if `MobMateSecretKey` is empty. Do not share or commit secret values.
  The following is for Debug only; Steam distribution and signing values are not needed.

## Windows x64 — proposed Debug assembly steps

Prepare the Visual Studio 2022 C++ x64 toolchain, Windows SDK, .NET 8 SDK,
NuGet access, and CMake 3.25 or later, and open the candidate root in PowerShell.
WebHost targets `net8.0-windows10.0.17763.0`, enables WPF/Windows Forms,
and references WebView2 1.0.3124.44.

The Windows native bridge configuration has a fixed reference to `libs/sherpa-onnx/sherpa-onnx-c-api.lib`.
This import library is absent from the candidate; obtain it and check compatibility after confirming its terms of use.
The original pinned configuration is sherpa-onnx 2.0.1 and identifies the upstream archive and hashes below.
If files with these names already exist in the work area's `temp`, do not overwrite them; choose another empty directory.

```powershell
dotnet build .\src\MobMate.App.WebHost\MobMate.App.WebHost.csproj -c Debug
dotnet build .\src\MobMate.Launcher\MobMate.Launcher.csproj -c Debug -r win-x64
dotnet build .\src\MobMate.Cli\MobMate.Cli.csproj -c Debug
```

`MobMateV2.sln` registers only Core, CLI, and WebHost, not Launcher or the Linux application.
Build the latter by explicitly specifying each project. `dotnet build` performs NuGet restore.

Windows Hearing requires `MobMateLoopbackPcm.exe` in the parent of the configured native executable's parent directory.
Neither this executable nor its source is included in the candidate, and the `dotnet build` commands above do not generate it.
The old public version has [source](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/Program.cs) and a
[project](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/MobMateLoopbackPcm.csproj),
but that project references `NAudio`, `NAudio.Core`, `NAudio.Wasapi`, and `NAudio.WinMM` DLLs outside this candidate.
Separate acquisition/building and confirmation of dependency terms and placement are required.
[NAudio's upstream license](https://github.com/naudio/NAudio/blob/master/license.txt) is MIT, but check the terms
and dependencies of the version actually obtained. Building the old source and Hearing operation have not been verified.

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

The path in the first comment is an example; set it to the installed CMake executable.
These are compilation steps. For execution and distribution, compatible DLLs, WebView2 Runtime,
models, web/preset, and so on must separately be placed according to `AppContext.BaseDirectory`.
The original private package workflow is not included, and the complete layout as-is is undecided.

## Linux x86_64 — proposed Debug assembly steps

Prepare the .NET 8 SDK, CMake 3.25 or later, Ninja, C++20-compatible GCC/Clang, pthread, dl, and math.
`src/MobMate.App.Linux/MobMate.App.Linux.csproj` references `net8.0`, `web/`, the required
`libs/preset/`, and the Python helper for the screen. The Linux application is not registered in the solution,
so specify its project separately.

```sh
dotnet build src/MobMate.App.Linux/MobMate.App.Linux.csproj -c Debug
dotnet build src/MobMate.Launcher/MobMate.Launcher.csproj -c Debug -r linux-x64
dotnet build src/MobMate.Cli/MobMate.Cli.csproj -c Debug
```

The native bridge requires sherpa headers and a compatible library.
`scripts/linux/prepare-asr-runtime.sh` is an existing procedure that obtains and builds Whisper,
Moonshine, sherpa, and ONNX Runtime 1.31.1 at the pinned revisions and SHA256 values in `libs/asr-library-versions.json`.
It requires Linux x86_64, a CMake/C++ toolchain, curl, tar, unzip, installed Vulkan SDK headers, loader and `glslc`,
SPIRV-Headers CMake config, and network access to dependencies; Vulkan inputs are required even for CPU-only use.
Check the licenses of each dependency and model before downloading or building. Do not overwrite existing runtimes;
specify a new absolute path and an existing cache directory.

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

The values in the first comment are examples; replace them with existing absolute paths for your installed tools and SDK.
The script stops if the first candidate path already exists. Further dependency downloads and platform ABI requirements
may also arise in upstream builds.
CMake warns if sherpa is not found, but the bridge source references the API; a warning alone does not establish
that linking or execution will work. At runtime, the Linux host looks for `libonnxruntime.so` and
`libsherpa-onnx-c-api.so` in `libs/sherpa-onnx/`, and the native executable and bridge use the same directory.
The complete layout including runtime, models, web, and so on is unverified.

## Tests and remaining work

The original `tests/` are retained as far as possible, but tests may require builds, application launches, external connections,
real models, devices, or specific host paths. Do not run them all indiscriminately.
For example, `settings_interaction_regression.js` requires the hash of an excluded confirmation sound,
and `MobMate.LanguageSmoke/OnsetRouterSmoke.cs` reads excluded audio.
The fixture for `addon_asr_ui_regression.js` normally calls `dotnet run`.
These need attention after checking missing items, rights, and the environment; tests have not been removed to make them appear to PASS.

Real host references in the following seven files were changed to arguments or environment variables. Test conditions
and expected results were not changed, and no run meeting the following prerequisites has been performed this time.

- `piper_language_regression.py` and `piper_session_isolation_regression.py` can select the .NET SDK executable
  with `--dotnet`; when omitted, they look for `dotnet` on PATH.
  The latter follows the original procedure of comparing input generated with `--prepare-only` and the binary before running.
  `--router-before` requires the corresponding pre-change Git `HEAD` and cannot be reproduced with this candidate alone, which lacks the source snapshot's pre-change history.
- `addon_language_http_smoke.py` takes `--dotnet` and `--native-dir`.
  The latter is an existing directory containing `mobmate-native` and `libmobmate_native_bridge.so`.
  It additionally requires Linux Debug build output outside this candidate and involves launching and HTTP.
- `addon_linux_connection_regression.py` requires `--dotnet-root`, `--node` in normal mode,
  and `--native-build-dir` plus existing Core DLL/native build in full mode.
  `--windows-final-segment-only` and `--standalone-failure-only` additionally require the original limit records
  (`--budget-json`, `--pulse-json`, `--retained-tts-seconds`), reservation history directories
  (`--windows-reservations`, plus `--standalone-reservations` for the latter),
  and the original `--capped-output` helper script. Place reservation histories under this checkout's `temp/`,
  and set `--run-dir` to a new path directly under the selected reservation directory. The post-change standalone side
  also needs `--native-archive` and `--sherpa-dir`. These historical records, helper, and builds are not included
  in the candidate. Do not create new reservation values to reset past limit consumption.
  The sandbox assumes .NET SDK 8.0.427 and the 8.0.33 reference pack/runtime.
- `realtime_gate_padding_regression.py` refers to the CMake cache of an existing native build.
  `/deps/sherpa` in the cache is a virtual sandbox path. If it is not visible from the host,
  specify the same library corresponding to that build with `--sherpa-library`.
- `QwenRealSmoke.cs` accepts existing real audio through `MOBMATE_QWEN_JA_WAV` and `MOBMATE_QWEN_EN_WAV`
  in addition to the existing `--qwen-real <output> <store>`. The original Japanese “こんにちは”, English “Open the map now.”,
  Japanese order and comparison values are unchanged. Audio, models, and runtime are not included, and identity
  with the original audio cannot be verified here. Because the test moves and re-extracts the runtime in the store
  and modifies verification files, use only an authorized isolated copy, not the original or an active store.
- `NemoServeBoundaryTests.cs` is a Windows test that takes an existing Python executable in `MOBMATE_TEST_PYTHON`.
  An unspecified or nonexistent executable is a failure, not a success.

`/scratch`, `/dotnet`, and `/fixtures` are virtual paths in the sandbox above, not paths specific to the development host.
The separate `mobfy_segment_boundary_regression.py` also had a real Windows host reference, so it was changed
to explicitly specify `--cmake`, `--baseline-ref`, and `--baseline-sha256` for the original baseline source.
The source snapshot's private history is not in the candidate, so the comparison cannot be reproduced if the original
baseline cannot be confirmed. Do not treat results using a different baseline as the original regression result.
Execution, builds, and viability on each OS after these input changes are unverified.
External fixtures were not acquired or included, and no changes were made to make failures appear to PASS.

In this static regression check, `whisper_turbo_ui_regression.js` exited 0 on both the original candidate and this candidate.
`settings_interaction_regression.js` exited 0 on the original candidate but exited 1 on this candidate because the excluded
`web/audio/asr-accepted-a.wav` caused `ENOENT`; subsequent portions are unverified.
This does not prove implementation, application, or real-audio behavior.
`addon_asr_ui_regression.js` in normal mode on the original candidate tried to invoke `dotnet run` internally,
but `dotnet` was not found and it exited 1 before an actual build or launch. At that time, fixtures and failure logs
were generated in the original candidate's ignored `temp/asr-candidates/`.
Subsequently, with `MOBMATE_START_PROFILE_STATIC_ONLY=1` to avoid calling a build, both the original and this candidate
produced `TypeError: Cannot read properties of undefined (reading 'First')` and exited 1.
The fixture in this candidate was then updated to permit limited evaluation of set/string operations and input validation,
and the same static mode exited 0 through subsequent UI regressions. The case checking cleanup on failure
also emits the intended `stop failed` log. This is a limited JavaScript-side check using simulated input,
not proof of C# compilation, real HTTP, audio, models, or Windows/Linux execution.

Node.js is needed to try the same check without building or launching the application. The version used was
Node.js v22.23.4. From the candidate root, the following can be run. Other limited-mode settings are cleared
only within this command so they cannot take precedence and skip the start-profile check.
Normal mode calls `dotnet run` and is not used under the constraints above.

```sh
MOBMATE_FINAL_SEGMENT_ONLY= MOBMATE_ADDON_PLATFORM_ONLY= MOBMATE_STATE_UI_ONLY= MOBMATE_POLL_ONLY= MOBMATE_START_PROFILE_STATIC_ONLY=1 node tests/MobMate.Core.Tests/addon_asr_ui_regression.js
```

In Windows PowerShell, the following is an example that does not change the parent shell's environment variables.

```powershell
cmd /c "set MOBMATE_FINAL_SEGMENT_ONLY=&& set MOBMATE_ADDON_PLATFORM_ONLY=&& set MOBMATE_STATE_UI_ONLY=&& set MOBMATE_POLL_ONLY=&& set MOBMATE_START_PROFILE_STATIC_ONLY=1&& node tests/MobMate.Core.Tests/addon_asr_ui_regression.js"
```

To reduce accidental additions when placing external materials during development, `.gitignore` lists major model, runtime,
and audio directories. It is not an exhaustive safety mechanism. Check changes and rights yourself before adding or committing.

Before publication, check the scope of rights actually available for GPL publication and separate licensing of the main application;
the conditions for Steamworks source, official Steam releases, and forks; each external material's acquisition and redistribution terms;
and sensitive information in the code. The CLA is a draft, and the recipient must be determined before starting to obtain
consent for or merge external PRs. Actual builds and launches on both OSes remain unverified.
Publication, push, Steam distribution, and changes to visibility are outside this investigation.
