import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const sentinel = read('src/HelpSys.Desktop/MainWindow.PrivacySentinel.cs');
const privacy = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const session = read('src/HelpSys.Desktop/Services/GuidanceSessionController.cs');

// Guide/Answer startup is local-only. A transient foreground race must not swallow the routed
// button/Enter event before the request exists; every actual cloud egress is gated later.
const guideBranchStart = sentinel.indexOf('if (button.Name is "GuideButton" or "AnswerButton")');
const guideBranch = guideBranchStart >= 0 ? sentinel.slice(guideBranchStart, guideBranchStart + 420) : '';
assert(guideBranchStart >= 0, 'Privacy Sentinel must explicitly distinguish local Guide/Answer startup.');
assert(guideBranch.includes('PrivacySentinel_ClearPendingInput();') && guideBranch.includes('return;'),
  'Guide/Answer startup must continue through the normal local handler without an early cloud preflight.');
assert(!guideBranch.includes('e.Handled = true'),
  'Privacy Sentinel must not swallow Guide/Answer button clicks before the request session starts.');

const enterStart = sentinel.indexOf('private static void PrivacySentinel_TextBoxKeyDown');
const enterBody = enterStart >= 0 ? sentinel.slice(enterStart, enterStart + 620) : '';
assert(enterStart >= 0 && enterBody.includes('RequestBox') && enterBody.includes('AnswerBox'),
  'Enter-key guidance startup must remain covered by the sentinel integration.');
assert(!enterBody.includes('e.Handled = true'),
  'Privacy Sentinel must not swallow Request/Answer Enter before the local guidance path runs.');

assert(sentinel.includes('PrivacySentinel_RestorePendingInputForResume'),
  'Privacy resume must retain backward-compatible local pending-input restoration.');
assert(sentinel.includes('PrivacySentinel_SuspendCloudAudio'),
  'Foreground uncertainty must still suspend active cloud audio immediately.');
assert(sentinel.includes('await Task.Delay(140)') && sentinel.includes('await Task.Delay(180)'),
  'Transient foreground disagreement must be rechecked before escalating to UNKNOWN Privacy Mode.');
assert(sentinel.includes('foreground_transition_unverified'),
  'Persistently unverified foreground transitions must still fail closed.');

const preflight = privacy.indexOf('var contextAssessment = _cloudGuide.PreflightPrivacy');
const restore = privacy.indexOf('PrivacySentinel_RestorePendingInputForResume();');
const candidateScan = privacy.indexOf('_scanner.CaptureCandidatesForProcessAsync', restore);
assert(preflight >= 0 && restore > preflight, 'Privacy resume must not restore local session state until the current foreground passes the context-only Privacy Gate.');
assert(candidateScan > restore, 'UIA candidate scanning must remain after safe foreground verification.');
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

console.log('HelpSys Privacy Mode startup/resume contract passed.');
