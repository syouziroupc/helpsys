# HelpSys

HelpSys は Windows の操作を代行せず、現在の画面とPC状態を確認して「人間が次に何を操作すべきか」を1手ずつ案内する作業支援システムです。

## 公開サイト / ダウンロード

公開サイト:

```text
https://helpsys.syouziroupc.workers.dev/
```

通常版の安定ダウンロードURL:

```text
https://helpsys.syouziroupc.workers.dev/download
```

Education版の安定ダウンロードURL:

```text
https://helpsys.syouziroupc.workers.dev/download/education
```

公開サイトはバージョン番号付きZIPへ直接リンクしません。`/download` と `/download/education` から固定名のrelease aliasへ302リダイレクトするため、Reliability v7以降へ版を上げてもHTMLのリンクが古くならない構成です。

## 最重要原則

HelpSys の目的は監視範囲を狭くすることではなく、**現在の画面を十分確認したうえで、目的達成までの正しい次の1手を提示すること**です。

- 人間がクリック・キー入力を行う。HelpSys は入力を注入しない。
- 利用者の依頼がない待機中はAI案内を開始しない。
- 案内の各ステップで、現在のスクリーンショットを主要証拠として確認する。
- UI Automation（UIA）、前面アプリ、URL、実行中アプリ、履歴などはスクリーンショットを補強する構造化証拠として同時に使う。
- UIA候補が0件でも画面確認を省略しない。
- スクリーンショットを安全に取得できない場合、UIAだけで推測して案内を進めない。
- AIは次の1手だけを提案する。
- AI応答後にも前面アプリ・ブラウザ状態・対象UIを再確認する。
- 画面が変わった場合、古い計画・青枠・音声を新しい画面へ持ち越さない。
- 画像とUIA/system contextが矛盾する場合は推測で進まず、再確認または利用者確認へ戻る。
- 分からない状態を「たぶん成功した」と扱わない。

## Reliability v7 — Quality First + Commander

Reliability v7 は、通常版HelpSysの案内判断を画面確認中心へ組み直し、音声起動アシスタント **Commander（コマンダー）** を追加します。Educationの追加機能開発ではなく、通常版の品質・起動方法・配布信頼性を優先した更新です。

### Quality First

通常経路は `/v1/quality-guide` を使用します。各ステップで次を同じ判断へ投入します。

- current screenshot
- UI Automation候補
- foreground / running apps / browser URL等のsystem context
- 直前までの案内history

判断規則:

- `done` は画面上の可視証拠と confidence 0.90以上を必須とする。
- `target` は画面確認と confidence 0.80以上を必須とする。
- バックグラウンドで対象アプリが起動しているだけでは `done` にしない。
- custom-rendered UIは、画面上で十分に確認でき confidence 0.92以上の場合に限りvisual-only targetを許可する。
- screenshot / UIA / system contextが矛盾した場合は `not_found` または `clarify` とし、推測で続行しない。
- planning timeoutは品質優先のため70秒まで許容する。

旧案内ループは比較用に残していますが、通常経路からは外しています。

### Commander

ウェイクフレーズ:

```text
ねえコマンダー
```

動作:

1. Windowsローカルの `System.Speech` でウェイクフレーズだけを待機する。
2. 「ねえコマンダー」を検出したらwake recognizerを完全に解放する。
3. 「はい。どうしましたか？」と応答する。
4. 用件を1回だけ音声認識する。
5. 認識した用件を通常のQuality First案内へ渡す。
6. 処理後にwake待機へ戻る。

`MicrophoneCoordinator` がマイク利用を調停します。手動音声入力、確認回答、Commander本認識など前面の音声処理をwake待機より優先し、別処理がマイクを使う間はCommanderのwake recognizerを停止・解放します。マイクが利用できない場合は約4秒ごとに静かに再試行します。

CommanderはUIからON/OFFできます。日本語recognizerやマイクが存在しないPCでも、通常のHelpSys本体は起動を継続します。

### 配布導線の固定

通常版releaseは追跡用の

```text
HelpSys-Reliability-v7-win-x64.zip
```

と、公開サイト用の固定名

```text
HelpSys-latest-win-x64.zip
```

を同時に公開します。

固定導線:

```text
/download            -> HelpSys-latest-win-x64.zip
/download/education  -> HelpSys-Education-latest-win-x64.zip
```

CIはHTML・redirect・release workflow・API既定値を横断検査し、名前やURLがずれた場合に失敗します。

### 前面アプリ監視と計画の鮮度

