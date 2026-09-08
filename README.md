# HelpSys

HelpSys は Windows の操作を代行せず、現在の画面を確認して「人間が次に何を操作すべきか」を1手ずつ案内する学習・作業支援システムです。

## 原則

- 人間がクリック・キー入力を行う。HelpSys は入力を注入しない。
- 利用者の依頼がない待機中は、案内用の画面解析やAI通信を行わない。
- 起動は画面端の HelpSys ボタン、または `Ctrl+Alt+H`。
- Windows UI Automation（UIA）を第一情報源にする。
- AIへ送るUI候補は原則として現在の前面アプリとWindowsシェルに限定する。
- 前面アプリを特定できない場合は背景UIを推測して送信せず、再取得しても不明なら案内を止める。
- AIは次の1手だけを提案する。Windows側は対象を再照合し、現在も実在する操作可能な要素だけを案内する。
- 画面が変わった場合、古い計画・青枠・音声を新しい画面へ持ち越さない。
- UIAだけで特定できない場合のみVisionを使う。

## セッション状態

案内セッションは `GuidanceSessionController` が一元管理します。

```text
Idle
  ↓
Capturing
  ↓
Planning
  ↓
Presenting
  ↓
AwaitingUserAction
  ↓
Verifying
  ├─→ Capturing
  ├─→ AwaitingUserAction
  └─→ Clarifying

任意の状態 → Stopped / Idle
```

各計画には単調増加する generation 番号が付きます。画面変更、中止、新しい計画でgenerationが更新された後に古いHTTP応答やVision結果が戻ってきても、現在のgenerationと一致しなければ表示・発話しません。また、古いplannerが非同期で終了処理中の間は新しいplannerを開始しません。

## 画面監視

HelpSysはWindows全体の構造変更を常時詳細監視する方式ではありません。

- アプリ切替検出用にUIAのフォーカス変更を受け取る。
- 構造変更イベントは現在の前面プロセスに属するトップレベルウィンドウへ限定する。
- 650msのquiet periodで連続イベントをまとめる。
- 約1.4秒のheartbeatを保険として使う。
- 通常の文字入力中に発生する候補・サジェスト等の一時的なUI変化は再計画理由にしない。
- 前面アプリ変更や実際のブラウザURL変更は強い画面境界として扱う。
- UIツリーだけの変化は複数回確認してから古い案内を破棄する。

## 発話

案内文は決定直後に即読み上げません。

- 自動発話は750ms保留する。
- その間に案内が更新された場合、古い発話をキャンセルする。
- 同じ自動案内の短時間連続読み上げを抑制する。
- 画面変更を確定した時点で、古い読み上げと青枠を同時に停止する。
- 「読み上げ」ボタンによる利用者の明示的な再読み上げは許可する。

## 操作結果の確認

HelpSysは「クリックを検出した」だけでは成功と判断しません。操作後に現在状態を確認します。

優先する確認例:

- チェックボックス・ラジオボタン: `TogglePattern` / 選択状態の変化
- リスト・タブ・ツリー: 選択状態 / 展開状態の変化
- 入力欄: フォーカスや値の変化
- ブラウザ移動: URLの変化
- アプリ切替: 前面プロセスの変化
- ウィンドウ操作: 前面アプリ内のウィンドウ集合の変化

操作固有の状態が取れないアプリでは、現在の前面アプリに限定した安定UIトポロジー差分をフォールバックとして使います。一時的な1フレームの変化だけで成功にしないため、変化を独立した2回の観測で確認します。

## Visionフォールバック

UIAで安全に対象を決められない場合だけ、現在操作しているモニターのスクリーンショットを使います。

- HelpSys自身のウィンドウはキャプチャ時に隠す。
- パスワード欄は候補リストとは独立してキャプチャ前後にUIA検索し、黒塗りできない場合は画像を送信しない。
- 不要な別モニターは原則送信しない。
- Visionが返した生座標をそのまま青枠や音声に使わない。
- 現在その場所に実在する操作可能なUIA要素へ再照合できた場合だけ案内する。

## 通信と失敗分類

HTTPの遅延、サービス障害、画面変更によるキャンセルを同じ「通信不良」として扱いません。

- 一時的なHTTP `408 / 429 / 500 / 502 / 503 / 504` と一時的な通信例外は1回だけ再試行する。
- ネットワーク到達失敗、サービス一時停止、要求拒否、不正な応答形式を別の失敗種別として扱う。
- 画面変更や利用者操作によるキャンセルは通信障害へ変換しない。
- デスクトップ側が計画処理全体の有限時間を管理し、HTTPクライアント独自の短いtimeoutと競合させない。

## 現在の構成

```text
Windows HelpSys (.NET/WPF)
  ├─ 明示的な依頼
  ├─ SystemContextService
  │    └─ 前面アプリ / ブラウザ状態
  ├─ UiAutomationScanner
  │    ├─ UI候補
  │    └─ 操作固有状態
  ├─ GuidanceSessionController
  │    ├─ 単一状態機械
  │    └─ generation / planner ownership
  ├─ GuidanceStateWatcher
  │    └─ 前面ウィンドウ中心の変化監視
  ├─ CloudGuideService
  ├─ ローカルtarget再照合
  ├─ Vision fallback
  ├─ 操作結果検証
  ├─ SpeechOutputService
  └─ click-through overlay
          │
          ▼
Cloudflare Worker
  ├─ /health
  ├─ /v1/guide
  │    └─ Workers AI structured guidance
  └─ /v1/vision-guide
       └─ visual fallback
```

## Cloudflare

`wrangler.jsonc` には Workers AI binding `AI` が設定されています。既定モデル:

```text
@cf/google/gemma-4-26b-a4b-it
```

Cloudflare側の環境変数 `HELPSYS_MODEL` でモデルを交換できます。

ローカルWorker:

```powershell
npm install
npm run dev
```

デプロイ:

```powershell
npm run deploy
```

必要ならCloudflare secret `HELPSYS_API_KEY` を設定できます。設定した場合はWindows側にも同名の環境変数を設定します。

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
```

## 今後の改善候補

- UIA patternを十分公開しないアプリ向けの操作別成功判定拡張。
- 長時間実機利用でのCPU使用率、UIA provider例外率、誤再計画率の計測。
- 成功した操作系列のローカルキャッシュによる応答高速化とAPI使用量削減。
- 必要ならローカルウェイクワード検出を追加し、呼び出し方法を拡張する。
