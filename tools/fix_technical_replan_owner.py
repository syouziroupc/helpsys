from pathlib import Path

main_path = Path('src/HelpSys.Desktop/MainWindow.xaml.cs')
text = main_path.read_text(encoding='utf-8')

start = text.find('    private void WaitForClarification(string question, long? generation = null)')
end = text.find('\n        _technicalClarificationRetries = 0;\n        ResetCurrentStateReplanBudget();', start)
if start < 0 or end < 0:
    raise SystemExit('technical clarification block not found')
block = text[start:end]

old = '''            _sessionState.Invalidate(GuidanceSessionState.Idle);\n            SetState("現在の画面情報を取り直して案内を続けています…", speak: false);\n\n            Dispatcher.BeginInvoke(new Action(async () =>\n            {\n                if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;\n                try\n                {\n                    await Task.Delay(320, _sessionCts.Token);\n                    if (_activeRequest is not null && !_sessionCts.IsCancellationRequested) await AdvanceGuideAsync();\n                }\n                catch (OperationCanceledException) { }\n            }));\n'''
new = '''            _sessionState.Invalidate(GuidanceSessionState.Idle);\n            _liveReplanPending = true;\n            SetState("現在の画面情報を取り直して案内を続けています…", speak: false);\n\n            Dispatcher.BeginInvoke(new Action(async () =>\n            {\n                if (_activeRequest is null || _sessionCts is null || _sessionCts.IsCancellationRequested) return;\n                try { await TryRunPendingLiveReplanAsync(); }\n                catch (OperationCanceledException) { }\n            }));\n'''

if '_liveReplanPending = true;' not in block or 'TryRunPendingLiveReplanAsync()' not in block:
    if old not in block:
        raise SystemExit('technical clarification direct-replan anchor not found')
    block = block.replace(old, new, 1)
    text = text[:start] + block + text[end:]
    main_path.write_text(text, encoding='utf-8')

contract_path = Path('tests/live-replan-owner-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
if "const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');" not in contract:
    contract = contract.replace(
        "const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');\n",
        "const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');\nconst main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');\n",
        1)

marker = "technical clarification retries must be owned by the established live replan pipeline"
if marker not in contract:
    addition = '''\nconst clarificationStart = main.indexOf('private void WaitForClarification(');\nconst genuineStart = main.indexOf('_technicalClarificationRetries = 0;', clarificationStart);\nconst technicalBlock = main.slice(clarificationStart, genuineStart);\nif (!technicalBlock.includes('_liveReplanPending = true;') || !technicalBlock.includes('TryRunPendingLiveReplanAsync()'))\n  throw new Error('technical clarification retries must be owned by the established live replan pipeline');\nif (technicalBlock.includes('await AdvanceGuideAsync();'))\n  throw new Error('technical clarification retries must not bypass live replan ownership with a direct AdvanceGuideAsync call');\n'''
    contract = contract.replace(
        "console.log('HelpSys live replan ownership contract passed.');",
        addition + "\nconsole.log('HelpSys live replan ownership contract passed.');",
        1)
    contract_path.write_text(contract, encoding='utf-8')
