import fs from 'node:fs';

// Field regression contract for browser/Office recognition, latency, and stable same-app guidance.
const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.StableGuidance.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const privacy = fs.readFileSync('src/HelpSys.Desktop/Services/PrivacyGate.cs', 'utf8');

function must(cond, msg) { if (!cond) throw new Error(msg); }

must(scanner.includes('TextPattern.Pattern') && scanner.includes('ReadBoundedVisibleText'), 'visible TextPattern evidence must be collected');
must(scanner.includes('DataItem') && scanner.includes('Document'), 'Office/document context types must be retained');
must(privacy.includes('x.Interactable ? 180 : 360'), 'sanitized context text budget must be larger than control labels');
must(cloud.includes('TimeSpan.FromSeconds(6)') && cloud.includes('attempt < 1'), 'cloud attempt latency must be bounded');
must(cloud.includes('expected.Browser?.Domain') && !cloud.includes('var expectedUrl = expected.Browser?.Url'), 'planning staleness must use browser domain, not same-domain URL churn');
must(quality.includes('TryFastStructuredPlanAsync'), 'fast structured/text-first planning path must exist');
must(!quality.includes('!HasSameCaptureIdentity(systemContext, afterCaptureContext) || HasSystemTransitionV3(systemContext, afterCaptureContext)'), 'same-window content transitions must not invalidate capture identity');
must(live.includes('_sessionState.PlannerInFlight && !hardChange'), 'live same-app churn must not cancel an in-flight planner');
must(live.includes('before.Browser?.Domain') && !live.includes('var beforeUrl = before.Browser?.Url'), 'live hard change must not use same-domain URL changes');
must(!live.includes('focus={x.Focused}'), 'ordinary focus movement must not be treated as semantic route change');
must(!main.includes('通信回線が切れているとは断定せず'), 'confusing service failure wording must be removed');
console.log('recognition/latency/state regression contract passed');
