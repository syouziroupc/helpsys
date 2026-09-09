# HelpSys

HelpSys は Windows の操作を代行せず、現在のPC状態を確認して「人間が次に何を操作すべきか」を1手ずつ案内する作業支援システムです。

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

公開サイトはバージョン番号付きZIPへ直接リンクしません。`/download` と `/download/education` から固定名のrelease aliasへ302リダイレクトするため、Reliability v7等へ版を上げてもHTMLのリンクが古くならない構成です。

## 最重要原則

HelpSys の目的は監視範囲を狭くすることではなく、**現在の状態から目的達成までの正しい次の1手を提示すること**です。

- 人間がクリック・キー入力を行う。HelpSys は入力を注入しない。
- 利用者の依頼がない待機中はAI案内を開始しない。
- UI Automation（UIA）を第一情報源にする。
- 必要な状態は広く観測してよいが、案内対象は現在の作業文脈と再照合する。
- AIは次の1手だけを提案する。
- AI応答後にも前面アプリ・ブラウザ状態・対象UIを再確認する。
- 画面が変わった場合、古い計画・青枠・音声を新しい画面へ持ち越さない。
- UIAだけで安全に対象を決められない場合のみVisionを使う。
- 分からない状態を「たぶん成功した」と扱わない。

## Reliability v6

Reliability v6 は v5 の案内・監視信頼性を維持したうえで、**公開配布・Education接続・配布契約の再発防止**を追加しています。

### 配布導線の固定

旧サイトには、releaseに存在しない `HelpSys-win-x64.zip` へのリンクが残り、ダウンロードが404になる問題がありました。v6ではサイトHTMLからバージョン付きZIP名を排除し、次の固定導線へ変更しています。

```text
/download            -> HelpSys-latest-win-x64.zip
/download/education  -> HelpSys-Education-latest-win-x64.zip
```

releaseには追跡用のバージョン付きZIPと、サイト用の固定名ZIPの両方を公開します。CIはHTML・redirect・release workflow・Education API既定値を横断検査し、どれか1つでも名前やURLがずれた場合に失敗します。

### Education APIの配布版既定接続

Education版は以前、`HELPSYS_EDUCATION_API_BASE` を利用者が手動設定しない限りAIヒントが「未接続」になる構造でした。v6以降の本番HelpSys Workerは次も処理します。

```text
POST /v1/education/assist
```

Educationデスクトップの既定APIは通常版と同じ本番Workerなので、配布ZIPを展開した利用者が環境変数を設定する必要はありません。別Education WorkerやローカルWorkerを使う場合だけ `HELPSYS_EDUCATION_API_BASE` で上書きします。

### 前面アプリ監視の競合防止

Reliability v5で、前面PIDへの監視切替要求に世代番号を持たせました。アプリAからBへ素早く切り替わり、古いA向け非同期処理がBより遅れて戻ってきても、Aへ監視対象を巻き戻しません。

アプリ起動直後にプロセスだけ存在し、トップレベルWindowがUIAへまだ公開されていない場合は「購読済み」と誤認せず、次のheartbeatで同じPIDへの購読を再試行します。同一プロセスが複数Windowを持つ場合は、可視Windowのうちキーボードフォーカスを持つものを優先します。

### 計画の鮮度

案内セッションは `GuidanceSessionController` が一元管理し、各計画に generation 番号を付けます。

```text
Idle → Capturing → Planning → Presenting → AwaitingUserAction → Verifying
                                                        ├→ Capturing
                                                        ├→ Clarifying
                                                        └→ Done
任意の状態 → Stopped / Idle
```

画面変更、中止、新しい計画で generation が更新された後に古いHTTP応答やVision結果が戻ってきても表示・発話しません。AI呼び出しの前後と表示直前にも現在の前面アプリを再確認します。

### 操作結果の確認

クリックや文字入力を検出しただけでは成功にしません。

- チェックボックス・ラジオボタン: `TogglePattern` / 選択状態
- リスト・タブ・ツリー: 選択・展開状態
- ブラウザ移動: URL変化
- アプリ切替: 前面プロセス変化
- ウィンドウ操作: ウィンドウ集合・前面状態の変化
- 一般操作: 安定したUI状態変化を複数回確認

