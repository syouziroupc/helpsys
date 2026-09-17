import fs from 'node:fs';

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

const project = fs.readFileSync('src/HelpSys.Stable/HelpSys.Stable.csproj', 'utf8');
const main = fs.readFileSync('src/HelpSys.Stable/MainWindow.xaml.cs', 'utf8');
const observation = fs.readFileSync('src/HelpSys.Stable/ObservationService.cs', 'utf8');
const safety = fs.readFileSync('src/HelpSys.Stable/SafetyGate.cs', 'utf8');
const client = fs.readFileSync('src/HelpSys.Stable/GeminiPlannerClient.cs', 'utf8');
const worker = fs.readFileSync('worker/stable-gemini.js', 'utf8');
const wrangler = fs.readFileSync('wrangler.stable.jsonc', 'utf8');

assert(!project.includes('ProjectReference'), 'stable project must not reference legacy HelpSys projects');
assert(!project.includes('PackageReference'), 'stable project should avoid third-party package dependencies');
assert(main.includes('SemaphoreSlim _runGate = new(1, 1)'), 'guidance must be single-flight');
assert((main.match(/_planner\.PlanAsync\(/g) || []).length === 1, 'one guidance run must have exactly one planner call site');
assert(!main.includes('System.Threading.Timer') && !main.includes('DispatcherTimer'), 'stable shell must not use background timers');
assert(!main.includes('while (') && !main.includes('while('), 'stable controller must not contain retry loops');
assert(main.includes('catch (ObservationChangedException)') && (main.match(/_observation\.CaptureAsync\(/g) || []).length === 2,
  'only one observation retry is allowed and only for actual foreground change');

const safetyIndex = observation.indexOf('SafetyGate.EnsureSafeToCapture');
const imageIndex = observation.indexOf('CaptureWindow(rect)');
assert(safetyIndex >= 0 && imageIndex > safetyIndex, 'local safety gate must run before screenshot capture');
assert(safety.includes('controls.Any(x => x.Password)'), 'password controls must block screenshot capture');
assert(safety.includes('SecurityWarningTerms'), 'browser/OS security warnings must be blocked locally');
assert(safety.includes('SecretContextTerms'), 'secret/authentication contexts must be blocked locally');

assert(client.includes('helpsys-stable.syouziroupc.workers.dev/v1/plan'), 'desktop must use isolated stable endpoint');
assert(!client.toLowerCase().includes('glm'), 'stable desktop client must not contain GLM fallback');
assert(worker.includes("const MODEL = 'gemini-3.8-flash'"), 'stable worker must pin Gemini 3.8 Flash');
assert(!worker.toLowerCase().includes('glm'), 'stable worker must not contain GLM fallback');
assert(!worker.includes('env.AI.run'), 'stable worker must not call Cloudflare AI models');
assert(worker.includes("thinkingLevel: 'medium'"), 'stable Gemini planner must use medium thinking');
assert(worker.includes("responseMimeType: 'application/json'"), 'stable worker must require structured output');
assert(wrangler.includes('"name": "helpsys-stable"'), 'stable worker must deploy independently of legacy HelpSys');
assert(wrangler.includes('worker/stable-gemini.js'), 'stable worker config must target only the clean Gemini worker');

console.log('HelpSys Stable rebuild architecture contract passed.');
