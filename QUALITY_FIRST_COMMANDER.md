# HelpSys Reliability v7 — Quality First + Commander

このブランチでは通常版HelpSysを優先します。Educationの追加開発は、通常版の品質優先案内とCommanderが安定してから再開します。

## Quality First

従来は UI Automation / 前面プロセス / URL 等で先に判断し、Visionは失敗時のfallbackでした。v7では順序を逆転させません。**毎ステップでスクリーンショットを主要証拠として確認し、UI Automation等は補助情報として同時に照合します。**

- `/v1/quality-guide` へ screenshot + UIA + systemContext + history を1回で送る。
- `runningApps`、プロセス名、URL、履歴だけでは `done` にしない。
- `done` は現在のスクリーンショット上の可視証拠が必須。最低confidence 0.90。
- `target` も可視確認が必須。最低confidence 0.80。
- UIAと画像が一致しない場合は先へ進まず停止または確認する。
- UIAが空でもスクリーンショットは確認する。
- スクリーンショットを安全に取得できない場合はUIAだけへfallbackしない。
- カスタム描画UIでUIAが使えない場合、画像上の対象がconfidence 0.92以上ならvisual-only targetを許可する。
- AI応答後・表示直前にも前面文脈を再確認し、古い画面の判断は捨てる。
- 旧ループは `AdvanceGuideLegacyAsync` として比較用に残すが、通常経路では使わない。

## Commander

呼び出し名は **コマンダー**、ウェイクフレーズは **「ねえコマンダー」**。

- Windowsローカルの `System.Speech.Recognition` でウェイクフレーズだけを待つ。
- ウェイク検出後は待機recognizerを完全に解放してから通常の音声入力へ移る。
- 手動音声入力・確認回答・Commander本認識は `MicrophoneCoordinator` 経由でウェイク待機より優先する。
- 他処理がマイクを使う間、Commanderは一時停止し、終了後に再取得する。
- マイク/音声認識が一時的に使えない場合は約4秒後に静かに再試行する。
- 「ねえコマンダー」→「はい。どうしましたか？」→用件を1回聞き取る→自動でquality-first案内開始。
- UIからCommanderをON/OFFできる。OFFでは待機recognizerも解放する。
- 日本語音声認識がない環境でもアプリ本体は起動し続ける。

## CI

Windows smokeは以下を追加検査する。

1. `quality-guide.js` の構文・self-test。
2. HelpSys UIに `CommanderButton` が公開されること。
3. 通常版案内が `/v1/quality-guide` を使うこと。
4. そのrequestに `data:image/png;base64,...` の現在スクリーンショットが含まれること。
5. fused responseから実際の青枠案内が表示されること。
