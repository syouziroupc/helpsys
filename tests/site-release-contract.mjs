import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const read = (p) => fs.readFileSync(path.join(root, p), 'utf8');
const exists = (p) => fs.existsSync(path.join(root, p));
const assert = (condition, message) => {
  if (!condition) throw new Error(message);
};

const index = read('site/index.html');
const redirects = read('site/_redirects');
const headers = read('site/_headers');
const wrangler = read('wrangler.jsonc');
const normalRelease = read('.github/workflows/release.yml');
const educationRelease = read('.github/workflows/education-preview.yml');

const NORMAL_ROUTE = '/download';
const EDUCATION_ROUTE = '/download/education';
const NORMAL_ALIAS = 'HelpSys-latest-win-x64.zip';
const EDUCATION_ALIAS = 'HelpSys-Education-latest-win-x64.zip';
const NORMAL_DEST = `https://github.com/syouziroupc/helpsys/releases/download/preview-latest/${NORMAL_ALIAS}`;
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
assert(routeMap.get(EDUCATION_ROUTE)?.destination === EDUCATION_DEST, 'Education /download/education redirect does not match the stable release alias.');
assert(routeMap.get(EDUCATION_ROUTE)?.code === '302', 'Education download redirect must be temporary (302).');

assert(index.includes(`href="${NORMAL_ROUTE}"`), 'Public site does not expose the stable normal download route.');
assert(index.includes(`href="${EDUCATION_ROUTE}"`), 'Public site does not expose the stable Education download route.');
assert(!index.includes('HelpSys-win-x64.zip'), 'Removed broken HelpSys-win-x64.zip URL has reappeared in the public site.');
assert(!/releases\/download\/[^"']+\.zip/i.test(index), 'Public HTML must not couple directly to a versioned GitHub ZIP URL; use stable local routes.');
assert(index.includes('https://helpsys.syouziroupc.workers.dev/'), 'Canonical production HelpSys URL is missing from the public site.');

assert(normalRelease.includes(NORMAL_ALIAS), 'Normal release workflow does not publish the stable alias used by /download.');
assert(normalRelease.includes("HelpSys-Reliability-v5-win-x64.zip"), 'Normal release workflow lost the traceable versioned package.');
assert(normalRelease.includes('Release asset missing after publish'), 'Normal release workflow does not verify its published assets.');
assert(educationRelease.includes(EDUCATION_ALIAS), 'Education release workflow does not publish the stable alias used by /download/education.');
assert(educationRelease.includes('HelpSys-Education-v2.1-preview-win-x64.zip'), 'Education release workflow lost the traceable versioned package.');
assert(educationRelease.includes('Education release asset missing after publish'), 'Education release workflow does not verify its published assets.');

const config = JSON.parse(wrangler);
assert(config?.assets?.directory === './site', 'wrangler.jsonc must deploy ./site as Worker static assets.');
assert(exists('site/favicon.svg'), 'favicon.svg referenced by the public site is missing.');
assert(exists('site/style.css'), 'style.css referenced by the public site is missing.');
assert(index.includes('href="favicon.svg"'), 'favicon should use a deployment-portable relative URL.');
assert(index.includes('href="style.css"'), 'stylesheet should use a deployment-portable relative URL.');

assert(/Content-Security-Policy:/i.test(headers), 'Static site security headers are missing Content-Security-Policy.');
assert(/X-Content-Type-Options:\s*nosniff/i.test(headers), 'Static site security headers are missing nosniff.');
assert(/X-Frame-Options:\s*DENY/i.test(headers), 'Static site security headers are missing clickjacking protection.');

for (const match of index.matchAll(/<a\b[^>]*target="_blank"[^>]*>/gi)) {
  const tag = match[0];
  assert(/rel="[^"]*noopener[^"]*noreferrer[^"]*"/i.test(tag), `target=_blank link is missing noopener+noreferrer: ${tag}`);
}

for (const match of index.matchAll(/(?:href|src)="([^"]+)"/gi)) {
  const value = match[1];
  assert(!/^javascript:/i.test(value), `Unsafe javascript: URL found: ${value}`);
}

console.log('HelpSys site/release contract passed.');
console.log(`normal: ${NORMAL_ROUTE} -> ${NORMAL_DEST}`);
console.log(`education: ${EDUCATION_ROUTE} -> ${EDUCATION_DEST}`);
