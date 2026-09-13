from pathlib import Path


def replace_once_if_needed(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


# The current clarification UI uses AnswerBox/SubmitClarificationAnswerAsync.
# Intercept raw account/profile answers here before they can enter history.
live_path = Path('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs')
live = live_path.read_text(encoding='utf-8')
old_live = '''        _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", answer, _clarificationQuestion ?? "確認質問"));\n'''
new_live = '''        var localChoice = await TryHandleLocalAccountChoiceAnswerAsync(answer);\n        if (localChoice == LocalChoiceAnswerResult.Handled) return;\n\n        _history.Add(new GuideHistoryItem(_stepNumber, "clarification_answer", answer, _clarificationQuestion ?? "確認質問"));\n'''
live = replace_once_if_needed(
    live,
    old_live,
    new_live,
    'TryHandleLocalAccountChoiceAnswerAsync(answer)',
    'AnswerBox local-choice hook')
live_path.write_text(live, encoding='utf-8')


# Keep the extended clarification panel consistent when local resolution succeeds/fails.
resolver_path = Path('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs')
resolver = resolver_path.read_text(encoding='utf-8')
old_success = '''        _clarificationQuestion = null;\n        GuideButton.Content = "案内";\n        RequestBox.Text = _originalRequest ?? _activeRequest;\n        RequestBox.CaretIndex = RequestBox.Text.Length;\n        _localChoiceTargetActive = true;\n'''
new_success = '''        _clarificationQuestion = null;\n        HideClarificationUiIfNeeded(force: true);\n        RequestBox.Text = _originalRequest ?? _activeRequest;\n        RequestBox.CaretIndex = RequestBox.Text.Length;\n        _localChoiceTargetActive = true;\n'''
resolver = replace_once_if_needed(
    resolver,
    old_success,
    new_success,
    'HideClarificationUiIfNeeded(force: true);\n        RequestBox.Text = _originalRequest',
    'local-choice success UI')

old_failure = '''        _localChoiceTargetActive = false;\n        WaitForClarification(question);\n'''
new_failure = '''        _localChoiceTargetActive = false;\n        WaitForClarification(question);\n        EnsureClarificationUi();\n'''
resolver = replace_once_if_needed(
    resolver,
    old_failure,
    new_failure,
    'WaitForClarification(question);\n        EnsureClarificationUi();',
    'local-choice retry UI')
resolver_path.write_text(resolver, encoding='utf-8')


# Rewrite the contract so both legacy and current AnswerBox paths are protected.
contract_path = Path('tests/local-choice-contract.mjs')
contract_path.write_text(r'''import fs from 'node:fs';

const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const live = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LiveGuidance.cs', 'utf8');
const local = fs.readFileSync('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs', 'utf8');
const reliability = fs.readFileSync('src/HelpSys.Desktop/MainWindow.ReliabilityV3.cs', 'utf8');

const legacyHook = main.indexOf('TryHandleLocalAccountChoiceAnswerAsync(text)');
const legacyHistory = main.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", text');
if (legacyHook < 0 || legacyHistory < 0 || legacyHook > legacyHistory)
  throw new Error('legacy clarification path must intercept the raw account answer before history/cloud handling');

const liveHook = live.indexOf('TryHandleLocalAccountChoiceAnswerAsync(answer)');
const liveHistory = live.indexOf('new GuideHistoryItem(_stepNumber, "clarification_answer", answer');
if (liveHook < 0 || liveHistory < 0 || liveHook > liveHistory)
  throw new Error('AnswerBox clarification path must intercept the raw account answer before history/cloud handling');

if (!local.includes('FindUniqueLocalAccountChoice'))
  throw new Error('local account resolver must require a unique local UIA match');
if (!local.includes('RevalidateCandidateAsync(match, context.ForegroundProcessId'))
  throw new Error('local account target must be revalidated before guidance');
if (!local.includes('利用者が選んだアカウント'))
  throw new Error('local account history must use an opaque label');
if (local.includes('_activeRequest +=') || local.includes('clarification_answer", answer'))
  throw new Error('local resolver must not append the raw account answer to the cloud-bound request/history');
if (!local.includes('HideClarificationUiIfNeeded(force: true)'))
  throw new Error('successful local choice must close the clarification panel before target guidance');
if (!local.includes('WaitForClarification(question);\n        EnsureClarificationUi();'))
  throw new Error('failed local matching must keep the clarification UI available for retry');
if (!reliability.includes('_localChoiceTargetActive') || !reliability.includes('利用者が選んだアカウント'))
  throw new Error('successful local account selection must keep later history opaque');

console.log('HelpSys local account-choice privacy contract passed.');
''', encoding='utf-8')
