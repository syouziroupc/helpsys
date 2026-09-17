# HelpSys Stable 3.0.0 — Gemini Edition

HelpSys は Windows の操作を代行せず、現在の画面を確認して「利用者が次に行う1操作」を案内する支援アプリです。

## 公開URL

- Web: https://helpsys.syouziroupc.workers.dev/
- 最新版: https://helpsys.syouziroupc.workers.dev/download
- Education: https://helpsys.syouziroupc.workers.dev/download/education

## Stable 3.0.0

通常版はゼロから組み直した `src/HelpSys.Stable` を配布します。旧 `src/HelpSys.Desktop` は履歴参照用で、Stable 3.0.0 のビルド・実行には参照しません。

- AI: Gemini 3.8 Flash 固定
- F8: 現在画面を取得して次の1操作を案内
- F9 / 音声入力: Windows音声認識で目的を1回だけ入力
- UI Automation + スクリーンショットを同一観測として送信
- Gemini応答後、構造化ターゲットをローカルで再検証
- 画面取得中に前面ウィンドウが実際に変わった場合のみ1回再取得
- 常時foreground監視、案内自動ループ、GLMフォールバックなし
- パスワード・認証画面・セキュリティ警告はスクリーンショット取得前にローカル停止
- 青枠オーバーレイはPer-Monitor DPIに対応
- 読み上げ対応
- SHA-256検証付き手動更新

旧Commanderの常時待受はStable 3.0では使用しません。マイクの常時占有とrecognizer競合を避けるため、音声目的一覧はF9で明示的に開始します。音声入力機能自体は維持しています。

## Worker

本番 `helpsys` Workerは `worker/stable-router.js` を入口にします。

- `POST /v1/plan` → Gemini 3.8 Flash
- `POST /v1/education/assist` → Gemini 3.8 Flash
- `GET /health` → モデル/バージョン/Gemini設定状態

`GEMINI_API_KEY` が未設定の場合は503で明示的に失敗し、旧モデルへは切り替えません。

## Build

```powershell
dotnet restore src/HelpSys.Stable/HelpSys.Stable.csproj
dotnet build src/HelpSys.Stable/HelpSys.Stable.csproj -c Release
```

Releaseは `preview-latest` に以下を公開します。

- `HelpSys-Stable-3.0.0-<commit>-win-x64.zip`
- `HelpSys-latest-win-x64.zip`
- `HelpSys-latest-win-x64.sha256`
- `HelpSys-Unified-<commit>-win-x64.zip`（旧v8 UpdaterからStable 3.0へ移行するための互換パッケージ）
