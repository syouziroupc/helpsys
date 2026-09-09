import { validateQualityDecision } from './quality-guide.js';
import { buildWindowsTaskContext } from './windows-knowledge.js';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const baseTarget = {
  status: 'target', action: 'left_click', instruction: 'この候補を1回押してください。', question: null, key: null,
  confidence: 0.97, x: 10, y: 10, width: 100, height: 30,
  screenConfirmed: true, visualEvidence: '検索結果が画面に見える', observedDomain: null, sponsored: false
};

const searchContext = {
  foregroundProcess: 'chrome', foregroundProcessId: 10, runningApps: ['chrome'],
  browser: { domain: 'google.com', url: 'https://www.google.com/search?q=YouTube', windowTitle: 'YouTube - Google Search' }
};
const searchElements = [
  { id: 'official', name: 'YouTube', controlType: 'Hyperlink', processName: 'chrome', interactable: true, enabled: true, x: 100, y: 200, width: 300, height: 30 },
  { id: 'official-domain', name: 'youtube.com', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true, x: 110, y: 230, width: 260, height: 20 },
  { id: 'fake', name: 'YouTube 動画を見る', controlType: 'Hyperlink', processName: 'chrome', interactable: true, enabled: true, x: 100, y: 420, width: 300, height: 30 },
  { id: 'fake-domain', name: 'youtube-login.example', controlType: 'Text', processName: 'chrome', interactable: false, enabled: true, x: 110, y: 450, width: 260, height: 20 }
];
const searchTask = buildWindowsTaskContext('YouTubeを開いて', searchElements, [], searchContext);

let value = validateQualityDecision({ ...baseTarget, targetId: 'fake' }, searchElements, searchTask, false);
assert(value.status === 'not_found', 'Known-site structured target outside the official allow-list must be rejected.');

value = validateQualityDecision({ ...baseTarget, targetId: 'official' }, searchElements, searchTask, false);
assert(value.status === 'target' && value.targetId === 'official', 'Verified official search result must remain actionable.');

value = validateQualityDecision({ ...baseTarget, targetId: 'fake' }, searchElements, searchTask, true);
assert(value.status === 'not_found', 'Recovery mode must not relax official-site target identity.');

const wrongDomainContext = {
  foregroundProcess: 'chrome', foregroundProcessId: 11, runningApps: ['chrome'],
  browser: { domain: 'example.com', url: 'https://example.com/youtube', windowTitle: 'YouTube videos' }
};
const wrongDomainTask = buildWindowsTaskContext('YouTubeを開いて', [], [], wrongDomainContext);
value = validateQualityDecision({
  status: 'done', targetId: null, action: 'none', instruction: 'YouTubeを開けました。', question: null, key: null,
  confidence: 0.99, x: 0, y: 0, width: 0, height: 0,
  screenConfirmed: true, visualEvidence: 'YouTubeという文字が見える', observedDomain: 'example.com', sponsored: false
}, [], wrongDomainTask, false);
assert(value.status === 'not_found', 'Known-site completion must require current official browser domain, not visual branding alone.');

const visualSearchElements = [
  { id: 'fake', name: 'YouTube', controlType: 'Hyperlink', processName: 'chrome', interactable: true, enabled: true, x: 100, y: 300, width: 300, height: 30 }
];
const visualSearchTask = buildWindowsTaskContext('YouTubeを開いて', visualSearchElements, [], searchContext);
assert(visualSearchTask.forceVision === true, 'Search results without a structured official-domain match should require visual verification.');

const visualBase = {
  ...baseTarget,
  targetId: 'vision-target',
  x: 100, y: 200, width: 300, height: 80,
  visualEvidence: '検索結果のリンクと表示ドメインが見える'
};
value = validateQualityDecision({ ...visualBase, observedDomain: 'youtube-login.example' }, visualSearchElements, visualSearchTask, false);
assert(value.status === 'not_found', 'Visual known-site target with a lookalike domain must be rejected.');

value = validateQualityDecision({ ...visualBase, observedDomain: 'youtube.com' }, visualSearchElements, visualSearchTask, false);
assert(value.status === 'target' && value.targetId === 'vision-target', 'Visual known-site target with the official domain should remain actionable.');

value = validateQualityDecision({ ...visualBase, observedDomain: 'youtube.com', sponsored: true }, visualSearchElements, visualSearchTask, false);
assert(value.status === 'not_found', 'Sponsored visual result must remain rejected for known-site navigation.');

console.log('HelpSys deep-audit site safety self-test passed.');
