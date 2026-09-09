import fs from 'node:fs';
import path from 'node:path';

const read = (filePath) => fs.readFileSync(filePath, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const wake = read('src/HelpSys.Desktop/Services/CommanderWakeService.cs');
const coordinator = read('src/HelpSys.Desktop/Services/MicrophoneCoordinator.cs');
const input = read('src/HelpSys.Desktop/Services/SpeechInputService.cs');
const output = read('src/HelpSys.Desktop/Services/SpeechOutputService.cs');
const window = read('src/HelpSys.Desktop/MainWindow.Commander.cs');
const cloud = read('src/HelpSys.Desktop/Services/CloudGuideService.cs');
const watcher = read('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs');
const stable = read('src/HelpSys.Desktop/MainWindow.StableGuidance.cs');
const systemContext = read('src/HelpSys.Desktop/Services/SystemContextService.cs');
const legacyWorker = read('worker/index.js');
const qualityWorker = read('worker/quality-guide.js');
const educationWorker = read('worker/education.js');
const wrangler = read('wrangler.jsonc');
const educationWrangler = read('wrangler.education.jsonc');

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
assert(window.includes('wakeReleased = true'), 'Commander must release wake listening before a potentially long planning pass.');
assert(window.indexOf('_commander.CompleteWakeInteraction();\n            wakeReleased = true;') < window.indexOf('var planningTask = StartOrContinueSessionAsync();'), 'Commander wake release must occur before long planning begins.');

assert(cloud.includes('AttemptTimeout = TimeSpan.FromSeconds(9)'), 'Cloud guidance calls must have a short per-attempt timeout.');
assert(cloud.includes('x-helpsys-request-id'), 'Guidance calls must carry request IDs for production debugging.');

assert(!watcher.includes('pump.GetAwaiter().GetResult()'), 'Live watcher shutdown must never synchronously block the WPF thread.');
assert(watcher.includes('DrainStoppedPumpAsync'), 'Live watcher pump must drain asynchronously.');
assert(stable.includes('_stablePulseQueued'), 'Rapid screen-change pulses must be coalesced before entering the dispatcher.');
assert(systemContext.includes('GetCachedBrowser'), 'Browser UIA context must use a cached/background path.');
assert(systemContext.includes('QueueBrowserRefresh'), 'Browser UIA context must refresh outside the caller path.');
assert(!/Capture\(\)[\s\S]{0,2500}\?\s*TryCaptureBrowser\(/.test(systemContext), 'SystemContext Capture must not directly perform browser UIA traversal on the caller thread.');

const config = JSON.parse(wrangler);
const educationConfig = JSON.parse(educationWrangler);
assert(config?.vars?.HELPSYS_MODEL === '@cf/zai-org/glm-4.7-flash', 'Normal HelpSys text model must be GLM-4.7 Flash.');
assert(config?.vars?.HELPSYS_VISION_MODEL === '@cf/zai-org/glm-5.3-flash', 'Legacy vision route must be pinned to GLM-5.3 Flash.');
assert(config?.vars?.HELPSYS_QUALITY_MODEL === '@cf/zai-org/glm-5.3-flash', 'Multimodal quality model must be GLM-5.3 Flash.');
assert(educationConfig?.vars?.HELPSYS_EDUCATION_MODEL === '@cf/zai-org/glm-4.7-flash', 'Education text model must be GLM-4.7 Flash.');
assert(legacyWorker.includes("DEFAULT_TEXT_MODEL = '@cf/zai-org/glm-4.7-flash'"), 'Legacy structured route must default only to GLM-4.7 Flash.');
assert(legacyWorker.includes("DEFAULT_VISION_MODEL = '@cf/zai-org/glm-5.3-flash'"), 'Legacy vision route must default only to GLM-5.3 Flash.');
assert(legacyWorker.includes('selectVisionModel(env.HELPSYS_VISION_MODEL || env.HELPSYS_QUALITY_MODEL)'), 'Legacy vision must never reuse the text-only model variable.');
assert(qualityWorker.includes("DEFAULT_MODEL = '@cf/zai-org/glm-5.3-flash'"), 'Quality route must use GLM-5.3 Flash.');
assert(educationWorker.includes("DEFAULT_MODEL = '@cf/zai-org/glm-4.7-flash'"), 'Education route must use GLM-4.7 Flash.');

const allowedModels = new Set([
  '@cf/zai-org/glm-4.7-flash',
  '@cf/zai-org/glm-5.3-flash'
]);

const runtimeFiles = [
  ...walkFiles('worker', file => file.endsWith('.js')),
  ...walkFiles('src', file => file.endsWith('.cs') || file.endsWith('.csproj')),
  ...walkFiles('.github/workflows', file => file.endsWith('.yml') || file.endsWith('.yaml')),
  ...fs.readdirSync('.').filter(file => /^wrangler(?:\..+)?\.jsonc$/i.test(file))
];

const modelPattern = /@cf\/[A-Za-z0-9._/-]+/g;
const foundModels = new Map();
for (const file of runtimeFiles) {
  const matches = [...read(file).matchAll(modelPattern)].map(match => match[0]);
  if (matches.length) foundModels.set(file, [...new Set(matches)]);
}

for (const [file, models] of foundModels) {
  for (const model of models) {
    assert(allowedModels.has(model), `Stale/unapproved runtime model ${model} found in ${file}.`);
  }
}

assert([...foundModels.values()].flat().includes('@cf/zai-org/glm-4.7-flash'), 'Runtime scan did not find the GLM-4.7 text model.');
assert([...foundModels.values()].flat().includes('@cf/zai-org/glm-5.3-flash'), 'Runtime scan did not find the GLM-5.3 vision model.');

console.log('HelpSys Commander/GLM/freeze stability contract passed.');
console.log('Runtime model refs:', Object.fromEntries(foundModels));

function walkFiles(root, include) {
  if (!fs.existsSync(root)) return [];
  const outputFiles = [];
  const queue = [root];
  while (queue.length) {
    const current = queue.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) queue.push(full);
      else if (entry.isFile() && include(full)) outputFiles.push(full);
    }
  }
  return outputFiles;
}
