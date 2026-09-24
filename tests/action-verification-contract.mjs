import fs from 'node:fs';

const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');
const normalized = reliability.replace(/\s+/g, ' ').trim();

function need(fragment, message) {
  if (!normalized.includes(String(fragment).replace(/\s+/g, ' ').trim())) throw new Error(message);
}

for (const state of ['Success', 'NoEffect', 'Inconclusive'])
  need(state, `action verifier must expose ${state}`);

need('private async Task<ActionVerificationResult> WaitForStableStateTransitionV3Async(',
  'post-action verification must return a tri-state result instead of bool');
need('verification == ActionVerificationResult.NoEffect',
  'only a confirmed no-effect result may enter failed-action handling');
need('verification == ActionVerificationResult.Success',
  'confirmed expected effects must advance the session');
need('"action_verification_inconclusive"',
  'inconclusive verification must be logged explicitly');
need('HandleTechnicalPlanningUncertainty("操作結果の検証が不確定", generation);',
  'inconclusive verification must request one bounded fresh observation');
need('changedSignal.Task.IsCompleted ? ActionVerificationResult.Inconclusive : ActionVerificationResult.NoEffect',
  'an observed UI event without a provable expected effect must not be mislabeled as no-effect');

const inconclusiveMatch = /else\s*\{\s*LocalLogService\.Write\(\s*"action_verification_inconclusive"[\s\S]*?inconclusive\s*=\s*true;\s*\}/m.exec(reliability);
if (!inconclusiveMatch || inconclusiveMatch[0].includes('RecordOutlawGuidanceOutcome('))
  throw new Error('inconclusive verification must not poison Outlaw success/failure memory');

console.log('HelpSys tri-state action verification contract passed.');
