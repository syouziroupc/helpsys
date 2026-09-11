import fs from 'node:fs';

const read = file => fs.readFileSync(file, 'utf8');
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const quality = read('worker/quality-guide.js');
const education = read('worker/education.js');
const transcribe = read('worker/transcribe.js');
const guard = read('worker/reliability-v4-guard.js');
const wrangler = read('wrangler.jsonc');
const educationWrangler = read('wrangler.education.jsonc');
const packageJson = JSON.parse(read('package.json'));

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
assert(guard.includes('sanitizeOutboundText'), 'Worker screen boundary must re-sanitize free-form text from old or modified clients.');
for (const marker of ['<email>', '<phone>', '<postal-code>', '<redacted-secret>', '<redacted-api-key>', '<redacted-card>'])
  assert(guard.includes(marker), `Worker screen boundary is missing privacy replacement marker ${marker}.`);
for (const pattern of ['EMAIL', 'JP_PHONE', 'JP_POSTAL', 'LABELED_SECRET', 'BEARER', 'JWT', 'API_KEY', 'CARD', 'URL_PATTERN'])
  assert(guard.includes(`const ${pattern}`), `Worker screen boundary is missing ${pattern} sanitizer.`);
assert(guard.includes('sanitizeAliasedText(body, \'request\', \'Request\''), 'Worker must sanitize the user request before model routing.');
assert(guard.includes('sanitizeStringArray(safeEvidence'), 'Worker must sanitize free-form evidence arrays before model routing.');
assert(guard.includes('store: false'), 'Production Worker AI wrapper must force store:false as defense in depth.');

assert(education.includes('sanitizeEducationText'), 'Education Worker must sanitize learner-controlled text before inference.');
for (const marker of ['<email>', '<phone>', '<postal-code>', '<redacted-secret>', '<redacted-api-key>', '<redacted-card>'])
  assert(education.includes(marker), `Education Worker is missing privacy replacement marker ${marker}.`);
assert(education.includes('const URL_PATTERN'), 'Education Worker must use a URL sanitizer without shadowing the URL constructor.');
assert(education.includes('const lessonTitle = sanitizeEducationText'), 'Education lesson titles must be sanitized before inference.');
assert(education.includes('const objective = sanitizeEducationText'), 'Education objectives must be sanitized before inference.');
assert(education.includes('const learnerMessage = sanitizeEducationText'), 'Education learner messages must be sanitized before inference.');

for (const [name, config] of [['main', wrangler], ['education', educationWrangler]]) {
  const compact = config.replace(/\s+/g, '');
  assert(compact.includes('"cache":{"enabled":false}'), `${name} Worker must explicitly disable Workers HTTP caching.`);
}
assert(/^\^4\.(?:6[9-9]|[7-9]\d|\d{3,})\./.test(String(packageJson.devDependencies?.wrangler || '')),
  'Wrangler must be pinned to a version that supports explicit cache.enabled=false configuration.');

console.log('HelpSys Worker privacy data-handling and HTTP-cache contract passed.');
