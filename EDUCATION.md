# HelpSys Education

HelpSys Education は通常版 HelpSys とは別の Windows アプリとして開発します。

## 役割分担

- **通常版 HelpSys**: 現在のPC状態から、目的達成のための正しい次の1手を案内する。
- **HelpSys Education**: パソコン操作そのものを段階的に学習・練習・テストする。

通常版のUIへ教育モードを埋め込まず、別実行ファイル `HelpSys.Education.exe` とします。

## 学習フロー

`教育 → 練習 → テスト → 合格 → 次の単元`

### 練習教材

すべてを自作せず、既存の良質な教材を利用できる単元は外部教材を開きます。初期のタイピング教材は `https://www.e-typing.ne.jp/` です。マウス・Windows・ファイル操作などは、必要に応じて専用の安全な疑似UIや練習フォルダーを追加します。

## 教育AI v1

教育AIは通常版HelpSys Workerとは別の `helpsys-education` Workerとして定義します。

- `education`: 初心者向けの説明・質問回答
- `practice`: 3段階ヒント。最初から答えを出さず、必要なときだけ具体化する
- `test`: **AIを呼び出さない**。答え・ヒントを構造的に遮断する
- 秘密情報をHelpSysへ送らせる出力は最終ガードで拒否する
- ベースモデルは `HELPSYS_EDUCATION_MODEL` で通常版と独立して交換可能

ローカル:

```powershell
npm run test:education
npm run dev:education
```

Cloudflareへ別Workerとしてデプロイ:

```powershell
npm run deploy:education
```

デスクトップEducationアプリから接続するときだけ次を設定します。未設定でも教材・進捗・外部練習サイトは利用できます。

```powershell
$env:HELPSYS_EDUCATION_API_BASE="https://<education-worker>"
```

APIキーを設定する場合はWorker側 `HELPSYS_EDUCATION_API_KEY` とWindows側の同名環境変数を合わせます。

## 初期カリキュラム

1. マウスの基本
2. キーボードの基本
3. タイピング
4. Windowsの基本
5. ファイルとフォルダー
6. Webブラウザ

## 進捗

各単元の教育完了・練習完了・テスト合格を LocalApplicationData 配下 `HelpSys.Education/progress.json` へ保存します。

## 今後

- 通常版の認識・検証エンジンを共有ライブラリ化
- 実画面を使う練習の操作検出
- テスト自動採点
- 誤操作回数、ヒント利用回数、所要時間の記録
- 苦手操作の自動復習
