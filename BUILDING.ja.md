# ビルドのための静的調査

[English](BUILDING.md) | [日本語](BUILDING.ja.md) | [简体中文](BUILDING.zh-CN.md)

この文書は固定元を基に選別した候補の
プロジェクト設定とファイルを読んだ結果です。以下のコマンドは**将来の作業手順**であり、
この候補では Windows/Linux とも実ビルド、アプリ起動、CI、実機検証を行っていません。
外部資材のライセンス、対応版、入手性、ビルドや実行の成立は別途確認してください。
以下だけで公式製品相当の配布物が完成するとは言えません。

## この候補の内容と不足

- `src/` に .NET アプリ、`native/` に C++ bridge、`web/` に HTML/JS、
  `libs/preset/` に UI/カタログ設定、`tests/` に元のテストソースがあります。
  `src/MobMate.App.WebHost/MobMate.App.WebHost.csproj` は、出所未確認の `app.ico` を
  除外したため `ApplicationIcon` 指定のみ削除しています。
- Steamworks SDK/runtime (`libs_other/steam_api64.dll` 等)、モデルとモデル関連データ、
  バイナリ、アイコン、画像、`src/MobMate.Core/Tts/OnsetAssets/*.wav`、
  `web/audio/asr-accepted-a.wav` は含みません。合成音や確認音等は元の動作と一致しません。
  `tools/generate-asr-acceptance-cues.py` は元の生成器ソースですが、音声の利用・再配布権限を
  この文書は保証しません。
