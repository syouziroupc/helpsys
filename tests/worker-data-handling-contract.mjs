import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const quality = read('worker/quality-guide.js');
const education = read('worker/education.js');
const transcribe = read('worker/transcribe.js');
const guard = read('worker/reliability-v4-guard.js');

for (const [name, source] of [['quality', quality], ['education', education], ['transcribe', transcribe]]) {
  assert(!/catch\s*\(\s*(?:error|err|ex|exception)\s*\)[\s\S]{0,240}console\.(?:log|error|warn|info|debug)\s*\([^)]*,/i.test(source),
    `${name} Worker must not log provider exception objects or other dynamic error arguments.`);
  assert(!/console\.(?:log|error|warn|info|debug)\s*\([^\n]*(?:body|payload|image|audio|request)\b/i.test(source),
    `${name} Worker must not log request bodies, payloads, images or audio.`);
}

assert(quality.includes('store: false'), 'Quality inference must explicitly disable model-side storage when supported.');
assert(education.includes('store: false'), 'Education inference must explicitly disable model-side storage when supported.');
assert(!quality.includes('value: value.password'), 'Quality model compaction must not accept raw UI values.');
assert(quality.includes('inputPresent:'), 'Quality model may retain only boolean input-presence state.');
assert(!/\burl:\s*nullableText\(b\.url/.test(quality), 'Quality model context must not accept a full browser URL.');
assert(!/browserUrl:\s*nullableText/.test(quality), 'Quality evidence must not accept a full browser URL.');

assert(guard.includes('sanitizeScreenBody'), 'Production Worker entry must sanitize screen-derived payloads before downstream routing.');
assert(guard.includes('const { value, Value, ...safe } = element'), 'Worker boundary must remove raw UI value fields.');
assert(guard.includes('const { url, Url, ...safeBrowser } = browser'), 'Worker boundary must remove full browser URL fields.');
assert(guard.includes('const { browserUrl, BrowserUrl, ...safeEvidence } = evidence'), 'Worker boundary must remove full evidence URL fields.');
assert(guard.includes('store: false'), 'Production Worker AI wrapper must force store:false as defense in depth.');

console.log('HelpSys Worker privacy data-handling contract passed.');
