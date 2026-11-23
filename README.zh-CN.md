# MobMate WhispTalk — 源代码

[English](README.md) | [日本語](README.ja.md) | [简体中文](README.zh-CN.md)

MobMate WhispTalk 是一款帮助用户发言和听取对话的语音交流应用。
它可以识别麦克风语音，并将识别结果或输入的文本合成为语音。
源代码还包含翻译、听取传入音频和字幕功能。
功能能否使用取决于兼容的模型、runtime、设备和设置；此源代码候选尚未启动或进行设备验证。

[![在 Steam 上查看 MobMate WhispTalk](assets/steam-banner.svg)](https://store.steampowered.com/app/4261090/)

**官方 MobMate 二进制文件仅通过 [Steam](https://store.steampowered.com/app/4261090/) 分发。**
第三方使用此源代码构建的版本不是官方版本。欢迎为改进和实验创建 Fork。
非官方分发请使用能让用户与官方版本区分的名称和标识，并提供自己的联系方式。
这不会给代码许可证附加条件，也不会限制依照适用许可证进行的使用、修改和再分发。

## 从哪里开始阅读

- 可以从 [.NET Core](src/MobMate.Core/)、[Windows 主机](src/MobMate.App.WebHost/Program.cs)、[Linux 主机](src/MobMate.App.Linux/Program.cs)、[C++ bridge](native/mobmate_native_core/) 或 [Web UI](web/index.html) 开始。
- 此候选中的源代码、UI、preset 和测试可以阅读和编辑。构建需要外部依赖；语音验证及部分测试还需要模型、设备、fixture 或原始记录。请参阅 [BUILDING.md](BUILDING.zh-CN.md)。
- 欢迎改进文档和翻译、UI 无障碍及可复现的测试等；请参阅 [CONTRIBUTING.md](CONTRIBUTING.zh-CN.md)。

## 当前状态

本仓库包含源代码，不包含原始快照的 Git 历史、非公开分发 workflow、
Steamworks SDK/runtime、模型、原始分发中的图标和图像、音频或分发用二进制文件。
此源代码配置仅经过静态核对，尚未进行实际构建、应用启动、CI 或设备验证。
[BUILDING.md](BUILDING.zh-CN.md) 说明了 Windows/Linux 的步骤、外部依赖和缺失项。

模型列表等目录只是配置信息，不是模型本体。特别是，
`libs/preset/voiceconv_catalog.json` 中 `tsukuyomi-rvc` 的“bundled”标记和通知描述的是原始分发；
**本仓库不包含该模型**。对于尚未确认条件的外部材料，请勿未经授权再分发。

## 许可方针

对于主程序，在持有并有权许可的范围内采用 **GPL-3.0-only** 方针。
许可证正文见 [LICENSE](LICENSE)。如需不同于 GPL 的条件，仅在持有权利的范围内考虑单独许可。
单独许可不意味着包含第三方材料或未经确认的权利，也不表示许可主体已确定或合同已成立。

先前公开的 V1 是 misterWhisper 的 Java fork，V2 则是全新开发。
V1 的 [`e112427f88d2952fee9aef9f0fefc5b3cb6fb328`](https://github.com/zufall-upon/MobMate/tree/e112427f88d2952fee9aef9f0fefc5b3cb6fb328)
所用 MIT 许可证原文保存在 [licenses/MobMate-v1-e112427-MIT.txt](licenses/MobMate-v1-e112427-MIT.txt)。
V1 的 MIT 许可不会被撤销，也不会被 V2 的 GPL 方针替代。
仅有相似功能并不意味着 V2 从 V1 移植或复制。
V2 中第三方材料的条件及可单独许可的权利范围需要另行确认。

第三方通知和单独条件见 [THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt) 和
[licenses/README.md](licenses/README.zh-CN.md)。通知中也描述了原始分发包含但本仓库
未包含的材料。外部模型和依赖分别适用其自身条件。
[Valve 的说明](https://partner.steamgames.com/doc/sdk/uploading/distributing_opensource)提醒注意
Steamworks SDK 与 GPL 等许可证的组合。具体权利及分发方式需要另行确认。
拟合并到主程序的外部 PR 同意草案见 [CONTRIBUTING.md](CONTRIBUTING.zh-CN.md)。