- `libs/preset/voiceconv_catalog.json` の `tsukuyomi-rvc` の「bundled」は元配布についての情報であり、
  この候補には含まれません。変換済みモデルの再配布可否や出力音声の条件は、
  [公式条件](https://tyc.rei-yumesaki.net/work/software/rvc/terms/)を確認してください。
  Moonshine はモデルごとに条件が異なり、英語以外の旧非ストリーミングモデルの一部は
  Community License の対象です。[固定 revision の上流条件](https://github.com/moonshine-ai/moonshine/blob/d8d8fa29d05ca67441a7719e70099f9f48917a93/LICENSE)
  と取得するモデル自体の条件を確認してください。
- ORT の MIT ヘッダー、sherpa-onnx の Apache-2.0 ヘッダー、miniaudio の MIT-0/Unlicense
  ヘッダーは各通知と共に含めています。実行用 DLL/SO と Windows 用 import lib は含めていません。
  `THIRD_PARTY_LICENSES.txt` は元のままのため、この候補に含まれない元配布物への言及も残ります。
- Windows の WebHost は WebView2 を参照します。Linux では画面用 Python helper のほか、
  実行する機能に応じ PipeWire/PulseAudio、pactl、ブラウザや対応モデルが必要です。
  Whisper、Moonshine、sherpa-onnx、ONNX Runtime、Piper 等の runtime とモデルの取得条件はそれぞれ異なります。
  Steam 関連の動作や entitlement は SDK/runtime がなく未検証です。

### Steamworks の静的境界

`SteamAchievementService` は Windows/Linux のアプリから実績 API を呼び、
`AppConfigLoader` と `VulkanEntitlementGate` は DLC の利用権を確認します。
Launcher の `SteamEntitlementProvider` は Supporter/Legacy の購読を確認します。
これらは実行時に `steam_api64.dll` または `libsteam_api.so` を読み込みます。
Core と Launcher の project には Steamworks の NuGet 参照はありませんが、
API 呼び出しは残っており、ライブラリがなければ関連機能は利用できません。
利用権の確認には、オンライン状態とキャッシュ等の条件もあります。
SDK 非同梱や動的ロードは GPL との両立、SDK の利用・再配布権限、
公式 Steam バイナリや Fork の再配布可能性を証明するものではありません。
それぞれの配布形態と適用条件を別々に確認してください。

## 共通の前提

- x64 用 .NET 8 SDK と NuGet へのアクセス。`MobMate.Core.csproj` は
  Microsoft.Extensions.Logging 8.0.0、Microsoft.ML.OnnxRuntime 1.31.1、
  Microsoft.ML.Tokenizers 2.0.0、org.k2fsa.sherpa.onnx 2.0.1 を参照します。
- CMake 3.25 以上と C++20 コンパイラ。`native/CMakeLists.txt` が要求し、
  `native/CMakePresets.json` は Windows に Visual Studio 17 2022、Linux に Ninja を指定します。
- 任意の voice-neutralizer 拡張 `native/voice_neutralizer_ext` は通常の native preset には含まれず、
  RNNoise、WORLD、WebRTC Audio Processing の別ソース・ライブラリが必要です。
  取得版・ライセンス・組立手順は未確定です。
- `Directory.Build.props` は開発用 Debug 設定で秘密値が空です。Launcher の Release は
  `MobMateSecretKey` が空ならビルドを拒否します。秘密値は共有・コミットしないでください。
  以下は Debug のみであり、Steam 配信や署名用の値は不要です。

## Windows x64 — Debug の組み立て手順案

Visual Studio 2022 の C++ x64 ツールチェーン、Windows SDK、.NET 8 SDK、
NuGet 接続と CMake 3.25 以上を用意し、PowerShell で候補のルートを開きます。
WebHost の target は `net8.0-windows10.0.17763.0`、WPF/Windows Forms を有効にし、
WebView2 1.0.3124.44 を参照します。

ネイティブ bridge の Windows 設定は `libs/sherpa-onnx/sherpa-onnx-c-api.lib` を固定参照します。
候補にはこの import lib がないため、利用条件を確認した上で取得・整合確認が必要です。
元の固定設定は sherpa-onnx 2.0.1 で、以下の upstream archive とハッシュを示しています。
作業先の `temp` に同名の既存ファイルがある場合は上書きせず、別の空ディレクトリを選んでください。

```powershell
dotnet build .\src\MobMate.App.WebHost\MobMate.App.WebHost.csproj -c Debug
dotnet build .\src\MobMate.Launcher\MobMate.Launcher.csproj -c Debug -r win-x64
dotnet build .\src\MobMate.Cli\MobMate.Cli.csproj -c Debug
```

`MobMateV2.sln` には Core、CLI、WebHost のみが登録され、Launcher と Linux アプリは含まれません。
それぞれ明示してビルドします。NuGet restore は `dotnet build` が行います。

Windows の Hearing は、設定した native executable の親ディレクトリのさらに親に
`MobMateLoopbackPcm.exe` を要求します。候補にはこの実行ファイルもそのソースも含まれず、
上の `dotnet build` でも生成されません。旧公開版には
[ソース](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/Program.cs)と
[project](https://github.com/zufall-upon/MobMate/blob/e112427f88d2952fee9aef9f0fefc5b3cb6fb328/src/whisper/MobMateLoopbackPcm.csproj)
がありますが、project は候補外の `NAudio`、`NAudio.Core`、`NAudio.Wasapi`、
`NAudio.WinMM` の DLL を参照します。別途の取得・構築、依存条件と配置の確認が必要です。
[NAudio の上流ライセンス](https://github.com/naudio/NAudio/blob/master/license.txt)は MIT ですが、
実際に取得する版の条件と依存関係を確認してください。旧ソースからの実ビルドや Hearing の動作は未検証です。

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

これらはコンパイル対象の手順です。実行・配布には対応 DLL、WebView2 Runtime、
モデルと web/preset 等を `AppContext.BaseDirectory` に合わせて別途配置する必要があります。
元の非公開 package workflow は含まず、そのままの完成配置は未確定です。

## Linux x86_64 — Debug の組み立て手順案

.NET 8 SDK、CMake 3.25 以上、Ninja、C++20 対応 GCC/Clang、pthread、dl、math を用意します。
`src/MobMate.App.Linux/MobMate.App.Linux.csproj` は `net8.0` と `web/`、必要な
`libs/preset/`、画面用 Python helper を参照します。Linux アプリは solution に登録されていないため、
別の project を指定します。

```sh
dotnet build src/MobMate.App.Linux/MobMate.App.Linux.csproj -c Debug
dotnet build src/MobMate.Launcher/MobMate.Launcher.csproj -c Debug -r linux-x64
dotnet build src/MobMate.Cli/MobMate.Cli.csproj -c Debug
```

ネイティブ bridge は sherpa のヘッダーと互換ライブラリを要します。
`scripts/linux/prepare-asr-runtime.sh` は `libs/asr-library-versions.json` と同じ固定 revision・SHA256 の
Whisper、Moonshine、sherpa と ONNX Runtime 1.31.1 を取得して構築する既存手順です。
Linux x86_64、CMake/C++ toolchain、curl、tar、unzip、既設 Vulkan SDK の headers・loader・`glslc`・
SPIRV-Headers CMake config、依存先へのネットワーク接続を要求し、CPU 専用の場合も Vulkan 入力を要求します。
取得・ビルド前に各依存とモデルのライセンスを確認してください。既存の runtime を上書きせず、
新規の絶対パスと既存 cache ディレクトリを指定します。

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

最初の candidate パスが既にある場合、スクリプトは停止します。依存の追加ダウンロードや
各プラットフォームの ABI 要件は upstream のビルドでも生じ得ます。
CMake は sherpa が見つからない場合に警告しますが、bridge のソースは API を参照するため、
警告だけでリンク・動作できるとは扱いません。実行時の Linux ホストは `libs/sherpa-onnx/` 内に
`libonnxruntime.so` と `libsherpa-onnx-c-api.so` を探し、native executable と bridge は同じ
ディレクトリを使います。runtime、モデル、web 等を含む完成配置は未検証です。

## テストと残課題

元の `tests/` は可能な限り保持していますが、テスト自体がビルド、アプリ起動、外部接続、
実モデル、デバイス、特定ホストパスを前提とする場合があります。勝手に一括実行しないでください。
例えば `settings_interaction_regression.js` は除外した確認音のハッシュを要求し、
`MobMate.LanguageSmoke/OnsetRouterSmoke.cs` は除外した音声を読みます。
`addon_asr_ui_regression.js` の fixture は通常 `dotnet run` を呼びます。
これらは欠落や権利・環境の確認後に対応する必要があり、テストを削除して PASS に見せていません。

次の7ファイルの実ホスト参照は引数または環境変数にしました。テストの条件・
期待値は変更しておらず、以下の前提を満たした実行は今回していません。

- `piper_language_regression.py` と `piper_session_isolation_regression.py` は
  `--dotnet` で .NET SDK 実行ファイルを選べます。省略時は `dotnet` を PATH から探します。
  後者は `--prepare-only` で生成した入力と binary を照合してから実行する元の手順です。
  `--router-before` は対応する変更前の Git `HEAD` を必要とし、固定元の変更前の履歴がないこの候補だけでは再現できません。
- `addon_language_http_smoke.py` は `--dotnet` と `--native-dir` を指定します。
  後者は既存の `mobmate-native` と `libmobmate_native_bridge.so` を含むディレクトリです。
  さらに候補外の Linux Debug build 出力を要し、起動・HTTP を伴います。
- `addon_linux_connection_regression.py` は `--dotnet-root`、通常モードでは `--node`、
  full モードでは `--native-build-dir` と既存の Core DLL/native build を要します。
  `--windows-final-segment-only` と `--standalone-failure-only` はさらに元の上限記録
  (`--budget-json`、`--pulse-json`、`--retained-tts-seconds`)、予約履歴ディレクトリ
  (`--windows-reservations`、後者は加えて `--standalone-reservations`)、
  元の `--capped-output` 補助スクリプトを要します。予約履歴はこの checkout の `temp/` 下に置き、
  `--run-dir` は選んだ予約ディレクトリ直下の新規パスとします。standalone の変更後側は
  `--native-archive` と `--sherpa-dir` も必要です。これらの過去の記録・helper・build は
  候補に同梱されません。予約値を新規に作って過去の上限消費をリセットしてはいけません。
  sandbox 側は .NET SDK 8.0.427 と 8.0.33 の参照 pack/runtime を前提とします。
- `realtime_gate_padding_regression.py` は既存 native build の CMake cache を参照します。
  cache 内の `/deps/sherpa` は sandbox の仮想パスです。それがホストから見えない場合、
  その build に対応する同じライブラリを `--sherpa-library` で指定します。
- `QwenRealSmoke.cs` は従来の `--qwen-real <output> <store>` に加え、
  `MOBMATE_QWEN_JA_WAV` と `MOBMATE_QWEN_EN_WAV` で既存の実音声を指定します。
  元の日本語「こんにちは」、英語「Open the map now.」、日本語の順と照合値はそのままです。
  音声、モデル、runtime は同梱されず、元音声と同一かはここでは検証できません。
  テストは store 内の runtime を移動・再展開し、検証用ファイルも変更するため、
  原本や現用 store は指定せず、権限のある隔離コピーだけを使ってください。
- `NemoServeBoundaryTests.cs` は Windows のテストで `MOBMATE_TEST_PYTHON` に
  既存の Python 実行ファイルを指定します。未指定・不存在は失敗であり、成功扱いしません。

`/scratch`、`/dotnet`、`/fixtures` は上記 sandbox 内の仮想パスで、開発ホストの固有パスでは
ありません。別の `mobfy_segment_boundary_regression.py` にも Windows の実ホスト参照が
あったため、`--cmake` と `--baseline-ref`、元の baseline source の `--baseline-sha256` を
明示する形にしました。固定元の非公開履歴は候補にないため、元の baseline を確認できない場合は
この比較を再現できません。別 baseline を使った結果を元の回帰結果として扱わないでください。
これらの入力変更後の実行・build・各 OS での成立は未検証です。
外部 fixture の取得・同梱や、失敗を PASS に見せる変更はしていません。

今回の静的な回帰確認では `whisper_turbo_ui_regression.js` は元候補・この候補とも exit 0 でした。
`settings_interaction_regression.js` は元候補で exit 0、この候補では除外した
`web/audio/asr-accepted-a.wav` の `ENOENT` により exit 1 で、後続部分は未確認です。
実装・アプリ・実音声の動作証明ではありません。
`addon_asr_ui_regression.js` は元候補の通常モードで内部から `dotnet run` を試みましたが、
`dotnet` が見つからず exit 1 となり、実ビルド・起動前に停止しました。その際、元候補の
ignored な `temp/asr-candidates/` に fixture と失敗ログが生成されました。
以後はビルドを呼ばない `MOBMATE_START_PROFILE_STATIC_ONLY=1` を使い、元候補とこの候補の
両方で `TypeError: Cannot read properties of undefined (reading 'First')`、exit 1 を確認しました。
その後、この候補の fixture を更新して集合・文字列操作と入力検査を限定的に評価できるようにし、
同じ静的モードで後続の UI 回帰まで exit 0 を確認しました。失敗時の cleanup を検査する
ケースでは、意図した `stop failed` のログも出ます。これは模擬入力による JavaScript 側の
限定確認であり、C# のコンパイル、実 HTTP、音声、モデル、Windows/Linux 実行の証明ではありません。

ビルド・起動を伴わずに同じ確認を試すには Node.js が必要です。確認時の版は
Node.js v22.23.4 です。候補のルートで次を実行できます。別の限定モードが先に
選ばれて開始 profile の確認が飛ばされないよう、その設定はこのコマンド内だけで解除します。
通常モードは `dotnet run` を呼ぶため、上記の制約下では使用しません。

```sh
MOBMATE_FINAL_SEGMENT_ONLY= MOBMATE_ADDON_PLATFORM_ONLY= MOBMATE_STATE_UI_ONLY= MOBMATE_POLL_ONLY= MOBMATE_START_PROFILE_STATIC_ONLY=1 node tests/MobMate.Core.Tests/addon_asr_ui_regression.js
```

Windows の PowerShell では、親シェルの環境変数を変更しない例として次を使えます。

```powershell
cmd /c "set MOBMATE_FINAL_SEGMENT_ONLY=&& set MOBMATE_ADDON_PLATFORM_ONLY=&& set MOBMATE_STATE_UI_ONLY=&& set MOBMATE_POLL_ONLY=&& set MOBMATE_START_PROFILE_STATIC_ONLY=1&& node tests/MobMate.Core.Tests/addon_asr_ui_regression.js"
```

開発時に外部資材を配置するときの誤追加を減らすため、`.gitignore` に主なモデル・runtime・
音声ディレクトリを記載しています。ただし網羅的な安全装置ではありません。
追加・コミット前に差分と権利を自分で確認してください。

公開前に、本体で実際に GPL 公開・個別許諾できる権利の範囲、Steamworks のソース・公式 Steam 版・
Fork の各条件、各外部資材の取得・再配布条件とコード中の機微情報を確認してください。
CLA は草案であり、受領主体の確定は外部 PR の同意取得・マージを始める前に必要です。
両 OS の実ビルド・起動は未検証のままです。
公開・push・Steam 配布や visibility の変更はこの調査に含まれません。
