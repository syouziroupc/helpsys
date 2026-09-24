import fs from 'node:fs';

const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

function need(fragment, message) {
  if (!reliability.includes(fragment)) throw new Error(message);
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
need('changedSignal.Task.IsCompleted\n                ? ActionVerificationResult.Inconclusive\n                : ActionVerificationResult.NoEffect',
  'an observed UI event without a provable expected effect must not be mislabeled as no-effect');

const inconclusiveStart = reliability.indexOf('else\n            {\n                LocalLogService.Write(\n                    "action_verification_inconclusive"');
const postBlock = reliability.indexOf('        }\n        catch (OperationCanceledException)', inconclusiveStart);
const inconclusiveBlock = reliability.slice(inconclusiveStart, postBlock);
if (inconclusiveStart < 0 || inconclusiveBlock.includes('RecordOutlawGuidanceOutcome('))
  throw new Error('inconclusive verification must not poison Outlaw success/failure memory');

console.log('HelpSys tri-state action verification contract passed.');
