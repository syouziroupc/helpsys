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
const privacySentinel = read('src/HelpSys.Desktop/MainWindow.PrivacySentinel.cs');
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
assert(adapter.includes('PostJsonAsync') && adapter.includes('PostBytesAsync'), 'CloudAiAdapter must support JSON and transcription bytes.');
assert(adapter.includes('ValidateUnifiedApiBase'), 'Unified transport must validate its destination.');
assert(adapter.includes('uri.Host.Equals(approved.Host'), 'External traffic must be pinned to the approved HelpSys origin.');
assert(adapter.includes('AllowAutoRedirect = false'), 'Cloud transport must reject redirects.');
assert(adapter.includes('UseCookies = false'), 'Cloud transport must not persist cookies.');
assert(adapter.includes('UseProxy = false'), 'Cloud transport must not inherit an OS/user proxy.');
assert(adapter.includes('CheckCertificateRevocationList = true'), 'Cloud transport must check certificate revocation.');
assert(adapter.includes('NoStore = true') && adapter.includes('NoCache = true'), 'Cloud requests must request no-store/no-cache.');
assert(adapter.includes('MaxResponseBodyBytes = 1024 * 1024'), 'Cloud response bodies must remain bounded.');
assert(!adapter.includes('DangerousAcceptAnyServerCertificateValidator'), 'TLS validation must never be bypassed.');

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
  if (source.includes('CloudAiAdapter')) assert(allowedAdapterUsers.has(file), `Unexpected CloudAiAdapter user: ${file}`);
}

assert(desktopProject.includes('HELPSYS_SAFE_BUILD'), 'Unified HelpSys must compile the strict privacy profile by default.');
assert(!desktopProject.includes("'$(SafeBuild)' == 'true'"), 'Unified HelpSys must not rely on a separate Safe edition switch.');
assert(desktopProject.includes('<TieredPGO>true</TieredPGO>'), 'Unified build should keep profile-guided runtime optimization.');

assert(cloud.includes('PrivacyGate'), 'Cloud guidance must depend on PrivacyGate.');
assert(cloud.includes('PreflightPrivacy'), 'Cloud guidance must preflight before screenshot creation.');
assert(cloud.includes('PrivacyBlocked?.Invoke'), 'Blocked egress must signal Privacy Mode.');
assert(!cloud.includes('ImageDataUri'), 'CloudGuideService must not directly extract screenshot bytes.');
assert(speech.includes('CloudAiAdapter') && !speech.includes('HttpClient'), 'Speech transcription must use the shared adapter.');
assert(speech.includes('CloudTranscriptionAllowed => true'), 'Unified HelpSys should retain explicit voice input.');
assert(education.includes('CloudAiAdapter') && !education.includes('HttpClient'), 'Education AI must use the shared adapter.');

assert(gate.includes('image = frame.ImageDataUri'), 'Only PrivacyGate should convert a frame into an outbound image field.');
assert(gate.includes('PrivacyClassification.Unknown'), 'PrivacyGate must have UNKNOWN.');
assert(gate.includes('privacy_gate_failure'), 'PrivacyGate failures must fail closed.');
for (const code of ['password_control','otp_or_mfa','browser_secret_storage','financial_service','card_authentication'])
  assert(gate.includes(code), `PrivacyGate lost hard block ${code}.`);
assert(!gate.includes('HttpClient') && !gate.includes('Task<'), 'PrivacyGate hot path must remain local and synchronous.');
assert(!gate.includes('File.') && !gate.includes('Clipboard'), 'PrivacyGate must not read files or clipboard contents.');
assert(!gate.includes('value = x.Value'), 'Raw UI input values must never enter outbound payloads.');
assert(gate.includes('inputPresent = !x.Password && !string.IsNullOrEmpty(x.Value)'), 'Only input-presence state may leave the UI value boundary.');
assert(gate.includes('BearerRegex') && gate.includes('JwtRegex') && gate.includes('KnownApiKeyRegex'), 'Secret redaction patterns are required.');
assert(gate.includes('PassesLuhn'), 'Potential card numbers must be checked locally.');

