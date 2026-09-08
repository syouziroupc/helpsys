const APP_DEFINITIONS = [
  { id: 'excel', display: 'Excel', search: 'Excel', aliases: ['excel', 'エクセル'], processes: ['excel'] },
  { id: 'word', display: 'Word', search: 'Word', aliases: ['word', 'ワード'], processes: ['winword'] },
  { id: 'powerpoint', display: 'PowerPoint', search: 'PowerPoint', aliases: ['powerpoint', 'パワーポイント', 'パワポ'], processes: ['powerpnt'] },
  { id: 'outlook', display: 'Outlook', search: 'Outlook', aliases: ['outlook', 'アウトルック'], processes: ['outlook', 'olk'] },
  { id: 'chrome', display: 'Google Chrome', search: 'Google Chrome', aliases: ['google chrome', 'chrome', 'クローム', 'グーグルクローム'], processes: ['chrome'] },
  { id: 'edge', display: 'Microsoft Edge', search: 'Microsoft Edge', aliases: ['microsoft edge', 'edge', 'エッジ'], processes: ['msedge'] },
  { id: 'explorer', display: 'エクスプローラー', search: 'エクスプローラー', aliases: ['エクスプローラー', 'explorer', 'ファイルを見たい', 'フォルダーを開'], processes: ['explorer'] },
  { id: 'settings', display: '設定', search: '設定', aliases: ['設定を開', 'windowsの設定', 'windows 設定'], processes: ['systemsettings'] },
  { id: 'notepad', display: 'メモ帳', search: 'メモ帳', aliases: ['メモ帳', 'notepad'], processes: ['notepad'] },
  { id: 'calculator', display: '電卓', search: '電卓', aliases: ['電卓', 'calculator'], processes: ['calculatorapp'] }
];

const CORE_KNOWLEDGE = `Windows操作の基本知識:
- 画面に目的の物が無いことは異常ではない。見えていない物を無理に推測して、関係のないボタンを選んではいけない。
- アプリを開く標準経路は、(1) 既に開いていればその画面を使う、(2) 目的のマークが画面にあればそれを使う、(3) 無ければ画面下部のWindowsのマークから検索する、の順。
- 「スタート」「タスクバー」「デスクトップ」「アイコン」「ダブルクリック」など、初心者が知らない可能性のある専門語を説明なしで使わない。
- マウス操作は「左ボタンを1回押す」「左ボタンを、間をあけずに2回押す」のように、実際の手の動きを書く。
- キーボードは「Enter（エンター）」のように、キーに書かれた文字と読み方を示す。
- 画面に存在しない対象を選んではいけない。次の標準経路に必要な物も見つからなければ、画像確認に切り替える。`;

const KNOWLEDGE_SECTIONS = [
  {
    test: /youtube|ユーチューブ|ホームページ|ウェブ|web|サイト|インターネット|検索したい/i,
    text: `ブラウザー操作:
- 既にChromeやEdgeの画面が開いていれば、もう一度ブラウザーを開かない。
- ホームページへ行くときは、画面上部の長い入力欄を選び、アドレスや検索語を入力する。
- 「アドレスバー」「URL」という言葉だけで説明しない。画面で見分けられる位置や形を説明する。`
  },
  {
    test: /ファイル|フォルダ|フォルダー|保存|名前を付けて保存|開く/i,
    text: `ファイル操作:
- ファイルやフォルダーが画面に見えなければ、まずエクスプローラーを開く経路を考える。
- ファイル一覧では、名前が一致する物を確認してから開く。似た名前を推測で選ばない。
- 保存先が不明なら勝手に決めず、ユーザーに確認する。`
  },
  {
    test: /wifi|wi-fi|ブルートゥース|bluetooth|プリンタ|音量|ディスプレイ|画面設定|windows.*設定|設定/i,
    text: `Windows設定:
- 設定項目が現在の画面に無ければ、Windowsの「設定」を開き、設定画面内の検索または分類を使う。
- 表示名が似ている設定を推測で選ばない。現在の見出しや説明文を状態判断に使う。`
  },
  {
    test: /コピー|貼り付け|切り取り|ctrl\s*\+\s*[cvx]|印刷/i,
    text: `基本操作:
- コピーや貼り付けでは、まず対象が選ばれていることを確認する。
- キーの組み合わせを使う場合は「Ctrlを押したままCを1回押す」のように手順を説明する。
- 印刷ではプリンター名や部数を推測せず、画面に表示された選択肢を確認する。`
  }
];

