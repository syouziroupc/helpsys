import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

const hook = main.indexOf('TryHandleLocalAccountChoiceAnswerAsync(text)');
const cloudHistory = main.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", text');
if (hook < 0 || cloudHistory < 0 || hook > cloudHistory)
  throw new Error('local account resolver must intercept the raw clarification answer before cloud/history handling');

if (!local.includes('FindUniqueLocalAccountChoice'))
  throw new Error('local account resolver must require a unique local UIA match');
if (!local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('local account target must be revalidated before guidance');
if (!local.includes('利用者が選んだアカウント'))
  throw new Error('local account history must use an opaque label');
if (local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local resolver must not append the raw account answer to the cloud-bound request/history');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだアカウント'))
  throw new Error('successful local account selection must keep later history opaque');

console.log('HelpSys local account-choice privacy contract passed.');
