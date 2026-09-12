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

const SITE_DEFINITIONS = [
  { id: 'youtube', display: 'YouTube', search: 'YouTube', aliases: ['youtube', 'ユーチューブ'], domains: ['youtube.com'] },
  { id: 'rakuten', display: '楽天市場', search: '楽天市場', aliases: ['楽天市場', '楽天', 'rakuten'], domains: ['rakuten.co.jp'] },
  { id: 'yahoo-jp', display: 'Yahoo! JAPAN', search: 'Yahoo JAPAN', aliases: ['yahoo japan', 'yahoo! japan', 'ヤフー', 'yahoo'], domains: ['yahoo.co.jp'] },
  { id: 'amazon-jp', display: 'Amazon', search: 'Amazon', aliases: ['amazon', 'アマゾン'], domains: ['amazon.co.jp'] },
  { id: 'google-maps', display: 'Google マップ', search: 'Google マップ', aliases: ['google map', 'googleマップ', 'グーグルマップ'], domains: ['google.com', 'google.co.jp'] }
];

const SEARCH_DOMAINS = ['google.com', 'google.co.jp', 'bing.com', 'search.yahoo.co.jp', 'duckduckgo.com'];
const BROWSER_PROCESSES = ['chrome', 'msedge', 'firefox', 'brave', 'opera', 'vivaldi'];

const CORE_KNOWLEDGE = `Windows操作の基本知識:
- 最初の画面がデスクトップとは限らない。必ず foregroundProcess / foregroundTitle と、前面アプリの要素を最優先して現在状態を判断する。
- 背後に見えているデスクトップや別アプリを、前面ウィンドウ越しに押させない。
- 画面に目的の物が無いことは異常ではない。関係のない物を代わりに選ばない。
- アプリが見えない場合は、画面下のボタンを探し回るより、キーボードの Windows キーを使って検索画面を開く方法を優先する。
- 失敗した操作のあとに別の無関係な経路へ飛ばない。現在状態を再確認し、同じ標準経路から復帰する。
- 現在画面の認識不足は利用者への質問で埋めない。UI Automation・前面ウィンドウ・画面画像を再取得して自動復帰し、なお安全に確定できなければ推測せず停止する。
- 「クリック」「ダブルクリック」「アイコン」「タスクバー」「デスクトップ」「アドレスバー」「URL」「プロファイル」などの用語を初心者向け説明で裸のまま使わない。
- マウス操作は「左ボタンを1回押す」「左ボタンを、間をあけずに2回押す」のように実際の手の動きを書く。
- キーボード操作はキーに印字された文字を示し、複数キーなら「Ctrlを押したままTを1回押す」のように説明する。
- アカウント、利用者名、保存先、上書き、削除、購入、支払い、許可、既定アプリなど、利用者の選択が必要な分岐をAIが勝手に決めない。`;

const KNOWLEDGE_SECTIONS = [
  {
    test: /youtube|ユーチューブ|ホームページ|ウェブ|web|サイト|インターネット|検索したい|楽天|yahoo|amazon|アマゾン/i,
    text: `ブラウザー操作:
- 目的のサイトへ行くときは、原則「新しいタブを開く → 画面内に見えている検索欄へサイト名を入力する → 公式サイトの検索結果を確認して開く」。ドメイン文字列の直入力を初心者へ要求しない。
- Webページ内の検索欄とブラウザー上部のアドレス兼検索欄が両方ある場合、Webページ内の見えている検索欄を必ず優先する。上部の欄は画面内検索欄を確認できない場合だけ使う。
- 既に目的サイトが開いていれば同じ作業を繰り返さない。
- 検索結果の「広告」「スポンサー」は原則選ばない。
- 既知サービスは公式ドメインと一致する結果だけを案内する。似た綴りのドメインを選ばない。
- ブラウザーが危険・詐欺・フィッシング・証明書エラー等を警告している場合、警告を突破させず前のページへ戻す。`
  },
  {
    test: /ファイル|フォルダ|フォルダー|保存|名前を付けて保存|開く/i,
    text: `ファイル操作:
- ファイルやフォルダーが見えなければエクスプローラーを開く標準経路へ進む。
- 名前が似ているファイルを推測で選ばない。
- 保存先、上書き、削除は利用者に確認する。`
  },
  {
    test: /wifi|wi-fi|ブルートゥース|bluetooth|プリンタ|音量|ディスプレイ|画面設定|windows.*設定|設定/i,
    text: `Windows設定:
- 設定項目が現在画面に無ければWindowsの設定を開き、設定内検索または分類を使う。
- 似た設定名を推測で選ばない。`
  },
  {
    test: /コピー|貼り付け|切り取り|ctrl\s*\+\s*[cvx]|印刷/i,
    text: `基本操作:
- コピーや貼り付けでは対象が選ばれていることを先に確認する。
- キーの組み合わせは、押す順序を初心者向けに説明する。
- 印刷ではプリンター名や部数を勝手に決めない。`
  }
];

