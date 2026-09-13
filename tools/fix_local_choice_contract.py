from pathlib import Path

path = Path('tests/local-choice-contract.mjs')
text = path.read_text(encoding='utf-8')
old = """if (!local.includes('WaitForClarification(question);\\n        EnsureClarificationUi();'))\n  throw new Error('failed local matching must keep the clarification UI available for retry');\n"""
new = """const retryWait = local.indexOf('WaitForClarification(question);');\nconst retryUi = local.indexOf('EnsureClarificationUi();', retryWait);\nif (retryWait < 0 || retryUi < 0 || retryUi < retryWait)\n  throw new Error('failed local matching must keep the clarification UI available for retry');\n"""
if old not in text and "const retryWait = local.indexOf('WaitForClarification(question);');" not in text:
    raise SystemExit('local-choice retry contract anchor not found')
if old in text:
    text = text.replace(old, new, 1)
path.write_text(text, encoding='utf-8')
