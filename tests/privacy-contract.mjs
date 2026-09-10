import fs from 'node:fs';
import path from 'node:path';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const gate = read('src/HelpSys.Desktop/Services/PrivacyGate.cs');
const adapter = read('src/Shared/CloudAiAdapter.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const speech = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const education = read('src/HelpSys.Education/EducationGuideService.cs');
const capture = read('src/HelpSys.Desktop/Services/ScreenCaptureService.cs');
const watcher = read('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs');
const evidence = read('src/HelpSys.Desktop/Services/GuidanceEvidenceService.cs');
const privacyUi = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const qualityUi = read('src/HelpSys.Desktop/MainWindow.QualityFirst.cs');
const recoveryUi = read('src/HelpSys.Desktop/MainWindow.RouteRecovery.cs');
const telemetry = read('src/HelpSys.Desktop/Services/PrivacySafeTelemetry.cs');
const diagnostics = read('src/HelpSys.Desktop/Services/DiagnosticModePolicy.cs');
const serverGuard = read('worker/reliability-v4-guard.js');
const xaml = read('src/HelpSys.Desktop/MainWindow.xaml');
const desktopProject = read('src/HelpSys.Desktop/HelpSys.Desktop.csproj');

function csharpFiles(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...csharpFiles(full));
    else if (entry.isFile() && entry.name.endsWith('.cs')) out.push(full.replaceAll('\\', '/'));
  }
  return out;
}

assert(adapter.includes('sealed class CloudAiAdapter'), 'The single cloud AI adapter is missing.');
assert(adapter.includes('HttpClient'), 'CloudAiAdapter must own the outbound HTTP client.');
assert(adapter.includes('PostJsonAsync') && adapter.includes('PostBytesAsync'), 'CloudAiAdapter must support guidance/education JSON and transcription bytes.');
assert(adapter.includes('Uri.UriSchemeHttps'), 'External cloud endpoints must require HTTPS.');
assert(adapter.includes('uri.IsLoopback') && adapter.includes('Uri.UriSchemeHttp'), 'Plain HTTP may only be retained for loopback development.');
assert(adapter.includes('must not contain query parameters'), 'Cloud request paths must reject query strings/fragments that could carry secrets.');

const allowedAdapterUsers = new Set([
  'src/Shared/CloudAiAdapter.cs',
  'src/HelpSys.Desktop/Services/CloudGuideService.cs',
  'src/HelpSys.Desktop/Services/SpeechInputService.cs',
  'src/HelpSys.Education/EducationGuideService.cs'
]);
for (const file of csharpFiles('src')) {
  const source = read(file);
  if (file !== 'src/Shared/CloudAiAdapter.cs') {
    assert(!source.includes('new HttpClient'), `Direct HttpClient construction bypasses the cloud boundary: ${file}`);
    assert(!source.includes('new HttpRequestMessage'), `Direct HTTP request construction bypasses the cloud boundary: ${file}`);
  }
  if (source.includes('CloudAiAdapter'))
    assert(allowedAdapterUsers.has(file), `Unexpected CloudAiAdapter user could bypass the egress boundary: ${file}`);
}

assert(cloud.includes('PrivacyGate'), 'Cloud guidance must depend on PrivacyGate.');
assert(cloud.includes('PreflightPrivacy'), 'Cloud guidance must expose a local preflight before screenshot creation.');
assert(cloud.includes('PrivacyBlocked?.Invoke'), 'Blocked egress must signal Privacy Mode before control returns to the planner.');
assert(!cloud.includes('ImageDataUri'), 'CloudGuideService must not directly extract screenshot bytes.');
assert(speech.includes('CloudAiAdapter') && !speech.includes('HttpClient'), 'Speech transcription must use the shared cloud adapter.');
assert(education.includes('CloudAiAdapter') && !education.includes('HttpClient'), 'Education AI must use the shared cloud adapter.');

assert(gate.includes('image = frame.ImageDataUri'), 'Only PrivacyGate should turn a captured frame into an outbound image field.');
assert(gate.includes('PrivacyClassification.Unknown'), 'PrivacyGate must have an explicit UNKNOWN state.');
assert(gate.includes('privacy_gate_failure'), 'PrivacyGate failures must fail closed.');
assert(gate.includes('password_control'), 'Password controls must hard-block cloud analysis.');
assert(gate.includes('otp_or_mfa'), 'OTP/MFA screens must hard-block cloud analysis.');
assert(gate.includes('browser_secret_storage'), 'Cookie/Storage screens must hard-block cloud analysis.');
assert(gate.includes('financial_service'), 'Financial authentication/trading screens must be recognized.');
assert(gate.includes('card_authentication'), 'Card authentication screens must be recognized.');
assert(gate.includes('HELPSYS_SAFE_BUILD'), 'Safe edition policy must be compile-time fixed.');
assert(!gate.includes('HttpClient') && !gate.includes('Task<'), 'PrivacyGate hot path must stay local and synchronous.');
assert(!gate.includes('File.') && !gate.includes('Clipboard'), 'PrivacyGate must not read files or clipboard contents.');
assert(!gate.includes('value = x.Value'), 'Raw UI input values must never be copied into outbound payloads.');
assert(gate.includes('inputPresent = !x.Password && !string.IsNullOrEmpty(x.Value)'), 'Only boolean input-presence state may leave the UI candidate value boundary.');
assert(gate.includes('BearerRegex') && gate.includes('JwtRegex') && gate.includes('KnownApiKeyRegex'), 'Outbound free-form text must redact common token/API-key forms.');
assert(gate.includes('PassesLuhn'), 'Potential payment-card numbers must be checked locally before cloud use.');