export function buildWindowsTaskContext(goal, elements = [], history = [], systemContext = {}) {
  const knowledge = buildKnowledge(goal);
  const safety = detectSafetyState(elements, systemContext);
  if (safety) return safety;

  const branch = detectChoiceBranch(elements, systemContext);
  if (branch) return branch;

  const site = detectSiteGoal(goal);
  if (site) return buildSiteTask(site, goal, elements, history, systemContext, knowledge);

  const app = detectLaunchApp(goal) || detectGenericLaunchApp(goal);
  if (app) return buildAppTask(app, goal, elements, systemContext, knowledge);

  return {
    kind: 'general', knowledge: `${knowledge}\n\n現在状態: ${describeSystem(systemContext)}`,
    deterministic: null, forceVision: false, allowedTargetIds: null, site: null
  };
}

function buildAppTask(app, goal, elements, systemContext, knowledge) {
  const running = isAppRunning(app, elements, systemContext);
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  const foregroundApp = app.processes.some(process => process.toLowerCase() === foreground);
  const justOpen = isSimpleOpenGoal(goal, app);
  if (foregroundApp && justOpen) {
    return task('launch-app', knowledge, {
      status: 'done', targetId: null, action: 'none', instruction: `${app.display}はすでに開いています。`, question: null, key: null, confidence: 0.99
    }, false, null, app);
  }

  if (foregroundApp) {
    return task('launch-app', `${knowledge}\n\n${app.display}は現在前面で開いている。前面画面を確認して目的の続きへ進む。`, null, false, null, app, true);
  }

  const appTarget = findAppTarget(app, elements, systemContext);
  const searchField = findWindowsSearchField(elements);
  const allowed = new Set([appTarget?.id, searchField?.id].filter(Boolean));

  if (appTarget) {
    const needsTwoPresses = appTarget.controlType === 'ListItem' && /explorer/i.test(appTarget.processName || '');
    return task('launch-app', knowledge, {
      status: 'target', targetId: appTarget.id, action: needsTwoPresses ? 'double_click' : 'left_click',
      instruction: needsTwoPresses
        ? `青い枠の「${app.display}」のマークで、マウスの左ボタンを間をあけずに2回押してください。`
        : `青い枠の「${app.display}」で、マウスの左ボタンを1回押してください。`,
      question: null, key: null, confidence: 0.98
    }, false, allowed, app);
  }

  if (searchField?.focused) {
    return task('launch-app', knowledge, {
      status: 'target', targetId: searchField.id, action: 'type_text',
      instruction: `キーボードで「${app.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
      question: null, key: 'Enter', confidence: 0.98
    }, false, allowed, app);
  }

  if (searchField) {
    return task('launch-app', knowledge, {
      status: 'target', targetId: searchField.id, action: 'left_click',
      instruction: '青い枠の、文字を入力できる検索の欄で、マウスの左ボタンを1回押してください。',
      question: null, key: null, confidence: 0.98
    }, false, allowed, app);
  }

  return task('launch-app', `${knowledge}\n\n${app.display}は現在画面に無い。画面下の見えないボタンを探させず、Windowsキーで検索画面を開く。`, {
    status: 'target', targetId: null, action: 'press_key',
    instruction: 'キーボードの左下にある、窓の形の「Windows」キーを1回押してください。',
    question: null, key: 'Windows', confidence: 0.99
  }, false, new Set(), app);
}

function buildSiteTask(site, goal, elements, history, systemContext, knowledge) {
  const browser = systemContext?.browser || null;
  const currentDomain = normalizeHost(browser?.domain);
  const currentUrl = String(browser?.url || '');

  if (currentDomain && domainMatchesAny(currentDomain, site.domains)) {
    return task('site', knowledge, {
      status: 'done', targetId: null, action: 'none', instruction: `${site.display}を開けました。`, question: null, key: null, confidence: 0.99
    }, false, null, null, true, site);
  }

  const onSearchResults = currentDomain && SEARCH_DOMAINS.some(domain => domainMatches(currentDomain, domain)) && urlLooksLikeSearch(currentUrl, site.search);
  if (onSearchResults) {
    const official = findOfficialSearchResult(site, elements);
    if (official) {
      return task('site', `${knowledge}\n\n${site.display}の公式ドメイン: ${site.domains.join(', ')}`, {
        status: 'target', targetId: official.id, action: 'left_click',
        instruction: `「${site.display}」の公式サイトと確認できた検索結果で、マウスの左ボタンを1回押してください。`,
        question: null, key: null, confidence: 0.99
      }, false, new Set([official.id]), null, true, site);
    }
    return task('site', `${knowledge}\n\n検索結果から ${site.domains.join(' または ')} と表示された非広告の公式結果だけを探す。`, null, true, new Set(), null, true, site);
  }

  const browserForeground = browser && BROWSER_PROCESSES.includes(String(systemContext?.foregroundProcess || '').toLowerCase());
  if (browserForeground) {
    const webSearchField = findWebSearchField(elements, systemContext);
    if (webSearchField?.focused) {
      return task('site', knowledge, {
        status: 'target', targetId: webSearchField.id, action: 'type_text',
        instruction: `画面の中の検索欄に、キーボードで「${site.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
        question: null, key: 'Enter', confidence: 0.99
      }, false, new Set([webSearchField.id]), null, true, site);
    }
    if (webSearchField) {
      return task('site', knowledge, {
        status: 'target', targetId: webSearchField.id, action: 'left_click',
        instruction: '青い枠の、ページの中にある検索欄で、マウスの左ボタンを1回押してください。',
        question: null, key: null, confidence: 0.99
      }, false, new Set([webSearchField.id]), null, true, site);
    }

    if (looksLikeNewTab(browser)) {
      const addressField = findBrowserAddressField(elements, systemContext);
      if (addressField?.focused || browser.addressFieldFocused) {
        const target = addressField || findFocusedEdit(elements, systemContext);
        if (target) {
          return task('site', `${knowledge}\n\nページ内の検索欄を構造情報で確認できなかったため、ブラウザー上部の検索兼用欄を代替として使う。`, {
            status: 'target', targetId: target.id, action: 'type_text',
            instruction: `キーボードで「${site.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
            question: null, key: 'Enter', confidence: 0.92
          }, false, new Set([target.id]), null, true, site);
        }
      }
      return task('site', `${knowledge}\n\nページ内の検索欄を確認できない場合だけ、上部の検索兼用欄を代替として使う。`, {
        status: 'target', targetId: null, action: 'press_key',
        instruction: 'キーボードの「Ctrl」と書かれたキーを押したまま、「L」と書かれたキーを1回押してください。',
        question: null, key: 'Ctrl+L', confidence: 0.90
      }, false, new Set(), null, true, site);
    }

    if (currentDomain && !isNeutralBrowserPage(currentDomain, currentUrl)) {
      return task('site', knowledge, {
        status: 'target', targetId: null, action: 'press_key',
        instruction: '今のページはそのまま残します。「Ctrl」と書かれたキーを押したまま、「T」と書かれたキーを1回押してください。',
        question: null, key: 'Ctrl+T', confidence: 0.99
      }, false, new Set(), null, true, site);
    }

    const addressField = findBrowserAddressField(elements, systemContext);
    if (addressField?.focused || browser?.addressFieldFocused) {
      const target = addressField || findFocusedEdit(elements, systemContext);
      if (target) return task('site', `${knowledge}\n\nページ内の検索欄を確認できなかった場合の代替経路。`, {
        status: 'target', targetId: target.id, action: 'type_text',
        instruction: `キーボードで「${site.search}」と入力し、最後に「Enter」と書かれたキーを1回押してください。`,
        question: null, key: 'Enter', confidence: 0.90
      }, false, new Set([target.id]), null, true, site);
    }

    return task('site', knowledge, {
      status: 'target', targetId: null, action: 'press_key',
      instruction: '新しい検索画面を開きます。「Ctrl」と書かれたキーを押したまま、「T」と書かれたキーを1回押してください。',
      question: null, key: 'Ctrl+T', confidence: 0.99
    }, false, new Set(), null, true, site);
  }

  const runningBrowser = chooseRunningBrowser(systemContext);
  if (runningBrowser) {
    const browserApp = runningBrowser === 'chrome' ? APP_DEFINITIONS.find(x => x.id === 'chrome') : APP_DEFINITIONS.find(x => x.id === 'edge');
    const candidate = browserApp ? findAppTarget(browserApp, elements, systemContext, true) : null;
    if (candidate) return task('site', knowledge, {
      status: 'target', targetId: candidate.id, action: 'left_click',
      instruction: '青い枠のインターネットを見るアプリで、マウスの左ボタンを1回押してください。',
      question: null, key: null, confidence: 0.96
    }, false, new Set([candidate.id]), null, true, site);
  }

  const searchField = findWindowsSearchField(elements);
  const edgeTarget = findAppTarget(APP_DEFINITIONS.find(x => x.id === 'edge'), elements, systemContext, true);
  if (edgeTarget) return task('site', knowledge, {
    status: 'target', targetId: edgeTarget.id, action: 'left_click',
    instruction: '青い枠の「Microsoft Edge」で、マウスの左ボタンを1回押してください。',
    question: null, key: null, confidence: 0.97
  }, false, new Set([edgeTarget.id]), null, true, site);
  if (searchField?.focused) return task('site', knowledge, {
    status: 'target', targetId: searchField.id, action: 'type_text',
    instruction: 'キーボードで「Microsoft Edge」と入力し、最後に「Enter」と書かれたキーを1回押してください。',
    question: null, key: 'Enter', confidence: 0.98
  }, false, new Set([searchField.id]), null, true, site);
  if (searchField) return task('site', knowledge, {
    status: 'target', targetId: searchField.id, action: 'left_click',
    instruction: '青い枠の検索の欄で、マウスの左ボタンを1回押してください。',
    question: null, key: null, confidence: 0.98
  }, false, new Set([searchField.id]), null, true, site);

  return task('site', knowledge, {
    status: 'target', targetId: null, action: 'press_key',
    instruction: 'キーボードの左下にある、窓の形の「Windows」キーを1回押してください。',
    question: null, key: 'Windows', confidence: 0.99
  }, false, new Set(), null, true, site);
}

export function guardDecisionForTask(taskInfo, decision) {
  if (!taskInfo || !decision) return decision;
  if (decision.status === 'clarify' && taskInfo.kind !== 'choice') return notFound(decision.confidence);
  if (decision.status !== 'target') return decision;
  if (decision.action === 'press_key' && !decision.targetId) return decision;
  if (taskInfo.allowedTargetIds && taskInfo.allowedTargetIds.size > 0 && !taskInfo.allowedTargetIds.has(decision.targetId)) return notFound(decision.confidence);
  if (taskInfo.kind === 'site' && /https?:\/\/|www\.|\.com|\.jp/i.test(decision.instruction || '')) return notFound(decision.confidence);
  return decision;
}

export function guardVisionDecisionForTask(taskInfo, decision) {
  if (!taskInfo || !decision) return decision;
  if (decision.status === 'clarify' && taskInfo.kind !== 'choice') return visionNotFound(decision.confidence);
  if (decision.status !== 'target') return decision;
  if (taskInfo.kind !== 'site' || !taskInfo.site) return decision;
  if (decision.sponsored === true) return visionNotFound(decision.confidence);
  const observed = normalizeHost(decision.observedDomain);
  if (!observed || !domainMatchesAny(observed, taskInfo.site.domains)) return visionNotFound(decision.confidence);
  return decision;
}

export function visionHintForTask(taskInfo) {
  if (!taskInfo) return CORE_KNOWLEDGE;
  if (taskInfo.kind === 'site' && taskInfo.site) {
    return `${taskInfo.knowledge}\n\n安全条件: 「広告」「スポンサー」と書かれた結果は選ばない。表示ドメインが ${taskInfo.site.domains.join(' または ')} と一致する公式結果だけを選ぶ。似た綴りは不可。`;
  }
  return taskInfo.knowledge || CORE_KNOWLEDGE;
}

function detectSafetyState(elements, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  const browser = systemContext?.browser;
  if (!BROWSER_PROCESSES.includes(foreground) && !browser) return null;
  const visible = `${systemContext?.foregroundTitle || ''} ${elements.filter(x => String(x.processName || '').toLowerCase() === foreground).map(x => x.name).join(' ')}`;
  if (!/(危険|安全ではありません|安全でない|詐欺|フィッシング|SmartScreen|Deceptive site|Dangerous site|Privacy error|プライバシーが保護されません|証明書.*(無効|エラー)|malware|phishing)/i.test(visible)) return null;
  return task('safety-block', `${CORE_KNOWLEDGE}\n\nブラウザーが安全上の警告を表示している。警告を突破する操作は絶対に案内しない。`, {
    status: 'target', targetId: null, action: 'press_key',
    instruction: '危険な可能性があるページです。先へ進まず、「Alt」と書かれたキーを押したまま、左向き矢印「←」のキーを1回押してください。',
    question: null, key: 'Alt+Left', confidence: 1
  }, false, new Set());
}

function detectChoiceBranch(elements, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  const text = `${systemContext?.foregroundTitle || ''} ${elements.filter(x => !foreground || String(x.processName || '').toLowerCase() === foreground).map(x => x.name).join(' ')}`;
  if (/(どなたが使用|プロファイル.*選|プロフィール.*選|アカウント.*選|ゲストモード)/i.test(text)) {
    return task('choice', CORE_KNOWLEDGE, {
      status: 'clarify', targetId: null, action: 'none', instruction: '',
      question: '使う人を選ぶ画面です。勝手に選ばないので、画面に出ている名前のうち、どの名前を使うか教えてください。', key: null, confidence: 0.99
    }, false, null);
  }
  if (/(上書き|置き換えますか|削除しますか|既定.*ブラウ|アクセスを許可|許可しますか|購入|支払い|注文を確定)/i.test(text)) {
    return task('choice', CORE_KNOWLEDGE, {
      status: 'clarify', targetId: null, action: 'none', instruction: '',
      question: 'この画面は選び方によって結果が変わります。HelpSysでは勝手に決めません。何をしたいか教えてください。', key: null, confidence: 0.99
    }, false, null);
  }
  return null;
}

function buildKnowledge(goal) {
  const extra = KNOWLEDGE_SECTIONS.filter(section => section.test.test(goal)).map(section => section.text).join('\n');
  return [CORE_KNOWLEDGE, extra].filter(Boolean).join('\n\n');
}

function detectSiteGoal(goal) {
  const lower = goal.toLowerCase();
  const intent = /(見たい|開いて|開きたい|行きたい|表示して|検索して|アクセス)/.test(goal);
  if (!intent) return null;
  return SITE_DEFINITIONS.find(site => site.aliases.some(alias => lower.includes(alias.toLowerCase()))) || null;
}

function detectLaunchApp(goal) {
  const lower = goal.toLowerCase();
  const launchIntent = /(開いて|開きたい|起動|立ち上げ|使いたい|出して|表示して)/.test(goal);
  if (!launchIntent) return null;
  return APP_DEFINITIONS.find(app => app.aliases.some(alias => lower.includes(alias.toLowerCase()))) || null;
}

function detectGenericLaunchApp(goal) {
  const match = goal.trim().match(/^(.{1,36}?)(?:を)?(?:開いて|開きたい|起動して|立ち上げて|使いたい)[。.!！ ]*$/);
  if (!match) return null;
  const display = match[1].trim();
  if (!display || /(ファイル|フォルダ|フォルダー|サイト|ホームページ|設定|画面|インターネット)/.test(display)) return null;
  return { id: 'generic', display, search: display, aliases: [display], processes: [] };
}

function isSimpleOpenGoal(goal, app) {
  return /(開いて|開きたい|起動して|立ち上げて|出して|表示して)[。.!！ ]*$/.test(goal.trim()) || goal.trim().length <= app.display.length + 10;
}

function isAppRunning(app, elements, systemContext) {
  const running = Array.isArray(systemContext?.runningApps) ? systemContext.runningApps.map(x => String(x).toLowerCase()) : [];
  if (app.processes?.some(name => running.includes(name))) return true;
  return elements.some(element => {
    const process = String(element.processName || '').toLowerCase();
    if (app.processes?.some(name => process === name || process.startsWith(`${name}.`))) return true;
    if (!['Window', 'Pane', 'Document', 'TitleBar'].includes(element.controlType)) return false;
    return matchesAppText(app, element.name);
  });
}

function findAppTarget(app, elements, systemContext, allowBackground = false) {
  if (!app) return null;
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  const candidates = elements.filter(element => element.interactable && element.enabled !== false && matchesAppText(app, element.name))
    .filter(element => allowBackground || isReachableFromForeground(element, foreground));
  if (!candidates.length) return null;
  return candidates.sort((a, b) => targetScore(b, app, foreground) - targetScore(a, app, foreground))[0];
}

function isReachableFromForeground(element, foreground) {
  const process = String(element.processName || '').toLowerCase();
  if (!foreground || foreground === 'explorer') return true;
  if (process === foreground) return true;
  if (/searchhost|startmenuexperiencehost/.test(process)) return true;
  return false;
}

function targetScore(element, app, foreground) {
  let score = 0;
  const name = String(element.name || '').toLowerCase();
  if (name === app.display.toLowerCase() || name === app.search.toLowerCase()) score += 100;
  if (app.aliases.some(alias => name === alias.toLowerCase())) score += 80;
  if (['ListItem', 'Button', 'MenuItem', 'Hyperlink'].includes(element.controlType)) score += 30;
  const process = String(element.processName || '').toLowerCase();
  if (process === foreground) score += 40;
  if (/searchhost|startmenuexperiencehost/.test(process)) score += 35;
  return score;
}

function elementRole(element) {
  const match = String(element?.automationId || '').match(/(?:^|\|)role:([a-z_]+)/i);
  return match ? match[1].toLowerCase() : '';
}

function findWindowsSearchField(elements) {
  return elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' &&
    elementRole(element) === 'windows_search') ||
    elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' &&
      /(検索|search)/i.test(`${element.name || ''} ${element.automationId || ''}`) && /searchhost|startmenuexperiencehost|explorer/i.test(element.processName || '')) || null;
}

function findBrowserAddressField(elements, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  return elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' &&
    String(element.processName || '').toLowerCase() === foreground && elementRole(element) === 'browser_address') ||
    elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' &&
      String(element.processName || '').toLowerCase() === foreground &&
      /(アドレス|address|location|omnibox|urlbar|url bar|web address)/i.test(`${element.name || ''} ${element.automationId || ''} ${element.className || ''}`)) || null;
}

function findWebSearchField(elements, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  return elements.find(element => element.interactable && element.enabled !== false && element.controlType === 'Edit' &&
    String(element.processName || '').toLowerCase() === foreground && elementRole(element) === 'web_search') || null;
}

function findFocusedEdit(elements, systemContext) {
  const foreground = String(systemContext?.foregroundProcess || '').toLowerCase();
  return elements.find(element => element.interactable && element.controlType === 'Edit' && element.focused && String(element.processName || '').toLowerCase() === foreground) || null;
}

function findOfficialSearchResult(site, elements) {
  const links = elements.filter(x => x.interactable && x.enabled !== false && x.controlType === 'Hyperlink' && matchesSiteText(site, x.name));
  for (const link of links.sort((a, b) => Number(a.y || 0) - Number(b.y || 0))) {
    const nearby = elements.filter(x => Math.abs(Number(x.y || 0) - Number(link.y || 0)) < 150 && Math.abs(Number(x.x || 0) - Number(link.x || 0)) < 700)
      .map(x => x.name).join(' ');
    if (/(広告|スポンサー|Sponsored|Ad\b)/i.test(nearby)) continue;
    if (site.domains.some(domain => nearby.toLowerCase().includes(domain.toLowerCase()))) return link;
  }
  return null;
}

function chooseRunningBrowser(systemContext) {
  const running = Array.isArray(systemContext?.runningApps) ? systemContext.runningApps.map(x => String(x).toLowerCase()) : [];
  if (running.includes('chrome')) return 'chrome';
  if (running.includes('msedge')) return 'msedge';
  return null;
}

function looksLikeNewTab(browser) {
  const title = String(browser?.windowTitle || '');
  const url = String(browser?.url || '');
  return !url || /新しいタブ|new tab/i.test(title) || /^(chrome|edge):\/\/newtab/i.test(url) || /^about:(newtab|blank)/i.test(url);
}

function isNeutralBrowserPage(domain, url) {
  if (!domain) return true;
  if (SEARCH_DOMAINS.some(d => domainMatches(domain, d))) return true;
  return /newtab|about:blank/i.test(url || '');
}

function urlLooksLikeSearch(url, query) {
  const lower = String(url || '').toLowerCase();
  const q = encodeURIComponent(query).toLowerCase();
  const plain = query.toLowerCase().replace(/\s+/g, '+');
  return lower.includes('search') && (lower.includes(q) || lower.includes(plain) || lower.includes(query.toLowerCase()));
}

function matchesAppText(app, value) {
  const text = String(value || '').toLowerCase();
  if (!text) return false;
  return text.includes(app.display.toLowerCase()) || app.aliases.some(alias => text.includes(alias.toLowerCase()));
}

function matchesSiteText(site, value) {
  const text = String(value || '').toLowerCase();
  return site.aliases.some(alias => text.includes(alias.toLowerCase())) || text.includes(site.display.toLowerCase());
}

function normalizeHost(value) {
  return String(value || '').trim().toLowerCase().replace(/^www\./, '').replace(/\.$/, '');
}

function domainMatches(host, domain) {
  const h = normalizeHost(host);
  const d = normalizeHost(domain);
  return h === d || h.endsWith(`.${d}`);
}

function domainMatchesAny(host, domains) {
  return domains.some(domain => domainMatches(host, domain));
}

function describeSystem(systemContext) {
  const browser = systemContext?.browser;
  return `前面アプリ=${systemContext?.foregroundProcess || '不明'} / タイトル=${systemContext?.foregroundTitle || '不明'} / タスクバー表示=${systemContext?.taskbarVisible === true ? 'はい' : 'いいえ'}${browser ? ` / ブラウザーのドメイン=${browser.domain || '不明'}` : ''}`;
}

function task(kind, knowledge, deterministic = null, forceVision = false, allowedTargetIds = null, app = null, running = false, site = null) {
  return { kind, knowledge, deterministic, forceVision, allowedTargetIds, app, running, site };
}

function notFound(confidence = 0) {
  const c = Number(confidence);
  return { status: 'not_found', targetId: null, action: 'none', instruction: '今の画面だけでは次の操作を安全に決められません。', question: null, key: null, confidence: Number.isFinite(c) ? Math.max(0, Math.min(1, c)) : 0 };
}

function visionNotFound(confidence = 0) {
  const c = Number(confidence);
  return { status: 'not_found', label: null, instruction: '安全に確認できる公式サイトの結果を見つけられませんでした。', question: null, x: 0, y: 0, width: 0, height: 0, confidence: Number.isFinite(c) ? Math.max(0, Math.min(1, c)) : 0, observedDomain: null, sponsored: false };
}