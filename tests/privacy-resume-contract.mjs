import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const sentinel = read('src/HelpSys.Desktop/MainWindow.PrivacySentinel.cs');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const session = read('src/HelpSys.Desktop/Services/GuidanceSessionController.cs');

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

assert(session.includes('AbortCurrentOperation(GuidanceSessionState nextState)'), 'Session controller needs a distinct explicit-abort boundary.');
const abortStart = session.indexOf('AbortCurrentOperation(GuidanceSessionState nextState)');
const abortBody = session.slice(abortStart, abortStart + 650);
assert(abortBody.includes('_generation++'), 'Explicit abort must invalidate the old generation.');
assert(abortBody.includes('_plannerInFlight = false'), 'Explicit abort must release the stale planner slot.');
assert(abortBody.includes('_plannerOperationGeneration = 0'), 'Explicit abort must clear the old planner generation.');
assert(session.includes('public long Invalidate(GuidanceSessionState nextState)') &&
       !session.slice(session.indexOf('public long Invalidate(GuidanceSessionState nextState)'), abortStart).includes('_plannerInFlight = false'),
       'Ordinary invalidation must not silently become an abort and permit overlapping live replans.');

const enterPause = privacy.indexOf('private void EnterPrivacyMode');
const pauseCancel = privacy.indexOf('_sessionCts?.Cancel()', enterPause);
const pauseAbort = privacy.indexOf('_sessionState.AbortCurrentOperation(GuidanceSessionState.Idle)', enterPause);
assert(pauseCancel >= 0 && pauseAbort > pauseCancel, 'Privacy pause must cancel the old token before releasing its planner slot.');

const resumeAssessment = privacy.indexOf('var assessment = _cloudGuide.PrivacyGate.EvaluateState');
const resumeAbort = privacy.indexOf('_sessionState.AbortCurrentOperation(GuidanceSessionState.Idle)', resumeAssessment);
const newSessionToken = privacy.indexOf('_sessionCts = new CancellationTokenSource()', resumeAssessment);
const advance = privacy.indexOf('await AdvanceGuideAsync();', resumeAssessment);
assert(resumeAssessment >= 0 && resumeAbort > resumeAssessment,
  'Privacy resume may release a planner slot only after the current safe screen passes the deeper local gate.');
assert(newSessionToken > resumeAbort && advance > newSessionToken,
  'Privacy resume must establish a fresh token after abort and before starting the replacement planner.');

console.log('HelpSys Privacy Mode pending-request/resume contract passed.');
