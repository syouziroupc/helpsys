from pathlib import Path

main_path = Path('src/HelpSys.Desktop/MainWindow.xaml.cs')
main = main_path.read_text(encoding='utf-8')

start = main.find('    private void ShowKeyboardGuide(')
end = main.find('\n    private async Task<bool> TryVisionFallbackAsync', start)
if start < 0 or end < 0:
    raise SystemExit('ShowKeyboardGuide block not found')
block = main[start:end]
if 'ResetCurrentStateReplanBudget();' not in block:
    old = '''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;\n        _technicalClarificationRetries = 0;\n        _currentDecision = decision;\n'''
    new = '''        if (!_sessionState.TryTransition(generation, GuidanceSessionState.Presenting)) return;\n        _technicalClarificationRetries = 0;\n        ResetCurrentStateReplanBudget();\n        _currentDecision = decision;\n'''
    if old not in block:
        raise SystemExit('ShowKeyboardGuide presenting anchor not found')
    block = block.replace(old, new, 1)
    main = main[:start] + block + main[end:]
main_path.write_text(main, encoding='utf-8')

contract_path = Path('tests/current-state-replan-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
old_check = """if ((main.match(/ResetCurrentStateReplanBudget\\(\\);/g) || []).length < 3)\n  throw new Error('successful target/keyboard/clarification states must reset the replan budget');\n"""
new_check = """const structuredStart = main.indexOf('private void ShowStructuredTarget(');\nconst keyboardStart = main.indexOf('private void ShowKeyboardGuide(');\nconst visionFallbackStart = main.indexOf('private async Task<bool> TryVisionFallbackAsync', keyboardStart);\nconst structuredBlock = main.slice(structuredStart, keyboardStart);\nconst keyboardBlock = main.slice(keyboardStart, visionFallbackStart);\nif (!structuredBlock.includes('ResetCurrentStateReplanBudget();'))\n  throw new Error('successful structured target guidance must reset the replan budget');\nif (!keyboardBlock.includes('ResetCurrentStateReplanBudget();'))\n  throw new Error('successful keyboard guidance must reset the replan budget');\nif (!main.includes('_technicalClarificationRetries = 0;\\n        ResetCurrentStateReplanBudget();\\n        if (generation.HasValue)') &&\n    !main.includes('_technicalClarificationRetries = 0;\\r\\n        ResetCurrentStateReplanBudget();\\r\\n        if (generation.HasValue)'))\n  throw new Error('genuine clarification must reset the replan budget');\n"""
if old_check in contract:
    contract = contract.replace(old_check, new_check, 1)
elif "const structuredStart = main.indexOf('private void ShowStructuredTarget(');" not in contract:
    raise SystemExit('current-state replan reset contract anchor not found')
contract_path.write_text(contract, encoding='utf-8')
