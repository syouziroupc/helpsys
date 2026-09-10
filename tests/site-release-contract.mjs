import './commander-stability-contract.mjs';
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');
const exists = (p) => fs.existsSync(path.join(root, p));
const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};

const index = read('site/index.html');
const safePage = read('site/safe.html');
const redirects = read('site/_redirects');
const headers = read('site/_headers');
const wrangler = read('wrangler.jsonc');
const normalRelease = read('.github/workflows/release.yml');
const educationRelease = read('.github/workflows/education-preview.yml');
const providerReview = read('docs/AI_PROVIDER_PRIVACY_REVIEW.md');
const educationService = read('src/HelpSys.Education/EducationGuideService.cs');
const productionGuard = read('worker/reliability-v4-guard.js');
const educationDoc = read('EDUCATION.md');

const PRODUCTION_BASE = 'https://helpsys.syouziroupc.workers.dev';
const NORMAL_ROUTE = '/download';
const SAFE_ROUTE = '/download/safe';
const EDUCATION_ROUTE = '/download/education';
const NORMAL_ALIAS = 'HelpSys-latest-win-x64.zip';
const SAFE_ALIAS = 'HelpSys-Safe-latest-win-x64.zip';
const EDUCATION_ALIAS = 'HelpSys-Education-latest-win-x64.zip';
const NORMAL_VERSIONED = 'HelpSys-Reliability-v9-win-x64.zip';
const SAFE_VERSIONED = 'HelpSys-Safe-v1-win-x64.zip';
const EDUCATION_VERSIONED = 'HelpSys-Education-v2.2-preview-win-x64.zip';
const NORMAL_DEST = `https://github.com/syouziroupc/helpsys/releases/download/preview-latest/${NORMAL_ALIAS}`;
const SAFE_HOLDING_DEST = '/safe.html';
const EDUCATION_DEST = `https://github.com/syouziroupc/helpsys/releases/download/education-preview-latest/${EDUCATION_ALIAS}`;

function redirectMap(text) {
  const map = new Map();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const [source, destination, code = '302'] = line.split(/\s+/);
    assert(source && destination, `Malformed _redirects line: ${raw}`);
    assert(!map.has(source), `Duplicate redirect source: ${source}`);
    map.set(source, { destination, code });
  }
  return map;
}

const routeMap = redirectMap(redirects);
assert(routeMap.get(NORMAL_ROUTE)?.destination === NORMAL_DEST, 'Normal HelpSys /download redirect does not match the stable release alias.');
assert(routeMap.get(NORMAL_ROUTE)?.code === '302', 'Normal HelpSys download redirect must be temporary (302).');
assert(routeMap.get(SAFE_ROUTE)?.destination === SAFE_HOLDING_DEST, 'Safe /download/safe must remain on the local privacy-review holding page until provider approval.');
assert(routeMap.get(SAFE_ROUTE)?.code === '302', 'Safe holding redirect must be temporary (302).');
assert(routeMap.get(EDUCATION_ROUTE)?.destination === EDUCATION_DEST, 'Education /download/education redirect does not match the stable release alias.');
assert(routeMap.get(EDUCATION_ROUTE)?.code === '302', 'Education download redirect must be temporary (302).');

