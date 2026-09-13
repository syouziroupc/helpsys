import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

for (const [source, answer, label] of [
  [main, 'text', 'legacy RequestBox'],
  [live, 'answer', 'AnswerBox']
]) {
  const hook = source.indexOf(`TryHandleLocalVisibleChoiceAnswerAsync(${answer})`);
  const history = source.indexOf(`new GuideHistoryItem(_stepNumber, "clarification_answer", ${answer}`);
  if (hook < 0 || history < 0 || hook > history)
    throw new Error(`${label} clarification path must intercept visible-choice answers before cloud/history handling`);
}

if (!local.includes('LocalVisibleChoiceQuestionRegex') || !local.includes('LooksLikeVisibleChoiceQuestion'))
  throw new Error('local resolver must detect generic visible-choice clarifications');
if (!local.includes('Button", "ListItem", "MenuItem", "Hyperlink", "TabItem", "ComboBox"') ||
    !local.includes('"CheckBox", "RadioButton", "TreeItem"'))
  throw new Error('generic local choice must cover common choice/list/menu/dialog control types');
if (local.includes('IsSupportedLocalChoiceBrowser'))
  throw new Error('generic visible-choice resolution must not be browser-whitelisted');
if (!local.includes('preferWindowScope: true'))
  throw new Error('local visible-choice resolution must prefer the current foreground window scope');
if (!local.includes('FindUniqueLocalVisibleChoice') || !local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('generic local choice must require a unique match and revalidate it');
if (!local.includes('利用者が選んだ項目') || local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local visible-choice answers must remain opaque to cloud-bound request/history');
if (!local.includes('x.Bounds.Contains(center)') || !local.includes('mapped.Count == 1 ? mapped.Values.Single() : null'))
  throw new Error('context-text mapping must use geometric containment and fail closed on ambiguity');
if (!local.includes('if (normalizedAnswer.Length < 3) return null'))
  throw new Error('short answers must not use fuzzy/partial matching');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだ項目'))
  throw new Error('later action history must keep every locally resolved choice opaque');

console.log('HelpSys generic visible-choice privacy contract passed.');