`type_text` は入力欄の値が変わっただけでは完了扱いにしません。Enter等の仕上げ操作後に期待する状態遷移が確認できて初めて成功とします。

### 状態監視と再計画

HelpSys はフォーカス変更、構造変更、heartbeat 等から状態変化を把握します。監視イベントが発生したこと自体は案内・発話の理由にはしません。入力途中のサジェストや一時的なUI変化を即座に新しい指示へ変換せず、現在の目的に関係する安定した変化かを判定してから再計画します。

### Visionフォールバック

UIAで安全に対象を特定できない場合だけ、現在操作しているモニターのスクリーンショットを使います。

- HelpSys自身のウィンドウはキャプチャ時に隠す。
- 対象モニターを特定できなければ画像を送信しない。
- パスワード欄を対象モニター内でキャプチャ前後に検索する。
- プライバシー確認を有限時間・有限ノード数で完了できなければ画像を送信しない。
- Visionが返した座標をそのまま使わず、現在実在する操作可能なUIへ再照合する。
- Vision処理中に作業文脈が変わった場合は結果を破棄する。

### Worker側の最終ガード

AIの出力だけに安全性・意味判定を任せません。

- バックグラウンドでアプリが起動しているだけでは「アプリを開く」クエストを完了扱いにしない。
- パスワード、PIN、OTP、認証コード、リカバリキー、CVV等をHelpSysへ入力するよう求めるclarificationは最終ガードで拒否する。
- 一時的な `408 / 429 / 500 / 502 / 503 / 504` と一時通信例外のみ1回再試行する。
- 画面変更によるキャンセルを通信障害として表示しない。
- Educationのテスト段階ではAI推論を呼ばない。

## 構成

```text
Windows HelpSys (.NET/WPF)
  ├─ SystemContextService
  ├─ UiAutomationScanner
  ├─ GuidanceSessionController
  ├─ GuidanceStateWatcher
  ├─ CloudGuideService
  ├─ target再照合
  ├─ Vision fallback
  ├─ 操作結果検証
  ├─ SpeechOutputService
  └─ click-through overlay
          │
          ▼
Cloudflare Worker + Static Assets
  ├─ /
  ├─ /download
  ├─ /download/education
  ├─ /health
  ├─ /v1/guide
  ├─ /v1/vision-guide
  └─ /v1/education/assist
```

## Cloudflare

`wrangler.jsonc` には Workers AI binding `AI` と `./site` static assets が設定されています。通常案内の既定モデル:

```text
@cf/google/gemma-4-26b-a4b-it
```

Cloudflare側の `HELPSYS_MODEL` で通常案内モデルを、`HELPSYS_EDUCATION_MODEL` でEducationモデルを交換できます。Education側は未設定の場合も安全な既定モデルへフォールバックします。

```powershell
npm install
npm run check
npm test
npm run dev
npm run deploy
```

必要なら `HELPSYS_API_KEY` / `HELPSYS_EDUCATION_API_KEY` を設定できます。設定時はWindows側にも対応するキーを設定します。

公開Workerを本番運用する場合、アプリ内の共有キーだけを濫用対策とみなさず、Cloudflare AccessやRate Limiting等のエッジ側制御を別途設定してください。

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

- Worker JavaScript の構文
- Worker contract / visible-first / deliberation / reliability guards
- 本番entrypoint経由のEducation assist契約
- 公開HTML / `_redirects` / release asset / API既定値の整合性
- Windows Release build
- UI Automationを使った実際の案内表示
- smoke実行時の画面キャプチャ保存

Education smokeではカリキュラム整合性、段階ロック、教育→練習→テストのゲート、配布版の教育AI既定接続表示も確認します。

Preview release はDesktopコード、solution、release workflow、READMEの変更で再生成します。同梱READMEと配布バイナリの説明がずれないようにします。

## 残る運用課題

- 実アプリ別の操作成功判定を増やす。
- 長時間実機利用で誤案内率・再計画率・UIA provider例外率を計測する。
- 成功した操作系列のキャッシュで応答速度とAPI使用量を改善する。
- 公開APIの濫用対策はCloudflare Access / Rate Limiting等をアカウント側で設定する。リポジトリだけでは完結しない。
