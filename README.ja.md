# MobMate WhispTalk — ソースコード

[English](README.md) | [日本語](README.ja.md) | [简体中文](README.zh-CN.md)

**公式の MobMate バイナリは [Steam](https://store.steampowered.com/app/4261090/) でのみ配布しています。**
このソースから第三者が作ったビルドは公式版ではありません。改良・実験のための Fork を歓迎します。
非公式配布では、利用者が公式版と区別できる名前・表示と、独自の問い合わせ先を用意してください。
これはコードのライセンスに追加条件を課すものではなく、適用ライセンスに従った利用・改変・再配布を制限するものではありません。

## 現状

このリポジトリにはソースコードを収録しています。固定元の Git 履歴、非公開配信用 workflow、
Steamworks SDK/runtime、モデル、アイコン、画像、音声、配布用バイナリは含まれません。
このソース構成は静的な照合に限り、実ビルド・アプリ起動・CI・実機検証はしていません。
[BUILDING.md](BUILDING.ja.md) に Windows/Linux の手順、外部依存と不足を記載しています。

モデル一覧などのカタログは設定情報であり、モデル本体ではありません。特に
`libs/preset/voiceconv_catalog.json` の `tsukuyomi-rvc` の「bundled」という表示と通知は元配布の情報であり、
**このリポジトリには当該モデルは同梱されません**。条件を確認していない外部資材を無断で再配布しないでください。

## ライセンス方針

本体について、権利を持ち許諾できる範囲を **GPL-3.0-only** とする方針です。
本文は [LICENSE](LICENSE) にあります。GPL と異なる条件が必要な場合は、権利を持つ範囲に限って個別許諾を検討する方針です。
個別許諾が第三者資材や未確認の権利を含むこと、許諾主体、契約成立を意味するものではありません。

旧公開版 V1 は misterWhisper の Java fork、V2 は新規開発です。
V1 の [`e112427f88d2952fee9aef9f0fefc5b3cb6fb328`](https://github.com/zufall-upon/MobMate/tree/e112427f88d2952fee9aef9f0fefc5b3cb6fb328)
の MIT ライセンス原文は [licenses/MobMate-v1-e112427-MIT.txt](licenses/MobMate-v1-e112427-MIT.txt) に保持しています。
V1 の MIT 許諾は取り消されず、V2 の GPL 方針によって置き換わるものではありません。
似た処理があることだけで、V1 から V2 への移植やコピーを意味するものではありません。
V2 に含まれる第三者資材の条件や、個別に許諾できる権利の範囲は別途確認が必要です。

第三者の通知と個別条件は [THIRD_PARTY_LICENSES.txt](THIRD_PARTY_LICENSES.txt) と
[licenses/README.md](licenses/README.ja.md) を参照してください。通知には元配布に含まれたが
このリポジトリには含まれない資材の記述もあります。外部のモデルや依存物にはそれぞれの条件が適用されます。
[Valve の案内](https://partner.steamgames.com/doc/sdk/uploading/distributing_opensource)は
Steamworks SDK と GPL 等の組み合わせについて注意を求めています。具体的な権利と配布方法は別途確認が必要です。
本体に取り込む外部 PR の同意草案は [CONTRIBUTING.md](CONTRIBUTING.ja.md) にあります。
