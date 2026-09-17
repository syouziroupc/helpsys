import fs from 'node:fs';

const primary = fs.readFileSync('worker/gemini-primary.js', 'utf8');
const guide = fs.readFileSync('worker/gemini-guide.js', 'utf8');
const vision = fs.readFileSync('worker/gemini-vision.js', 'utf8');
const wrangler = fs.readFileSync('wrangler.jsonc', 'utf8');
const cloud = fs.readFileSync('src/HelpSys.Desktop/Services/CloudGuideService.cs', 'utf8');

for (const route of ['/v1/guide', '/v1/quality-guide', '/v1/vision-guide']) {
  if (!primary.includes(route)) throw new Error(`Gemini router must own ${route}`);
}
if (!primary.includes("plannerProvider: 'gemini'"))
  throw new Error('health endpoint must report Gemini as the guidance provider');
if (primary.includes('glm') || primary.includes('GLM'))
  throw new Error('Gemini guidance router must not contain a GLM fallback');
if (primary.includes('gemini_primary_unavailable_using_glm_fallback'))
  throw new Error('provider failure must never silently fall back to GLM');
if (!primary.includes('return geminiGuide.fetch') || !primary.includes('return geminiVision.fetch'))
  throw new Error('structured, quality, and vision guidance must terminate in Gemini planners');

if (!guide.includes("const DEFAULT_GEMINI_MODEL = 'gemini-3.8-flash'"))
  throw new Error('structured/quality planner must use the unified Gemini model');
if (!vision.includes("const DEFAULT_GEMINI_MODEL = 'gemini-3.8-flash'"))
  throw new Error('vision planner must use the same Gemini model');
if (!guide.includes("thinkingLevel === 'high'") || !vision.includes("thinkingLevel === 'high'"))
  throw new Error('same-evidence deeper adjudication must be available without recapturing the screen');
if (!guide.includes('Reuse the SAME evidence once with deeper reasoning'))
  throw new Error('quality uncertainty must be resolved by deeper reasoning rather than repeated capture');
if (!vision.includes('Same screenshot, deeper reasoning'))
  throw new Error('vision uncertainty must reuse the same screenshot rather than repeated capture');

if (wrangler.includes('HELPSYS_MODEL') || wrangler.includes('HELPSYS_VISION_MODEL') || wrangler.includes('HELPSYS_QUALITY_MODEL'))
  throw new Error('Wrangler must not configure old GLM guidance models');
if (!wrangler.includes('"HELPSYS_GEMINI_MODEL": "gemini-3.8-flash"'))
  throw new Error('Wrangler must configure Gemini 3.8 Flash for HelpSys guidance');

if (!cloud.includes('AttemptTimeout = TimeSpan.FromSeconds(30)'))
  throw new Error('desktop timeout must permit Gemini medium/high reasoning to finish');
if (!cloud.includes('EnsurePlanningContextCurrent(systemContext, requireSameWindow: true)'))
  throw new Error('pre-send privacy/TOCTOU check must still pin the exact source window');
if (!cloud.includes('EnsurePlanningContextCurrent(systemContext, requireSameWindow: false)'))
  throw new Error('post-model validation must allow same-process window transitions and rely on target revalidation');

console.log('HelpSys Gemini-only guidance contract passed.');
