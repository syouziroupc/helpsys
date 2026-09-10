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
const privacyUi = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
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

for (const file of csharpFiles('src')) {
  if (file === 'src/Shared/CloudAiAdapter.cs') continue;
  const source = read(file);
  assert(!source.includes('new HttpClient'), `Direct HttpClient construction bypasses the cloud boundary: ${file}`);
  assert(!source.includes('new HttpRequestMessage'), `Direct HTTP request construction bypasses the cloud boundary: ${file}`);
}

assert(cloud.includes('PrivacyGate'), 'Cloud guidance must depend on PrivacyGate.');
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

assert(privacyUi.includes('プライバシー保護のため画面解析を一時停止中'), 'Privacy Mode must be visible to the user.');
assert(privacyUi.includes('_sessionCts?.Cancel()'), 'Privacy Mode must cancel in-flight planning.');
assert(!privacyUi.includes('_activeRequest = null'), 'Privacy Mode must preserve the active task so it can resume.');
assert(privacyUi.includes('TryResumePrivacyModeAsync'), 'Privacy Mode must support automatic safe-screen resumption.');
assert(xaml.includes('x:Name="PrivacyButton"'), 'The user must have a one-click screen-analysis pause control.');

assert(watcher.includes('AutomationFocusChangedEventHandler'), 'Meaningful focus changes must remain observable locally.');
assert(watcher.includes('StructureChangedEventHandler'), 'Meaningful window/UI structure changes must remain observable locally.');
assert(!watcher.includes('ValuePattern.ValueProperty'), 'Per-character input value changes must not trigger the live watcher.');

assert(capture.includes('CaptureSensitiveInputBounds'), 'Screenshot privacy scan must remain independent from the ranked candidate list.');
assert(capture.includes('if (current.IsPassword) return true;'), 'Password controls must remain unconditionally redacted in local capture.');
assert(capture.includes('入力欄を安全に確認できないため、画面画像は送信しません'), 'Capture privacy uncertainty must fail closed.');

assert(desktopProject.includes("'$(SafeBuild)' == 'true'"), 'Desktop project must expose a separate compile-time Safe build.');
assert(desktopProject.includes('HELPSYS_SAFE_BUILD'), 'Safe build must define its fixed privacy profile symbol.');

console.log('HelpSys privacy architecture contract passed.');
