import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const speech = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const privacyUi = read('src/HelpSys.Desktop/MainWindow.Privacy.cs');
const diagnostics = read('src/HelpSys.Desktop/Services/DiagnosticModePolicy.cs');
const project = read('src/HelpSys.Desktop/HelpSys.Desktop.csproj');

assert(project.includes('HELPSYS_SAFE_BUILD'), 'Safe build compile-time symbol is required.');
assert(speech.includes('#if HELPSYS_SAFE_BUILD'), 'Safe build must compile a distinct cloud speech policy.');
assert(speech.includes('return false;') && speech.includes('CloudTranscriptionAllowed'), 'Safe build must report cloud transcription as disabled.');
assert(speech.indexOf('if (!CloudTranscriptionAllowed)') < speech.indexOf('MicrophoneCoordinator.BeginForegroundCapture'), 'Safe cloud-speech block must run before microphone capture starts.');
assert(speech.includes('安全版では音声データをクラウドへ送信しません'), 'Safe speech refusal must explain the privacy reason.');

assert(privacyUi.includes('VoiceButton.IsEnabled = false'), 'Safe UI must disable the cloud voice button.');
assert(privacyUi.includes('CommanderButton.IsEnabled = false'), 'Safe UI must disable Commander cloud dictation.');
assert(privacyUi.includes('_commander.SetEnabled(false)'), 'Safe UI must turn off Commander wake monitoring after initialization.');
assert(privacyUi.includes('音声のクラウド送信も無効です'), 'Safe startup status must disclose that cloud voice is disabled.');

assert(diagnostics.includes('#if HELPSYS_SAFE_BUILD'), 'Safe diagnostics policy must be compile-time fixed.');
assert(diagnostics.includes('RawScreenPersistenceAllowed = false'), 'Safe build must permanently forbid raw screen persistence.');

console.log('HelpSys Safe edition privacy contract passed.');
