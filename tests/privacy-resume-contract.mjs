import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const sentinel = read('src/HelpSys.Desktop/MainWindow.PrivacySentinel.cs');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');

assert(sentinel.includes('_privacySentinelPendingInput'), 'Blocked Guide/Answer input must have a local-only pending slot.');
assert(sentinel.includes('_privacySentinelPendingClarification'), 'Blocked clarification input must retain its semantic role locally.');
assert(sentinel.includes('PrivacySentinel_StagePendingInput'), 'Privacy Sentinel must stage typed intent when it blocks the original routed handler.');
assert(sentinel.includes('PrivacySentinel_RestorePendingInputForResume'), 'Privacy Sentinel must expose a local restore path for safe-screen resume.');
assert(sentinel.includes('EndSession();') && sentinel.includes('_activeRequest = pending;'), 'A fresh blocked request must be rebuilt as a normal local session before resume.');
assert(sentinel.includes('_activeRequest += $"\\n利用者からの追加回答: {pending}"'), 'Blocked clarification answers must remain attached to the existing task.');
assert(sentinel.includes('no UIA scan, screenshot, microphone or network work happens'), 'Pending request staging must document that it is local-only.');
assert(!/PrivacySentinel_StagePendingInput[\s\S]{0,900}(?:HttpClient|CloudAiAdapter|CaptureAsync\(|RecognizeOnceAsync)/.test(sentinel),
  'Pending input staging must not perform cloud, screenshot or microphone work.');

const preflight = privacy.indexOf('var contextAssessment = _cloudGuide.PreflightPrivacy');
const restore = privacy.indexOf('PrivacySentinel_RestorePendingInputForResume();');
const candidateScan = privacy.indexOf('_scanner.CaptureCandidatesForProcessAsync', restore);
assert(preflight >= 0 && restore > preflight, 'Pending request must not be restored until the current foreground passes the context-only Privacy Gate.');
assert(candidateScan > restore, 'UIA candidate scanning must remain after local pending-request restoration and safe foreground verification.');
assert(privacy.includes('DispatcherTimer _privacyResumeTimer'), 'Privacy Mode must retain its low-frequency missed-event resume fallback.');
assert(privacy.includes('Interval = TimeSpan.FromMilliseconds(900)'), 'Privacy Mode resume polling must remain low-frequency.');

console.log('HelpSys Privacy Mode pending-request/resume contract passed.');
