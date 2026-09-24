import fs from 'node:fs';

const broker = fs.readFileSync('src/HelpSys.Desktop/Services/ObservationBroker.cs', 'utf8');
const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');

function need(source, fragment, message) {
  if (!source.includes(fragment)) throw new Error(message + ': missing ' + fragment);
}

need(broker, '"StartMenuExperienceHost"', 'Start host must be part of the logical Windows shell');
need(broker, '"SearchHost"', 'Search host must be part of the logical Windows shell');
need(broker, '"ShellExperienceHost"', 'ShellExperienceHost must be included');
need(broker, '"TextInputHost"', 'TextInputHost must be included for Start/Search input');
need(broker, '"outlaw_shell_uia_fused"', 'Cross-process shell fusion must be logged');
need(broker, 'HasSameShellSurface(before, after)', 'Shell host handoffs must trigger a bounded rebound scan');
need(broker, '? "windows-shell"', 'Shell fingerprint must not depend on transient shell PID/HWND');
need(scanner, '"ShellExperienceHost"', 'UIA scanner must enumerate ShellExperienceHost roots');
need(scanner, '"TextInputHost"', 'UIA scanner must enumerate TextInputHost roots');

console.log('Outlaw 3.2.1 Windows shell surface contract passed.');