assert(evidence.includes('Full URLs can contain session IDs'), 'Guidance evidence must document why full URLs are local-only.');
assert(/context\.Browser\?\.Domain,\s*null,\s*context\.Browser\?\.AddressFieldFocused/s.test(evidence), 'Cloud evidence must omit the full browser URL while preserving domain-level context.');

assert(privacyUi.includes('プライバシー保護のため画面解析を一時停止中'), 'Privacy Mode must be visible to the user.');
assert(privacyUi.includes('_sessionCts?.Cancel()'), 'Privacy Mode must cancel in-flight planning.');
assert(!privacyUi.includes('_activeRequest = null'), 'Privacy Mode must preserve the active task so it can resume.');
assert(privacyUi.includes('TryResumePrivacyModeAsync'), 'Privacy Mode must support automatic safe-screen resumption.');
assert(privacyUi.includes('Dispatcher.BeginInvoke(new Action(SetPrivacyAwareStartupState))'), 'Privacy-aware Safe startup state must survive later generic Loaded handlers.');
assert(xaml.includes('x:Name="PrivacyButton"'), 'The user must have a one-click screen-analysis pause control.');

assert(qualityUi.includes('_cloudGuide.PreflightPrivacy'), 'Normal quality guidance must preflight privacy before screenshot creation.');
assert(qualityUi.indexOf('_cloudGuide.PreflightPrivacy') < qualityUi.indexOf('_screenCapture.CaptureAsync'), 'Privacy preflight must happen before quality screenshot creation.');
assert(recoveryUi.includes('_cloudGuide.PreflightPrivacy'), 'Recovery guidance must preflight privacy before screenshot creation.');
assert(recoveryUi.indexOf('_cloudGuide.PreflightPrivacy') < recoveryUi.indexOf('CaptureQualityFrameAsync'), 'Recovery privacy preflight must happen before recovery screenshot creation.');

assert(watcher.includes('AutomationFocusChangedEventHandler'), 'Meaningful focus changes must remain observable locally.');
assert(watcher.includes('StructureChangedEventHandler'), 'Meaningful window/UI structure changes must remain observable locally.');
assert(!watcher.includes('ValuePattern.ValueProperty'), 'Per-character input value changes must not trigger the live watcher.');

assert(capture.includes('CaptureSensitiveInputBounds'), 'Screenshot privacy scan must remain independent from the ranked candidate list.');
assert(capture.includes('if (current.IsPassword) return true;'), 'Password controls must remain unconditionally redacted in local capture.');
assert(capture.includes('return current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox;'), 'Every visible Edit/ComboBox must be redacted regardless of its current value.');
assert(!capture.includes('valuePattern.Current.Value'), 'Screenshot redaction must not read raw input values.');
assert(capture.includes('入力欄を安全に確認できないため、画面画像は送信しません'), 'Capture privacy uncertainty must fail closed.');
assert(capture.includes('FullMonitorShellProcesses'), 'Only shell operation surfaces should retain full-monitor capture.');
assert(capture.includes('return new CaptureArea(left, top, width, height);'), 'Normal application capture must be clipped to the active window.');
assert(capture.includes('data minimization'), 'Active-window capture minimization must remain an explicit privacy invariant.');

assert(telemetry.includes('sealed record PrivacySafeTelemetryEvent'), 'A fixed long-term telemetry allowlist type is required.');
for (const forbidden of ['Screenshot', 'Ocr', 'BrowserUrl', 'DocumentBody', 'MailBody', 'InputValue', 'Cookie', 'Token', 'Password'])
  assert(!telemetry.includes(`${forbidden},`), `Telemetry allowlist must not contain ${forbidden}.`);
assert(telemetry.includes('NullPrivacySafeTelemetrySink'), 'Telemetry must have a no-op default sink.');
assert(telemetry.includes('HELPSYS_TELEMETRY'), 'Telemetry must require explicit opt-in.');

assertDiagnosticPolicy();
function assertDiagnosticPolicy() {
  assert(diagnostics.includes('HELPSYS_DIAGNOSTIC_MODE'), 'Diagnostic mode must require explicit opt-in.');
  assert(diagnostics.includes('HELPSYS_DIAGNOSTIC_RAW_SCREEN'), 'Raw diagnostic screen persistence must require a second explicit opt-in.');
  assert(diagnostics.includes('I_UNDERSTAND_RAW_SCREEN_DATA'), 'Raw screen diagnostic opt-in must be intentionally difficult to enable accidentally.');
  assert(diagnostics.includes('#if HELPSYS_SAFE_BUILD'), 'Safe build must compile out diagnostic raw-screen persistence.');
  assert(!diagnostics.includes('File.') && !diagnostics.includes('StreamWriter'), 'Diagnostic policy itself must not persist anything automatically.');
}

assert(serverGuard.includes('privacyHardenedEnv'), 'Production Worker must wrap text/vision inference in the no-storage environment.');
assert(serverGuard.includes('{ ...options, store: false }'), 'Text/vision Workers AI inference must force store:false server-side.');
assert(serverGuard.includes("url.pathname === '/v1/transcribe'"), 'ASR must remain separated from the GLM storage-option wrapper.');

assert(desktopProject.includes("'$(SafeBuild)' == 'true'"), 'Desktop project must expose a separate compile-time Safe build.');
assert(desktopProject.includes('HELPSYS_SAFE_BUILD'), 'Safe build must define its fixed privacy profile symbol.');

console.log('HelpSys privacy architecture contract passed.');