前面PIDへの監視切替要求に世代番号を持たせ、古い非同期処理が後から戻ってきても監視対象を以前のアプリへ巻き戻しません。アプリ起動直後にUIA Windowがまだ公開されていない場合は、次のheartbeatで購読を再試行します。

案内セッションは `GuidanceSessionController` が一元管理し、各計画にgeneration番号を付けます。

```text
Idle → Capturing → Planning → Presenting → AwaitingUserAction → Verifying
                                                        ├→ Capturing
                                                        ├→ Clarifying
                                                        └→ Done
任意の状態 → Stopped / Idle
```

画面変更、中止、新しい計画でgenerationが更新された後に古いHTTP応答が戻ってきても表示・発話しません。

### 操作結果の確認

クリックや文字入力を検出しただけでは成功にしません。

- チェックボックス・ラジオボタン: `TogglePattern` / 選択状態
- リスト・タブ・ツリー: 選択・展開状態
- ブラウザ移動: URL変化
- アプリ切替: 前面プロセス変化
- ウィンドウ操作: ウィンドウ集合・前面状態の変化
- 一般操作: 安定したUI状態変化を複数回確認

`type_text` は入力欄の値が変わっただけでは完了扱いにしません。Enter等の仕上げ操作後に期待する状態遷移が確認できて初めて成功とします。

### Worker側の最終ガード

AIの出力だけに意味判定を任せません。

- バックグラウンドでアプリが起動しているだけでは「アプリを開く」クエストを完了扱いにしない。
- パスワード、PIN、OTP、認証コード、リカバリキー、CVV等をHelpSysへ入力するよう求めるclarificationは拒否する。
- 一時的な `408 / 429 / 500 / 502 / 503 / 504` と一時通信例外のみ1回再試行する。
- 画面変更によるキャンセルを通信障害として表示しない。
- Educationのテスト段階ではAI推論を呼ばない。

## 構成

```text
Windows HelpSys (.NET/WPF)
  ├─ CommanderWakeService
  ├─ MicrophoneCoordinator
  ├─ SpeechInputService / SpeechOutputService
  ├─ SystemContextService
  ├─ UiAutomationScanner
  ├─ screenshot capture / privacy checks
  ├─ GuidanceSessionController
  ├─ GuidanceStateWatcher
  ├─ CloudGuideService
  ├─ target再照合
  ├─ 操作結果検証
  └─ click-through overlay
          │
          ▼
Cloudflare Worker + Static Assets
  ├─ /
  ├─ /download
  ├─ /download/education
  ├─ /health
  ├─ /v1/quality-guide
  ├─ /v1/guide
  ├─ /v1/vision-guide
  └─ /v1/education/assist
```

## Cloudflare

`wrangler.jsonc` には Workers AI binding `AI` と `./site` static assets が設定されています。

```powershell
npm install
npm run check
npm test
npm run dev
npm run deploy
```

必要なら `HELPSYS_API_KEY` / `HELPSYS_EDUCATION_API_KEY` を設定できます。公開Workerを本番運用する場合、アプリ内の共有キーだけを濫用対策とみなさず、Cloudflare AccessやRate Limiting等のエッジ側制御を別途設定してください。

## Windows

必要環境:

- Windows 10/11 64-bit
- .NET 10 SDK（ソースからビルドする場合）

```powershell
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
$env:HELPSYS_EDUCATION_API_BASE="https://example.workers.dev"
```

## CI

Windows smoke では以下を確認します。

- Worker JavaScript の構文とself-test
- `/v1/quality-guide` の契約
- screen-confirmedなしの `done` 拒否
- background appだけの `done` 拒否
- screenshot / UIA一致時のtarget許可
- CommanderButtonのUI Automation公開
- PNG screenshot付きのQuality First実リクエスト
- Windows Release build
- 実Windows UIで青枠案内まで表示
- 公開HTML / `_redirects` / release asset / API既定値の整合性

Education smokeは既存Education版の回帰確認として継続しますが、通常版が安定するまではEducationの追加機能開発を優先しません。

## 残る運用課題

- 長時間実機利用でCommanderの誤起動率、マイク再取得失敗率、誤案内率、再計画率を計測する。
- 実アプリ別の操作成功判定を増やす。
- 成功した操作系列のキャッシュで応答速度とAPI使用量を改善する。
- 公開APIの濫用対策はCloudflare Access / Rate Limiting等をアカウント側で設定する。リポジトリだけでは完結しない。
