import fs from 'node:fs';

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const main = fs.readFileSync('src/HelpSys.Desktop/MainWindow.xaml.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
const policy = fs.readFileSync('src/HelpSys.Desktop/Services/DiagnosticModePolicy.cs', 'utf8');

const start = scanner.indexOf('private static string CaptureWindowDiagnostics(');
const end = scanner.indexOf('private UiElementCandidate? RevalidateCandidate(', start);
if (start < 0 || end < 0) throw new Error('browser UIA diagnostic method is missing');
const diagnostic = scanner.slice(start, end);

if (!diagnostic.includes('AutomationElement.FromHandle((IntPtr)windowHandle)'))
  throw new Error('browser diagnostics must inspect the exact verified HWND');
if (!diagnostic.includes('current.ProcessId') || !diagnostic.includes('current.ControlType') || !diagnostic.includes('current.IsKeyboardFocusable'))
  throw new Error('diagnostics must capture process/control/focusability structure');
for (const contentField of ['current.Name', 'current.AutomationId', 'current.ClassName', 'ValuePattern', 'TextPattern']) {
  if (diagnostic.includes(contentField)) throw new Error(`diagnostics must not capture UI content: ${contentField}`);
}
if (!diagnostic.includes('unclassifiedFocusable') || !diagnostic.includes('crossProcess='))
  throw new Error('diagnostics must expose evidence needed before changing type/PID policy');
if (!main.includes('private readonly DiagnosticModePolicy _diagnosticMode = new();'))
  throw new Error('diagnostics must use the existing explicit opt-in policy');
if (!quality.includes('_diagnosticMode.Enabled && systemContext.Browser is not null'))
  throw new Error('browser UIA diagnostics must be disabled by default and browser-scoped');
if (!quality.includes('Debug.WriteLine($"[HelpSys:UIA]'))
  throw new Error('diagnostics must remain ephemeral debugger output');
if (!policy.includes('HELPSYS_DIAGNOSTIC_MODE') || !policy.includes('RawScreenPersistenceAllowed = false'))
  throw new Error('diagnostics must retain explicit opt-in and no raw-screen persistence');

const interactiveBlock = scanner.slice(scanner.indexOf('private static bool IsInteractiveType'), scanner.indexOf('private static bool IsContextType'));
if (interactiveBlock.includes('Custom') || interactiveBlock.includes('DataItem'))
  throw new Error('diagnostic work must not silently broaden actionable control types');
if (!quality.includes('candidateProcessIds.Length > 1') ||
    !quality.includes('current.ForegroundProcessId != expected.ForegroundProcessId') ||
    !quality.includes('current.ForegroundWindowHandle != expected.ForegroundWindowHandle'))
  throw new Error('diagnostic work must not weaken existing PID/HWND safety boundaries');

console.log('HelpSys browser UIA diagnostics contract passed.');