export function buildWindowsTaskContext(goal, elements = [], history = []) {
  const app = detectLaunchApp(goal);
  const extra = KNOWLEDGE_SECTIONS.filter(section => section.test.test(goal)).map(section => section.text).join('\n');
  const knowledge = [CORE_KNOWLEDGE, extra].filter(Boolean).join('\n\n');

  if (!app) return { kind: 'general', knowledge, deterministic: null, forceVision: false, allowedTargetIds: null };

  const running = isAppRunning(app, elements);
  const justOpen = isSimpleOpenGoal(goal, app);
  if (running && justOpen) {
    return {
      kind: 'launch-app', app, running: true, knowledge: `${knowledge}\n\n対象: ${app.display}。このアプリは既に開いているため、もう一度開く案内をしてはいけない。`,
      deterministic: { status: 'done', targetId: null, action: 'none', instruction: `${app.display}はすでに開いています。`, question: null, key: null, confidence: 0.99 },
      forceVision: false, allowedTargetIds: null
    };
  }

  if (running) {
    return {
      kind: 'launch-app', app, running: true, knowledge: `${knowledge}\n\n対象: ${app.display}。このアプリは既に開いている。現在のアプリ画面から次の操作を選ぶこと。`,
      deterministic: null, forceVision: false, allowedTargetIds: null
    };
  }

  const appTarget = findAppTarget(app, elements);
  const searchField = findSearchField(elements);
  const startButton = findStartButton(elements);
  const allowedTargetIds = new Set([appTarget?.id, searchField?.id, startButton?.id].filter(Boolean));

  if (appTarget) {
    const desktopLike = appTarget.controlType === 'ListItem' && /explorer/i.test(appTarget.processName || '');
    return {
      kind: 'launch-app', app, running: false, knowledge,
      deterministic: {
        status: 'target', targetId: appTarget.id, action: desktopLike ? 'double_click' : 'left_click',
        instruction: desktopLike
          ? `青い枠で囲まれた「${app.display}」のマークを、マウスの左ボタンで、間をあけずに2回押してください。`
          : `青い枠で囲まれた「${app.display}」を、マウスの左ボタンで1回押してください。`,
        question: null, key: null, confidence: 0.98
      },
      forceVision: false, allowedTargetIds
    };
  }

  if (searchField?.focused) {
    return {
      kind: 'launch-app', app, running: false, knowledge,
      deterministic: {
        status: 'target', targetId: searchField.id, action: 'type_text',
        instruction: `キーボードで「${app.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
        question: null, key: 'Enter', confidence: 0.98
      },
      forceVision: false, allowedTargetIds
    };
  }

  if (searchField) {
    return {
      kind: 'launch-app', app, running: false, knowledge,
      deterministic: {
        status: 'target', targetId: searchField.id, action: 'left_click',
        instruction: '青い枠で囲まれた、文字を入力できる検索の欄を、マウスの左ボタンで1回押してください。',
        question: null, key: null, confidence: 0.98
      },
      forceVision: false, allowedTargetIds
    };
  }

  if (startButton) {
    return {
      kind: 'launch-app', app, running: false, knowledge,
      deterministic: {
        status: 'target', targetId: startButton.id, action: 'left_click',
        instruction: '青い枠で囲まれたWindowsの四角いマークを、マウスの左ボタンで1回押してください。',
        question: null, key: null, confidence: 0.98
      },
      forceVision: false, allowedTargetIds
    };
  }

  return {
    kind: 'launch-app', app, running: false,
    knowledge: `${knowledge}\n\n対象: ${app.display}。画面上に${app.display}もWindowsの検索欄もWindowsの開始ボタンも見つかっていない。無関係な物を選ばず、画像確認で画面下部のWindowsの四角いマークまたは検索欄を探すこと。`,
    deterministic: null, forceVision: true, allowedTargetIds
  };
}

export function guardDecisionForTask(task, decision) {
  if (!task || task.kind !== 'launch-app' || task.running || !decision || decision.status !== 'target') return decision;
  if (!task.allowedTargetIds || task.allowedTargetIds.size === 0) return notFound(decision.confidence);
  if (!task.allowedTargetIds.has(decision.targetId)) return notFound(decision.confidence);
  return decision;
}

export function visionHintForTask(task) {
  if (!task || task.kind !== 'launch-app' || task.running) return task?.knowledge || CORE_KNOWLEDGE;
  return `${task.knowledge}\n\n画像確認の優先順位: 1) ${task.app.display}のマークが見えるならそれ、2) 見えなければ画面下部のWindowsの四角いマーク、3) 検索欄。無関係なアプリやボタンは選ばない。`;
}

function detectLaunchApp(goal) {
  const lower = goal.toLowerCase();
  const launchIntent = /(開いて|開きたい|起動|立ち上げ|使いたい|出して|表示して)/.test(goal);
  if (!launchIntent) return null;
  return APP_DEFINITIONS.find(app => app.aliases.some(alias => lower.includes(alias.toLowerCase()))) || null;
}

function isSimpleOpenGoal(goal, app) {
  const stripped = goal.toLowerCase();
  if (!app.aliases.some(alias => stripped.includes(alias.toLowerCase()))) return false;
  return /(開いて|開きたい|起動して|立ち上げて|出して|表示して)[。.!！ ]*$/.test(goal.trim()) || goal.trim().length <= app.display.length + 10;
}

function isAppRunning(app, elements) {
  return elements.some(element => {
    const process = String(element.processName || '').toLowerCase();
    if (app.processes.some(name => process === name || process.startsWith(`${name}.`))) return true;
    if (!['Window', 'Pane', 'Document', 'TitleBar'].includes(element.controlType)) return false;
    return matchesAppText(app, element.name);
  });
}

function findAppTarget(app, elements) {
  const candidates = elements.filter(element => element.interactable && element.enabled !== false && matchesAppText(app, element.name));
  if (!candidates.length) return null;
  return candidates.sort((a, b) => targetScore(b, app) - targetScore(a, app))[0];
}

function targetScore(element, app) {
  let score = 0;
  const name = String(element.name || '').toLowerCase();
  if (name === app.display.toLowerCase() || name === app.search.toLowerCase()) score += 100;
  if (app.aliases.some(alias => name === alias.toLowerCase())) score += 80;
  if (['ListItem', 'Button', 'MenuItem'].includes(element.controlType)) score += 30;
  if (/searchhost|startmenuexperiencehost|explorer/i.test(element.processName || '')) score += 20;
  return score;
}

function findSearchField(elements) {
  return elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' && /(検索|search)/i.test(`${element.name || ''} ${element.automationId || ''}`)) || null;
}

function findStartButton(elements) {
  return elements.find(element => {
    if (!element.interactable || element.enabled === false || element.controlType !== 'Button') return false;
    const text = `${element.name || ''} ${element.automationId || ''}`.trim();
    return /^(スタート|start)$/i.test(String(element.name || '').trim()) || /startbutton|スタート/i.test(text);
  }) || null;
}

function matchesAppText(app, value) {
  const text = String(value || '').toLowerCase();
  if (!text) return false;
  return text.includes(app.display.toLowerCase()) || app.aliases.some(alias => text.includes(alias.toLowerCase()));
}

function notFound(confidence = 0) {
  const c = Number(confidence);
  return { status: 'not_found', targetId: null, action: 'none', instruction: '今の画面だけでは、次に押す場所を安全に決められません。画面全体を確認します。', question: null, key: null, confidence: Number.isFinite(c) ? Math.max(0, Math.min(1, c)) : 0 };
}
