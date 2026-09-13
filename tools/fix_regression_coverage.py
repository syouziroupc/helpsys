from pathlib import Path
import subprocess

path = Path('package.json')
text = path.read_text(encoding='utf-8')

check_anchor = 'node --check worker/quality-guide-selftest.mjs'
check_addition = 'node --check worker/quality-guide-selftest.mjs && node --check worker/policy-selftest.mjs && node --check tests/local-choice-contract.mjs && node --check tests/current-state-replan-contract.mjs && node --check tests/live-replan-owner-contract.mjs && node --check tests/recovery-taxonomy-contract.mjs'
if 'node --check worker/policy-selftest.mjs' not in text:
    if check_anchor not in text:
        raise SystemExit('package check anchor not found')
    text = text.replace(check_anchor, check_addition, 1)

test_anchor = 'node worker/quality-guide-selftest.mjs'
test_addition = 'node worker/quality-guide-selftest.mjs && node worker/policy-selftest.mjs && node tests/local-choice-contract.mjs && node tests/current-state-replan-contract.mjs && node tests/live-replan-owner-contract.mjs && node tests/recovery-taxonomy-contract.mjs'
if 'node worker/policy-selftest.mjs' not in text:
    if test_anchor not in text:
        raise SystemExit('package test anchor not found')
    text = text.replace(test_anchor, test_addition, 1)

path.write_text(text, encoding='utf-8')
subprocess.run(['git', 'add', 'package.json'], check=True)
