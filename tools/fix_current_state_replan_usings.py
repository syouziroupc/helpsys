from pathlib import Path

path = Path('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs')
text = path.read_text(encoding='utf-8')
required = 'using HelpSys.Models;\nusing HelpSys.Services;\n\n'
if not text.startswith(required):
    text = required + text
path.write_text(text, encoding='utf-8')

contract_path = Path('tests/current-state-replan-contract.mjs')
contract = contract_path.read_text(encoding='utf-8')
marker = "const helper = fs.readFileSync('src/HelpSys.Desktop/MainWindow.CurrentStateReplan.cs', 'utf8');\n"
addition = marker + "\nif (!helper.includes('using HelpSys.Models;') || !helper.includes('using HelpSys.Services;'))\n  throw new Error('current-state replan partial must import model and session-state namespaces');\n"
if 'current-state replan partial must import model and session-state namespaces' not in contract:
    if marker not in contract:
        raise SystemExit('current-state replan helper contract anchor not found')
    contract = contract.replace(marker, addition, 1)
contract_path.write_text(contract, encoding='utf-8')
