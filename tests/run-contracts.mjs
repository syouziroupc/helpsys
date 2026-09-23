import { spawnSync } from 'node:child_process';

const commands = [
  ['node', ['worker/selftest.mjs']],
  ['node', ['worker/policy-selftest.mjs']],
  ['node', ['worker/reliability-v4-selftest.mjs']],
  ['node', ['worker/quality-guide-selftest.mjs']],
  ['node', ['worker/transcribe-selftest.mjs']],
  ['node', ['tests/privacy-contract.mjs']],
  ['node', ['tests/privacy-capability-contract.mjs']],
  ['node', ['tests/privacy-resume-contract.mjs']],
  ['node', ['tests/safe-edition-contract.mjs']],
  ['node', ['tests/worker-data-handling-contract.mjs']],
  ['node', ['tests/site-release-contract.mjs']],
  ['node', ['tests/why5-root-contract.mjs']]
];

for (const [command, args] of commands) {
  const label = [command, ...args].join(' ');
  process.stdout.write(`\n=== ${label} ===\n`);
  const result = spawnSync(command, args, {
    stdio: 'inherit',
    shell: false,
    windowsHide: true
  });

  if (result.error) {
    console.error(`Failed to execute ${label}: ${result.error.message}`);
    process.exit(1);
  }
  if (result.status !== 0) {
    console.error(`${label} failed with exit code ${result.status ?? 'unknown'}`);
    process.exit(result.status ?? 1);
  }
}

console.log('\nAll HelpSys contract/self-tests passed.');