assert(index.includes(`href="${NORMAL_ROUTE}"`), 'Public site does not expose the stable normal download route.');
assert(index.includes(`href="${SAFE_ROUTE}"`), 'Public site does not expose the Safe review route.');
assert(index.includes(`href="${EDUCATION_ROUTE}"`), 'Public site does not expose the stable Education download route.');
assert(!index.includes('HelpSys-win-x64.zip'), 'Removed broken HelpSys-win-x64.zip URL has reappeared in the public site.');
assert(!/releases\/download\/[^"']+\.zip/i.test(index), 'Public HTML must not couple directly to a versioned GitHub ZIP URL; use stable local routes.');
assert(index.includes(`${PRODUCTION_BASE}/`), 'Canonical production HelpSys URL is missing from the public site.');
assert(safePage.includes('公開前プライバシー監査中') || safePage.includes('公開前監査中'), 'Safe holding page must clearly state that public release is still under review.');
assert(!/\.zip(?:["'])/i.test(safePage), 'Safe holding page must not expose a ZIP while provider approval is blocked.');

assert(providerReview.includes('SAFE_RELEASE_STATUS: BLOCKED'), 'Safe provider privacy review must remain BLOCKED until every mandatory provider condition is verified.');
assert(providerReview.includes('Processing geography / data residency of Workers AI inference | UNRESOLVED'), 'Provider review must explicitly track unresolved Workers AI processing geography.');
assert(providerReview.includes('Applicable model / third-party license terms | UNRESOLVED'), 'Provider review must explicitly track unresolved model license evidence.');

assert(normalRelease.includes(NORMAL_ALIAS), 'Normal release workflow does not publish the stable alias used by /download.');
assert(normalRelease.includes(NORMAL_VERSIONED), 'Normal release workflow lost the traceable Reliability v9 package.');
assert(normalRelease.includes(SAFE_ALIAS), 'Preview release workflow must still know the Safe stable alias for a future approved release.');
assert(normalRelease.includes(SAFE_VERSIONED), 'Preview release workflow must still build the traceable Safe package for QA.');
assert(normalRelease.includes('-p:SafeBuild=true'), 'Safe package is not compiled with the fixed Safe privacy profile.');
assert(normalRelease.includes("'^SAFE_RELEASE_STATUS: APPROVED$'"), 'Safe public upload must require explicit provider privacy approval.');
assert(normalRelease.includes('Safe release gate failed'), 'Release workflow must fail if a Safe ZIP becomes public while provider approval is blocked.');
assert(normalRelease.includes('Release asset missing after publish'), 'Normal/Safe release workflow does not verify its published assets.');
assert(educationRelease.includes(EDUCATION_ALIAS), 'Education release workflow does not publish the stable alias used by /download/education.');
assert(educationRelease.includes(EDUCATION_VERSIONED), 'Education release workflow lost the traceable v2.2 package.');
assert(educationRelease.includes('Education release asset missing after publish'), 'Education release workflow does not verify its published assets.');

assert(educationService.includes(`DefaultApiBase = "${PRODUCTION_BASE}"`), 'Education desktop does not default to the deployed production HelpSys API.');
assert(educationService.includes('/v1/education/assist'), 'Education desktop lost its assist API route.');
assert(productionGuard.includes("import education from './education.js'"), 'Production HelpSys Worker is not wired to the Education handler.');
assert(productionGuard.includes("url.pathname === '/v1/education/assist'"), 'Production HelpSys Worker does not expose the Education assist route.');
assert(educationDoc.includes(`${PRODUCTION_BASE}${EDUCATION_ROUTE}`), 'Education documentation lost the stable public download URL.');

const config = JSON.parse(wrangler);
assert(config?.assets?.directory === './site', 'wrangler.jsonc must deploy ./site as Worker static assets.');
assert(config?.ai?.binding === 'AI', 'Production HelpSys must use a direct Workers AI binding.');
for (const forbiddenBinding of ['kv_namespaces', 'r2_buckets', 'durable_objects', 'd1_databases', 'vectorize'])
  assert(!(forbiddenBinding in config), `Screen-capable production Worker must not gain persistent storage binding ${forbiddenBinding} without a new privacy review.`);
assert(config?.vars?.HELPSYS_MODEL === '@cf/zai-org/glm-4.7-flash', 'Production normal guidance model must be GLM-4.7 Flash.');
assert(config?.vars?.HELPSYS_VISION_MODEL === '@cf/zai-org/glm-5.3-flash', 'Production legacy vision model must be GLM-5.3 Flash.');
assert(config?.vars?.HELPSYS_QUALITY_MODEL === '@cf/zai-org/glm-5.3-flash', 'Production multimodal quality model must be GLM-5.3 Flash.');
assert(config?.vars?.HELPSYS_ASR_MODEL === '@cf/openai/whisper-large-v3-turbo', 'Production Commander ASR model must be Whisper large-v3-turbo.');
assert(exists('site/favicon.svg'), 'favicon.svg referenced by the public site is missing.');
assert(exists('site/style.css'), 'style.css referenced by the public site is missing.');
assert(index.includes('href="favicon.svg"'), 'favicon should use a deployment-portable relative URL.');
assert(index.includes('href="style.css"'), 'stylesheet should use a deployment-portable relative URL.');
assert(safePage.includes('href="favicon.svg"') && safePage.includes('href="style.css"'), 'Safe holding page must use deployment-portable local assets.');

assert(/Content-Security-Policy:/i.test(headers), 'Static site security headers are missing Content-Security-Policy.');
assert(/X-Content-Type-Options:\s*nosniff/i.test(headers), 'Static site security headers are missing nosniff.');
assert(/X-Frame-Options:\s*DENY/i.test(headers), 'Static site security headers are missing clickjacking protection.');

for (const html of [index, safePage]) {
  for (const match of html.matchAll(/<a\b[^>]*target="_blank"[^>]*>/gi)) {
    const tag = match[0];
    assert(/rel="[^"]*noopener[^"]*noreferrer[^"]*"/i.test(tag), `target=_blank link is missing noopener+noreferrer: ${tag}`);
  }
  for (const match of html.matchAll(/(?:href|src)="([^"]+)"/gi)) {
    const value = match[1];
    assert(!/^javascript:/i.test(value), `Unsafe javascript: URL found: ${value}`);
  }
}

console.log('HelpSys site/release/API contract passed.');
console.log(`normal: ${NORMAL_ROUTE} -> ${NORMAL_DEST}`);
console.log(`safe: ${SAFE_ROUTE} -> ${SAFE_HOLDING_DEST} (provider review blocked)`);
console.log(`education: ${EDUCATION_ROUTE} -> ${EDUCATION_DEST}`);
console.log(`education API: ${PRODUCTION_BASE}/v1/education/assist`);