assert(evidence.includes('Full URLs can contain session IDs'), 'Evidence must document why full URLs stay local.');
assert(/context\.Browser\?\.Domain,\s*null,\s*context\.Browser\?\.AddressFieldFocused/s.test(evidence), 'Cloud evidence must omit the full browser URL.');

assert(privacyUi.includes('HelpSys 統合版'), 'Privacy UI must identify the unified edition.');
assert(privacyUi.includes('プライバシー保護のため画面解析を一時停止中'), 'Privacy Mode must be visible.');
assert(privacyUi.includes('_sessionCts?.Cancel()'), 'Privacy Mode must cancel in-flight planning.');
assert(!privacyUi.includes('_activeRequest = null'), 'Privacy Mode must preserve the active task.');
assert(privacyUi.includes('TryResumePrivacyModeAsync'), 'Privacy Mode must support automatic resume.');
assert(xaml.includes('x:Name="PrivacyButton"'), 'The user must have a one-click screen-analysis pause control.');

assert(privacySentinel.includes('PrivacySentinelEventSystemForeground'), 'Privacy Sentinel must monitor foreground changes.');
assert(privacySentinel.includes('_cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>())'), 'Privacy Sentinel must use centralized preflight.');
assert(privacySentinel.includes('foreground_transition_unverified'), 'Foreground disagreement must become UNKNOWN.');
assert(privacySentinel.includes('PrivacySentinel_SuspendCloudAudio'), 'Dangerous screens must cancel cloud speech.');

assert(qualityUi.includes('_cloudGuide.PreflightPrivacy'), 'Quality guidance must preflight privacy before screenshot creation.');
assert(qualityUi.indexOf('_cloudGuide.PreflightPrivacy') < qualityUi.indexOf('_screenCapture.CaptureAsync'), 'Quality preflight must precede screenshot capture.');
assert(recoveryUi.includes('_cloudGuide.PreflightPrivacy'), 'Recovery guidance must preflight privacy.');
assert(recoveryUi.indexOf('_cloudGuide.PreflightPrivacy') < recoveryUi.indexOf('CaptureQualityFrameAsync'), 'Recovery preflight must precede capture.');

assert(watcher.includes('AutomationFocusChangedEventHandler'), 'Meaningful focus changes must remain observable locally.');
assert(watcher.includes('StructureChangedEventHandler'), 'Meaningful structure changes must remain observable locally.');
assert(!watcher.includes('ValuePattern.ValueProperty'), 'Per-character input values must not trigger the watcher.');

assert(capture.includes('CaptureSensitiveInputBounds'), 'Screenshot privacy scan must remain independent of ranked candidates.');
assert(capture.includes('if (current.IsPassword) return true;'), 'Password controls must remain redacted.');
assert(capture.includes('return current.ControlType == ControlType.Edit || current.ControlType == ControlType.ComboBox;'), 'Visible input controls must remain redacted.');
assert(!capture.includes('valuePattern.Current.Value'), 'Screenshot redaction must not read raw input values.');
for (const requiredPattern of ['VisibleEmailRegex','VisibleJapanesePhoneRegex','VisiblePostalCodeRegex','VisibleBearerRegex','VisibleJwtRegex','VisibleApiKeyRegex','VisibleCardNumberRegex','VisibleSensitiveUrlRegex'])
  assert(capture.includes(requiredPattern), `Screenshot redaction is missing ${requiredPattern}.`);

assert(telemetry.includes('NullPrivacySafeTelemetrySink'), 'Telemetry must have a no-op default sink.');
assert(telemetry.includes('HELPSYS_TELEMETRY'), 'Telemetry must require explicit opt-in.');
assert(diagnostics.includes('RawScreenPersistenceAllowed = false'), 'Strict unified profile must forbid raw-screen persistence.');

assert(serverGuard.includes('privacyHardenedEnv'), 'Production Worker must use the no-storage wrapper.');
assert(serverGuard.includes('{ ...options, store: false }'), 'Workers AI text/vision inference must force store:false.');
assert(serverGuard.includes("url.pathname === '/v1/transcribe'"), 'ASR must remain separated from the GLM storage-option wrapper.');

console.log('HelpSys unified privacy architecture contract passed.');
