import './commander-stability-contract.mjs';
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const read = p => fs.readFileSync(path.join(root, p), 'utf8');
const exists = p => fs.existsSync(path.join(root, p));
const assert = (condition, message) => { if (!condition) throw new Error(message); };

const index = read('site/index.html');
const redirects = read('site/_redirects');
const headers = read('site/_headers');
const wrangler = read('wrangler.jsonc');
const release = read('.github/workflows/release.yml');
const educationRelease = read('.github/workflows/education-preview.yml');
const cloudAdapter = read('src/Shared/CloudAiAdapter.cs');
const project = read('src/HelpSys.Desktop/HelpSys.Desktop.csproj');
const educationService = read('src/HelpSys.Education/EducationGuideService.cs');
const productionGuard = read('worker/reliability-v4-guard.js');
const educationDoc = read('EDUCATION.md');

const PRODUCTION_BASE = 'https://helpsys.syouziroupc.workers.dev';
const DOWNLOAD_ROUTE = '/download';
const LEGACY_SAFE_ROUTE = '/download/safe';
const EDUCATION_ROUTE = '/download/education';
const STABLE_ALIAS = 'HelpSys-latest-win-x64.zip';
const EDUCATION_ALIAS = 'HelpSys-Education-latest-win-x64.zip';
const DEST = `https://github.com/syouziroupc/helpsys/releases/download/preview-latest/${STABLE_ALIAS}`;
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
assert(routeMap.get(DOWNLOAD_ROUTE)?.destination === DEST, '/download must resolve to the stable unified asset.');
assert(routeMap.get(DOWNLOAD_ROUTE)?.code === '302', '/download redirect must stay temporary so the stable asset can be replaced.');
assert(routeMap.get(LEGACY_SAFE_ROUTE)?.destination === DEST, 'Legacy /download/safe must resolve to the same unified asset.');
assert(routeMap.get(EDUCATION_ROUTE)?.destination === EDUCATION_DEST, 'Education route must keep its stable asset.');

assert(index.includes(`href="${DOWNLOAD_ROUTE}"`), 'Public site must expose the unified stable download route.');
assert(index.includes(`href="${EDUCATION_ROUTE}"`), 'Public site must expose Education.');
assert(index.includes('通常版と安全版を統合'), 'Public site must explain that normal and Safe editions are unified.');
assert(index.includes('/download は常に最新版'), 'Public site must disclose the stable latest-download behavior.');
assert(!/releases\/download\/[^"']+\.zip/i.test(index), 'Public HTML must use local stable routes, not a direct versioned ZIP URL.');
assert(index.includes(`${PRODUCTION_BASE}/`), 'Canonical production HelpSys URL is missing.');

assert(release.includes('branches: [main]'), 'Unified release must publish from main.');
assert(!release.includes('paths:'), 'Unified latest release must not silently skip main updates because of a path filter.');
assert(release.includes(STABLE_ALIAS), 'Release workflow must publish the stable HelpSys alias.');
assert(release.includes('HelpSys-Unified-$shortSha-win-x64.zip'), 'Release workflow must also publish a traceable commit-specific asset.');
assert(release.includes('--clobber'), 'Stable latest asset must be replaced in-place on every release run.');
assert(release.includes('cancel-in-progress: true'), 'Concurrent main updates must not race when replacing preview-latest.');
assert(release.includes('-p:PublishReadyToRun=true'), 'Unified distribution should use ReadyToRun for faster startup.');
assert(!release.includes('HelpSys-Safe-latest-win-x64.zip'), 'Separate Safe distribution must not reappear.');

assert(project.includes('HELPSYS_SAFE_BUILD'), 'Unified desktop build must compile the strict privacy profile by default.');
assert(!project.includes("'$(SafeBuild)' == 'true'"), 'Unified desktop must not depend on a separate SafeBuild switch.');
assert(project.includes('<TieredPGO>true</TieredPGO>'), 'Unified build should retain runtime profile-guided optimization.');

assert(cloudAdapter.includes('ValidateUnifiedApiBase'), 'Unified cloud transport must validate its destination.');
assert(cloudAdapter.includes('uri.Host.Equals(approved.Host'), 'External cloud traffic must be pinned to the approved HelpSys host.');
assert(cloudAdapter.includes('UseProxy = false'), 'Unified cloud transport must not inherit an unreviewed OS/user proxy.');
assert(cloudAdapter.includes('AllowAutoRedirect = false'), 'Unified cloud transport must reject automatic redirects.');
assert(cloudAdapter.includes('UseCookies = false'), 'Unified cloud transport must not persist cookies.');

assert(educationRelease.includes(EDUCATION_ALIAS), 'Education release workflow lost its stable alias.');
assert(educationService.includes(`DefaultApiBase = "${PRODUCTION_BASE}"`), 'Education desktop must default to the deployed API.');
assert(productionGuard.includes("url.pathname === '/v1/education/assist'"), 'Production Worker must expose Education assist.');
assert(educationDoc.includes(`${PRODUCTION_BASE}${EDUCATION_ROUTE}`), 'Education documentation lost the stable public download URL.');

const config = JSON.parse(wrangler);
assert(config?.assets?.directory === './site', 'wrangler.jsonc must deploy ./site as Worker static assets.');
assert(config?.ai?.binding === 'AI', 'Production HelpSys must use a direct Workers AI binding.');
for (const forbiddenBinding of ['kv_namespaces', 'r2_buckets', 'durable_objects', 'd1_databases', 'vectorize'])
  assert(!(forbiddenBinding in config), `Screen-capable production Worker must not gain persistent storage binding ${forbiddenBinding} without review.`);
assert(exists('site/favicon.svg'), 'favicon.svg is missing.');
assert(exists('site/style.css'), 'style.css is missing.');
assert(/Content-Security-Policy:/i.test(headers), 'Static site security headers are missing Content-Security-Policy.');
assert(/X-Content-Type-Options:\s*nosniff/i.test(headers), 'Static site security headers are missing nosniff.');
assert(/X-Frame-Options:\s*DENY/i.test(headers), 'Static site security headers are missing clickjacking protection.');

console.log('HelpSys unified site/release/API contract passed.');
console.log(`unified: ${DOWNLOAD_ROUTE} -> ${DEST}`);
console.log(`legacy safe alias: ${LEGACY_SAFE_ROUTE} -> ${DEST}`);
console.log(`education: ${EDUCATION_ROUTE} -> ${EDUCATION_DEST}`);
