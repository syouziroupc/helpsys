import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const speech = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const privacyUi = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const diagnostics = read('src/HelpSys.Desktop/Services/DiagnosticModePolicy.cs');
const adapter = read('src/Shared/CloudAiAdapter.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const project = read('src/HelpSys.Desktop/HelpSys.Desktop.csproj');
const safeConfiguredSmoke = read('tests/safe_configured_smoke.ps1');

assert(project.includes('HELPSYS_SAFE_BUILD'), 'Safe build compile-time symbol is required.');
assert(project.includes('HELPSYS_SAFE_TEST_BUILD'), 'Loopback capability must exist only behind a distinct Safe test-build symbol.');
assert(project.includes("'$(SafeBuild)' == 'true' and '$(SafeTestBuild)' == 'true'"), 'Safe test capability must require both SafeBuild and SafeTestBuild.');
assert(speech.includes('#if HELPSYS_SAFE_BUILD'), 'Safe build must compile a distinct cloud speech policy.');
assert(speech.includes('return false;') && speech.includes('CloudTranscriptionAllowed'), 'Safe build must report cloud transcription as disabled.');
assert(speech.indexOf('if (!CloudTranscriptionAllowed)') < speech.indexOf('MicrophoneCoordinator.BeginForegroundCapture'), 'Safe cloud-speech block must run before microphone capture starts.');
assert(speech.includes('安全版では音声データをクラウドへ送信しません'), 'Safe speech refusal must explain the privacy reason.');

assert(privacyUi.includes('VoiceButton.IsEnabled = false'), 'Safe UI must disable the cloud voice button.');
assert(privacyUi.includes('CommanderButton.IsEnabled = false'), 'Safe UI must disable Commander cloud dictation.');
assert(privacyUi.includes('_commander.SetEnabled(false)'), 'Safe UI must turn off Commander wake monitoring after initialization.');
assert(privacyUi.includes('音声のクラウド送信も無効です'), 'Safe startup status must disclose that cloud voice is disabled.');
assert(privacyUi.includes('!_cloudGuide.CloudEndpointConfigured'), 'Safe UI must remain paused when no reviewed cloud endpoint is configured.');
assert(privacyUi.includes('画像を取得・送信しません'), 'Safe UI must disclose that an unconfigured endpoint prevents image acquisition and egress.');

assert(adapter.includes('#if HELPSYS_SAFE_BUILD'), 'Cloud adapter must compile a distinct Safe endpoint policy.');
assert(adapter.includes('HELPSYS_SAFE_API_BASE'), 'Safe build must require a dedicated reviewed endpoint variable.');
assert(adapter.includes('HELPSYS_SAFE_API_KEY'), 'Safe build must use a dedicated Safe API key variable.');
assert(adapter.includes('string.IsNullOrWhiteSpace(resolvedBase) ? null'), 'Safe build must have no production cloud endpoint fallback.');
assert(adapter.includes('ValidateSafeApiBase'), 'Safe build must validate its endpoint against a code-level allowlist.');
assert(adapter.includes('uri.Host.Equals(approved.Host'), 'Safe external endpoint host must match the reviewed origin exactly.');
assert(adapter.includes('uri.Port == approved.Port'), 'Safe external endpoint port must match the reviewed origin exactly.');
assert(adapter.includes('uri.AbsolutePath.Length == 0 || uri.AbsolutePath == "/"'), 'Safe endpoint must not hide an unreviewed base path.');
assert(adapter.includes('#if HELPSYS_SAFE_TEST_BUILD'), 'Safe loopback must be gated at compile time for test builds only.');
assert(adapter.includes('安全版本番ビルドではloopback AI接続先を許可しません'), 'Production Safe must explicitly reject loopback endpoints.');
assert(adapter.includes('AllowAutoRedirect = false'), 'Cloud transport must never follow redirects to another origin.');
assert(adapter.includes('UseCookies = false'), 'Cloud transport must not persist or replay cookies.');
assert(adapter.includes('CheckCertificateRevocationList = true'), 'Cloud transport must check TLS certificate revocation.');
assert(adapter.includes('handler.UseProxy = false'), 'Safe transport must not inherit an OS/user HTTP proxy.');
assert(adapter.includes('NoStore = true') && adapter.includes('NoCache = true'), 'Cloud requests must ask intermediaries not to store HTTP payloads.');
assert(adapter.includes('request.Headers.Pragma.ParseAdd("no-cache")'), 'Cloud requests must include legacy no-cache protection for intermediaries.');
assert(adapter.includes('AllowedApiKeyHeaders'), 'Cloud transport must use an allowlist for API-key header names.');
assert(!adapter.includes('DangerousAcceptAnyServerCertificateValidator'), 'TLS certificate validation must never be bypassed.');
assert(!adapter.includes('ServerCertificateCustomValidationCallback'), 'Safe transport must not install a custom certificate-validation bypass.');
assert(adapter.includes('MaxResponseBodyBytes = 1024 * 1024'), 'Cloud response bodies must have a strict memory-safety ceiling.');
assert(adapter.includes('content.Headers.ContentLength is > MaxResponseBodyBytes'), 'Declared oversized cloud responses must be rejected before buffering.');
assert(adapter.includes('buffer.Length + read > MaxResponseBodyBytes'), 'Chunked cloud responses must be bounded by measured bytes while streaming.');
assert(adapter.includes('Array.Clear(chunk') && adapter.includes('Array.Clear(segment.Array'), 'Temporary cloud response byte buffers must be cleared after decoding.');
assert(adapter.includes('if (!IsConfigured)'), 'Cloud transport must fail closed when no Safe endpoint is configured.');
assert(cloud.includes('cloud_endpoint_unconfigured'), 'Cloud preflight must classify an unconfigured Safe endpoint before screenshot creation.');
assert(cloud.includes('画面画像を取得せず外部送信を停止しています'), 'Cloud preflight must explicitly stop image acquisition when Safe is unconfigured.');

assert(safeConfiguredSmoke.includes('-p:SafeBuild=true -p:SafeTestBuild=true'), 'Loopback Safe E2E must build a separate test-only Safe binary.');
assert(safeConfiguredSmoke.includes('HelpSys.Safe.Test.exe'), 'Loopback Safe E2E must not execute the production Safe binary.');

assert(diagnostics.includes('#if HELPSYS_SAFE_BUILD'), 'Safe diagnostics policy must be compile-time fixed.');
assert(diagnostics.includes('RawScreenPersistenceAllowed = false'), 'Safe build must permanently forbid raw screen persistence.');

console.log('HelpSys Safe edition privacy contract passed.');