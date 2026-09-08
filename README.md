# HelpSys

HelpSys は Windows の操作を代行せず、画面を理解して「人間が次に何を操作すべきか」をオーバーレイで示す学習・作業支援システムです。

## 原則

- 人間がクリック・キー入力を行う。HelpSys は入力を注入しない。
- 待機中は画面取得もAI通信もしない。
- 起動は画面端の HelpSys ボタン、または `Ctrl+Alt+H`。
- Windows UI Automation を第一情報源にする。
- AIは現在画面に実在するUI要素から、次の1手だけを選ぶ。
- 確度が低い場合は矢印を出さず、確認または「特定できない」を返す。
- Vision/OCR は UI Automation で情報不足の場合のフォールバックとして後続実装する。

## 現在の構成

```text
Windows HelpSys (.NET/WPF)
  ├─ 明示起動
  ├─ UI Automation snapshot
  ├─ CloudGuideService
  ├─ target id のローカル再照合
  └─ click-through overlay
          │
          ▼
Cloudflare Worker
  ├─ /health
  └─ /v1/guide
       └─ Workers AI structured JSON
```

HelpSys は要求を受けた時だけUI Automationスナップショットを作成し、Cloudflare Workerへ送ります。Workerは画面に実在する候補IDから1つだけを選択します。Windows側はそのIDをローカルの座標へ再照合してから矢印を表示します。

## Cloudflare

`wrangler.jsonc` には Workers AI binding `AI` が設定されています。既定モデルは以下です。

```text
@cf/google/gemma-4-26b-a4b-it
```

Cloudflare側の環境変数 `HELPSYS_MODEL` を変更すればモデルを交換できます。精度重視でWorkers Paidを使用する場合は、例えば `@cf/zai-org/glm-5.2` を指定できます。

GitHub連携によるCloudflare Buildsでは、リポジトリのルートをプロジェクトルートとして `wrangler deploy` を実行してください。

ローカルでWorkerを起動する場合:

```powershell
npm install
npm run dev
```

デプロイ:

```powershell
npm run deploy
```

必要ならCloudflare secret `HELPSYS_API_KEY` を設定できます。設定した場合、Windows側にも同名の環境変数を設定してください。

## Windows

必要環境:

- Windows 10/11
- .NET 10 SDK

```powershell
dotnet build HelpSys.sln
dotnet run --project src/HelpSys.Desktop/HelpSys.Desktop.csproj
```

Windowsクライアントの既定APIは:

```text
https://helpsys.syouziroupc.workers.dev
```

別URLを使う場合:

```powershell
$env:HELPSYS_API_BASE="https://example.workers.dev"
```

## 次の実装

1. UIAで対象が取れない場合だけスクリーンショットを取得するVisionフォールバック。
2. 1操作後のUI変化を検出し、同一タスクを次のステップへ進めるステートマシン。
3. ローカルのウェイクワード検出。ウェイク前は画面取得・AI通信を行わない。
4. 成功した操作系列のローカルキャッシュによる高速化とAPI使用量削減。
