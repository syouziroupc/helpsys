# HelpSys

HelpSys は Windows の操作を代行せず、現在の画面とPC状態を確認して「人間が次に何を操作すべきか」を1手ずつ案内する作業支援システムです。

## 公開サイト / ダウンロード

```text
https://helpsys.syouziroupc.workers.dev/
https://helpsys.syouziroupc.workers.dev/download
https://helpsys.syouziroupc.workers.dev/download/education
```

通常版の `/download` は固定名 `HelpSys-latest-win-x64.zip` へ接続します。Reliabilityの版番号を上げても公開サイトのURLは変えません。

## Reliability v8 — GLM + Commander Stability

v8は通常版HelpSysの安定性と案内精度を優先した更新です。Educationの追加機能開発ではありません。

### GLMへ移行

Cloudflare Workers AIのモデルを次へ変更しています。

```text
通常テキスト経路: @cf/zai-org/glm-4.7-flash
画面確認経路:     @cf/zai-org/glm-5.3-flash
```

`/v1/quality-guide` は現在のスクリーンショットを主要証拠として、UI Automation、前面アプリ、URL、履歴などを補助証拠として同時に評価します。

体感速度を悪化させないため、通常の画面判断では `reasoning_effort=low`、UI候補最大280件、履歴最大8件、出力最大520 tokensとしています。Windows側のAPI呼び出しは1試行9秒で打ち切り、一時的な通信障害だけ1回再試行します。

### 実画面で確認した誤案内の修正

「youtubeが見たい」と依頼した際、デスクトップのGoogle Chromeショートカットへ「左ボタンを1回」と案内する不具合がありました。デスクトップショートカットはExplorerの `ListItem` なので、1回押すだけでは選択されるだけです。

v8ではモデル判断に加えてvalidatorでも物理操作を補正します。

- Explorer上のデスクトップショートカットを起動する場合 → `double_click`
- タスクバーのアプリボタン → `left_click`
- 実際のアプリ名が分かる場合 → 「インターネットを見るアプリ」のような曖昧な呼び方を避ける
- サイト移動では古いdeterministic手順を絶対条件にせず、現在画面を優先する

### Commanderのフリーズ対策

ウェイクフレーズは次です。

```text
ねえコマンダー
```

v7ではrecognizerの停止・破棄を状態lock内で行う経路があり、音声認識callbackとの待ち合わせで固まる余地がありました。v8ではrecognizerをlock内で破棄しません。

新しい流れ:

1. wake phraseを完全一致かつconfidence 0.72以上で検出。
2. wake recognizerを状態から切り離す。
3. cancel / disposeは別Taskで実行。
4. マイクが実際に解放されたことを確認してから「はい。どうしましたか？」と応答。
5. 固定2.3秒待機は使わず、応答音声の実終了後に用件認識を開始。
6. 用件認識終了後もrecognizerの完全解放を待ってからwake待機へ戻る。

手動音声入力・確認回答・Commander本認識はwake待機より常に優先します。Commander自身の読み上げ文にはwake phraseを含めず、自己再起動を防ぎます。

### タイムアウトと古い応答の破棄

- 音声用件認識: 8秒
- Commander 1回の対話全体: 16秒
- API: 1試行9秒、通信系のみ最大1回再試行
- 各APIリクエストにrequest idを付与
- 画面変更時は既存generation/context検査で古いAI応答を破棄

長時間固まって待つより、短時間で失敗として戻し、再試行できる構成を優先します。

## Quality Firstの基本規則

- `done` は画面上の可視証拠 + confidence 0.90以上が必要。
- `target` は画面確認 + confidence 0.80以上が必要。
- バックグラウンドでアプリが起動しているだけでは `done` にしない。
- screenshot / UIA / system contextが矛盾した場合は推測で進まない。
- custom-rendered UIは十分な画面確度がある場合だけvisual-only targetを許可する。
- パスワード、PIN、OTP、認証コード、リカバリキー、CVV等をHelpSysへ入力するよう求めない。
- 広告・スポンサー結果、紛らわしいドメイン、ブラウザのセキュリティ警告を安易に進めない。

## 構成

```text
Windows HelpSys (.NET/WPF)
  ├─ CommanderWakeService
  ├─ MicrophoneCoordinator
  ├─ SpeechInputService / SpeechOutputService
  ├─ ScreenCaptureService
  ├─ UiAutomationScanner
  ├─ GuidanceSessionController / GuidanceStateWatcher
  ├─ CloudGuideService
  └─ overlay / operation verification
          │
          ▼
Cloudflare Worker + Workers AI
  ├─ @cf/zai-org/glm-4.7-flash
  ├─ @cf/zai-org/glm-5.3-flash
  ├─ /v1/quality-guide
  ├─ /v1/guide
  ├─ /v1/vision-guide
  └─ /v1/education/assist
```

## 開発

```powershell
npm install
npm run check
npm test

dotnet build HelpSys.sln
dotnet run --project src/HelpSys.Desktop/HelpSys.Desktop.csproj
```

既定API:

```text
https://helpsys.syouziroupc.workers.dev
```

別URLを使う場合:

```powershell
$env:HELPSYS_API_BASE="https://example.workers.dev"
```

## CI

Windows smokeでは、Worker self-test、GLM設定契約、Chromeデスクトップショートカットのdouble-click回帰テスト、Commanderのlock内dispose再発防止、Windows Release build、実WPF UI起動、Quality Firstリクエストと青枠表示まで検査します。

通常版が安定するまではEducationの追加開発を優先しません。
