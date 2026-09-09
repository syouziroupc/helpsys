import fs from 'node:fs';

const read = (path) => fs.readFileSync(path, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const wake = read('src/HelpSys.Desktop/Services/CommanderWakeService.cs');
const coordinator = read('src/HelpSys.Desktop/Services/MicrophoneCoordinator.cs');
const input = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const output = read('src/HelpSys.Desktop/Services/SpeechOutputService.cs');
const window = read('src/HelpSys.Desktop/MainWindow.Commander.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const wrangler = read('wrangler.jsonc');

assert(!wake.includes('StopEngineLocked'), 'Commander must not cancel/dispose the speech engine while holding its state lock.');
assert(wake.includes('ReleaseEngineAsync'), 'Commander must release the recognizer outside the state lock.');
assert(wake.includes('MinimumWakeConfidence = 0.72f'), 'Commander wake confidence regression.');
assert(wake.includes('normalized.Equals("ねえコマンダー"'), 'Wake phrase must use exact normalized equality, not substring matching.');
assert(!wake.includes('normalized.Contains("ねえコマンダー"'), 'Wake phrase substring matching may cause false wakes.');

assert(coordinator.includes('WaitForBackgroundReleaseAsync'), 'Microphone coordinator must expose an awaitable wake-release barrier.');
assert(input.includes('await MicrophoneCoordinator.WaitForBackgroundReleaseAsync'), 'Foreground dictation must wait until Commander actually releases the microphone.');
assert(input.includes('TimeSpan.FromSeconds(8)'), 'Foreground dictation must have a bounded recognition window.');

assert(output.includes('SpeakPromptAsync'), 'Commander acknowledgement must be awaitable.');
assert(!window.includes('Task.Delay(2300)'), 'Fixed Commander TTS delay must not return.');
assert(window.includes('SpeakPromptAsync("はい。どうしましたか？"'), 'Commander should begin dictation from actual acknowledgement completion.');
assert(!window.includes('終わったらもう一度「ねえコマンダー」'), 'Spoken busy text must not contain the wake phrase.');
assert(!window.includes('もう一度「ねえコマンダー」と呼'), 'Spoken retry text must not contain the wake phrase.');

assert(cloud.includes('AttemptTimeout = TimeSpan.FromSeconds(9)'), 'Cloud guidance calls must have a short per-attempt timeout.');
assert(cloud.includes('x-helpsys-request-id'), 'Guidance calls must carry request IDs for production debugging.');

const config = JSON.parse(wrangler);
assert(config?.vars?.HELPSYS_MODEL === '@cf/zai-org/glm-4.7-flash', 'Normal HelpSys model must be GLM-4.7 Flash.');
assert(config?.vars?.HELPSYS_QUALITY_MODEL === '@cf/zai-org/glm-5.3-flash', 'Multimodal quality model must be GLM-5.3 Flash.');

console.log('HelpSys Commander/GLM stability contract passed.');
