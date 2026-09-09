import fs from 'node:fs';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');
const speech = fs.readFileSync('src/HelpSys.Desktop/Services/SpeechInputService.cs', 'utf8');
const education = fs.readFileSync('src/HelpSys.Education/EducationGuideService.cs', 'utf8');
const commander = fs.readFileSync('src/HelpSys.Desktop/Services/CommanderWakeService.cs', 'utf8');
const wrangler = fs.readFileSync('wrangler.jsonc', 'utf8');

for (const [name, source] of [['guide', cloud], ['speech', speech], ['education', education]]) {
  assert(source.includes('CloudflareCompatibleUserAgent'), `${name} client must define Cloudflare-compatible UA`);
  assert(source.includes('User-Agent'), `${name} client must send User-Agent`);
  assert(source.includes('x-helpsys-client'), `${name} client must identify HelpSys client class`);
}

assert(speech.includes('音声認識サービスの応答が時間内に返りませんでした。'),
  'speech timeout must not be reported as user cancellation');
assert(education.includes('教育AIの応答が時間内に返りませんでした。'),
  'education timeout must not be reported as user cancellation');

assert(commander.includes('CompleteDelayedWakeAfterReleaseAsync'),
  'recognized Commander wake must survive a slow recognizer release');
assert(commander.includes('RaiseWakeDetectedIfReady'),
  'Commander must dispatch the already-recognized wake after release');
const timeoutBlock = commander.match(/catch \(TimeoutException\)[\s\S]*?catch\s*\{/i)?.[0] || '';
assert(timeoutBlock && !timeoutBlock.includes('_interactionHeld = false'),
  'slow recognizer release must not discard the recognized wake interaction');

for (const binding of ['GUIDE_RATE_LIMITER', 'ASR_RATE_LIMITER', 'EDUCATION_RATE_LIMITER']) {
  assert(wrangler.includes(`\"name\": \"${binding}\"`), `${binding} must be declared in Wrangler config`);
}

console.log('network hardening contract passed.');
