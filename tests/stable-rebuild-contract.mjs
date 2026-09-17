import fs from 'node:fs';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const project = fs.readFileSync('src/HelpSys.Stable/HelpSys.Stable.csproj', 'utf8');
const main = fs.readFileSync('src/HelpSys.Stable/MainWindow.xaml.cs', 'utf8');
const observation = fs.readFileSync('src/HelpSys.Stable/ObservationService.cs', 'utf8');
const safety = fs.readFileSync('src/HelpSys.Stable/SafetyGate.cs', 'utf8');
const client = fs.readFileSync('src/HelpSys.Stable/GeminiPlannerClient.cs', 'utf8');
const speech = fs.readFileSync('src/HelpSys.Stable/SpeechService.cs', 'utf8');
const updater = fs.readFileSync('src/HelpSys.Stable/UpdateService.cs', 'utf8');
const worker = fs.readFileSync('worker/stable-gemini.js', 'utf8');
const education = fs.readFileSync('worker/education-gemini.js', 'utf8');
const router = fs.readFileSync('worker/stable-router.js', 'utf8');
const wrangler = fs.readFileSync('wrangler.jsonc', 'utf8');
const release = fs.readFileSync('.github/workflows/release.yml', 'utf8');

assert(project.includes('<Version>3.0.0</Version>'), 'Stable version must be explicit');
assert(!project.includes('ProjectReference'), 'Stable project must not reference legacy HelpSys projects');
assert(project.includes('System.Speech') && project.includes('10.0.12'), 'voice features must use the pinned Microsoft System.Speech package');

assert(main.includes('SemaphoreSlim _runGate = new(1, 1)'), 'guidance must be single-flight');
assert((main.match(/_planner\.PlanAsync\(/g) || []).length === 1, 'one guidance run must have one planner call site');
assert(!main.includes('System.Threading.Timer') && !main.includes('DispatcherTimer'), 'Stable shell must not use background timers');
assert(!main.includes('while (') && !main.includes('while('), 'Stable controller must not contain retry loops');
assert(main.includes('HotKeyGuideId') && main.includes('HotKeyVoiceId'), 'F8 guidance and F9 voice shortcuts must be retained');
assert(main.includes('IsPlanStillApplicableAsync'), 'model target must be locally revalidated before display');

const safetyIndex = observation.indexOf('SafetyGate.EnsureSafeToCapture');
const imageIndex = observation.indexOf('CaptureWindow(rect)');
assert(safetyIndex >= 0 && imageIndex > safetyIndex, 'local safety gate must run before screenshot capture');
assert(safety.includes('controls.Any(x => x.Password)'), 'password controls must block screenshot capture');
assert(safety.includes('SecurityWarningTerms'), 'security warnings must be blocked locally');
assert(safety.includes('SecretContextTerms'), 'secret/authentication contexts must be blocked locally');

assert(client.includes('helpsys.syouziroupc.workers.dev/v1/plan'), 'Stable desktop must preserve the public HelpSys origin');
assert(!client.toLowerCase().includes('glm'), 'Stable desktop must not contain GLM fallback');
assert(speech.includes('TimeSpan.FromSeconds(8)'), 'voice input must remain bounded');
assert(updater.includes('SHA256.HashDataAsync'), 'in-app updates must verify SHA-256');

for (const source of [worker, education, router]) assert(!source.toLowerCase().includes('glm'), 'Gemini services must not contain GLM');
assert(worker.includes("const MODEL = 'gemini-3.8-flash'"), 'planner must pin Gemini 3.8 Flash');
assert(education.includes("const MODEL = 'gemini-3.8-flash'"), 'Education must pin Gemini 3.8 Flash');
assert(worker.includes("thinkingLevel: 'medium'"), 'planner must use medium thinking');
assert(education.includes("thinkingLevel: 'low'"), 'Education must use low thinking');
assert(!worker.includes('env.AI.run') && !education.includes('env.AI.run'), 'no Cloudflare AI model fallback is allowed');
assert(wrangler.includes('"main": "worker/stable-router.js"'), 'production Worker must use the clean Stable router');
assert(wrangler.includes('"/v1/*"'), 'API routes must run Worker-first while static site remains asset-first');

assert(release.includes('HelpSys-Stable-$version-$shortSha-win-x64.zip'), 'versioned release asset must expose Stable version');
assert(release.includes('HelpSys-latest-win-x64.zip'), 'public stable alias must remain compatible');
assert(release.includes('HelpSys-latest-win-x64.sha256'), 'release must publish update integrity hash');

console.log('HelpSys Stable 3.0 architecture contract passed.');
