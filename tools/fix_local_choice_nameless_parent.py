from pathlib import Path


def replace_once(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


path = Path('src/HelpSys.Desktop/MainWindow.LocalChoiceResolver.cs')
text = path.read_text(encoding='utf-8')
old = '''        var interactable = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name))
            .ToArray();
'''
new = '''        var interactable = candidates
            .Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)
            .ToArray();
'''
text = replace_once(text, old, new, '.Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)\n            .ToArray();', 'nameless clickable choice support')
path.write_text(text, encoding='utf-8')

contract_path = Path('tests/local-choice-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
addition = r'''
if (!local.includes('.Where(x => x.Interactable && x.Enabled && !x.Bounds.IsEmpty)'))
  throw new Error('context-to-card mapping must allow a nameless clickable parent when its child identity is unique');
if (local.includes('x.Interactable && x.Enabled && !x.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(x.Name)'))
  throw new Error('nameless clickable account cards must not be discarded before local child-context mapping');
'''
marker = 'nameless clickable parent when its child identity is unique'
if marker not in contract:
    console = "console.log('HelpSys local account-choice privacy contract passed.');"
    if console not in contract:
        raise SystemExit('local choice contract anchor not found')
    contract = contract.replace(console, addition + '\n' + console, 1)
    contract_path.write_text(contract, encoding='utf-8')
