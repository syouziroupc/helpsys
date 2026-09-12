import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const speech = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const privacyUi = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const diagnostics = read('src/HelpSys.Desktop/Services/DiagnosticModePolicy.cs');
const adapter = read('src/Shared/CloudAiAdapter.cs');
const project = read('src/HelpSys.Desktop/HelpSys.Desktop.csproj');
const release = read('.github/workflows/release.yml');

assert(project.includes('HELPSYS_SAFE_BUILD'), 'Unified build must compile the strict privacy profile.');
assert(!project.includes("'$(SafeBuild)' == 'true'"), 'Separate Safe build switch must not return.');
assert(project.includes("'$(TestBuild)' == 'true'"), 'Loopback capability must require an explicit CI-only TestBuild.');
assert(project.includes('HELPSYS_TEST_BUILD'), 'CI-only test builds must have a distinct compile-time symbol.');
assert(project.includes('<AssemblyName>HelpSys</AssemblyName>'), 'Unified executable must remain HelpSys.exe.');
assert(project.includes('<TieredPGO>true</TieredPGO>'), 'Unified build should retain TieredPGO.');

assert(speech.includes('CloudTranscriptionAllowed => true'), 'Unified build must retain explicit voice input.');
assert(speech.includes('MicrophoneCoordinator.BeginForegroundCapture'), 'Voice capture must retain microphone coordination.');
assert(speech.includes('InitialNoiseFloor') && speech.includes('continueThreshold'), 'Adaptive endpointing must remain enabled.');

assert(privacyUi.includes('HelpSys 統合版'), 'UI must identify the unified secure edition.');
assert(!privacyUi.includes('VoiceButton.IsEnabled = false'), 'Unified edition must not disable explicit voice input.');
assert(privacyUi.includes('PrivacySentinel_SuspendCloudAudio'), 'Privacy Mode must still stop active cloud audio on dangerous screens.');
assert(privacyUi.includes('DispatcherTimer _privacyResumeTimer'), 'Automatic privacy resume fallback is required.');

assert(adapter.includes('ValidateUnifiedApiBase'), 'Unified transport must validate endpoints.');
assert(adapter.includes('#if HELPSYS_TEST_BUILD'), 'Loopback transport must be compiled only into CI/local test builds.');
assert(adapter.includes('配布版HelpSysではloopback AI接続先を許可しません'), 'Distributed build must explicitly reject loopback endpoints.');
assert(adapter.includes('uri.Host.Equals(approved.Host'), 'External endpoint host must match the approved origin exactly.');
assert(adapter.includes('uri.Port == approved.Port'), 'External endpoint port must match the approved origin.');
assert(adapter.includes('AllowAutoRedirect = false'), 'Cloud transport must never follow redirects.');
assert(adapter.includes('UseCookies = false'), 'Cloud transport must not persist cookies.');
assert(adapter.includes('UseProxy = false'), 'Cloud transport must not inherit OS/user proxy configuration.');
assert(adapter.includes('CheckCertificateRevocationList = true'), 'Cloud transport must check TLS certificate revocation.');
assert(adapter.includes('NoStore = true') && adapter.includes('NoCache = true'), 'Cloud requests must request no-store/no-cache.');
assert(adapter.includes('MaxResponseBodyBytes = 1024 * 1024'), 'Cloud response body must stay bounded.');
assert(!adapter.includes('DangerousAcceptAnyServerCertificateValidator'), 'TLS certificate validation must never be bypassed.');

assert(diagnostics.includes('RawScreenPersistenceAllowed = false'), 'Strict unified profile must forbid raw screen persistence.');
assert(release.includes('HelpSys-latest-win-x64.zip'), 'Unified release must keep a stable latest alias.');
assert(release.includes('HelpSys-Unified-$shortSha-win-x64.zip'), 'Unified release must keep a traceable per-commit asset.');
assert(!release.includes('HelpSys-Safe-latest-win-x64.zip'), 'Separate Safe asset must not reappear.');
assert(!release.includes('-p:TestBuild=true'), 'Public release must never publish the loopback-enabled CI test build.');

console.log('HelpSys unified secure-edition contract passed.');
