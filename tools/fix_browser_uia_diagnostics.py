from pathlib import Path
import subprocess


def replace_once(text: str, old: str, new: str, marker: str, label: str) -> str:
    if marker in text:
        return text
    if old not in text:
        raise SystemExit(f'{label} anchor not found')
    return text.replace(old, new, 1)


scanner_path = Path('src/HelpSys.Desktop/Services/UiAutomationScanner.cs')
scanner = scanner_path.read_text(encoding='utf-8')
scanner_anchor = '''    public Task<UiElementCandidate?> RevalidateCandidateAsync(UiElementCandidate candidate, CancellationToken cancellationToken = default)
        => Task.Run(() => RevalidateCandidate(candidate, null, cancellationToken), cancellationToken);
'''
scanner_add = '''    public Task<string> CaptureWindowDiagnosticsAsync(
        nint windowHandle,
        int expectedProcessId,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == nint.Zero) return Task.FromResult("hwnd=missing");
        return Task.Run(() => CaptureWindowDiagnostics(windowHandle, expectedProcessId, cancellationToken), cancellationToken);
    }

    private static string CaptureWindowDiagnostics(nint windowHandle, int expectedProcessId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = AutomationElement.FromHandle((IntPtr)windowHandle);
        if (root is null) return $"expectedPid={expectedProcessId};root=missing";

        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var visible = 0;
        var enabled = 0;
        var focusable = 0;
        var knownInteractive = 0;
        var unclassifiedFocusable = 0;
        var processIds = new HashSet<int>();
        var controlCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < 7500 && stopwatch.Elapsed < TimeSpan.FromMilliseconds(2200))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            visited++;
            try
            {
                var current = element.Current;
                if (current.ProcessId > 0) processIds.Add(current.ProcessId);
                var typeName = current.ControlType?.ProgrammaticName ?? "ControlType.Unknown";
                controlCounts[typeName] = controlCounts.TryGetValue(typeName, out var count) ? count + 1 : 1;

                var isVisible = !current.IsOffscreen && !current.BoundingRectangle.IsEmpty;
                if (isVisible) visible++;
                if (current.IsEnabled) enabled++;
                if (current.IsKeyboardFocusable) focusable++;
                var isKnownInteractive = IsInteractiveType(typeName);
                if (isKnownInteractive) knownInteractive++;
                if (isVisible && current.IsEnabled && current.IsKeyboardFocusable && !isKnownInteractive)
                    unclassifiedFocusable++;

                if (depth < 12) EnqueueChildren(walker, element, depth + 1, queue);
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
        }

        var pidSummary = processIds.Count == 0
            ? "none"
            : string.Join(',', processIds.OrderBy(x => x));
        var typeSummary = string.Join(',', controlCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(16)
            .Select(x => $"{x.Key.Replace("ControlType.", string.Empty, StringComparison.Ordinal)}:{x.Value}"));

        return $"expectedPid={expectedProcessId};pids={pidSummary};crossProcess={processIds.Any(x => x != expectedProcessId)};visited={visited};visible={visible};enabled={enabled};focusable={focusable};knownInteractive={knownInteractive};unclassifiedFocusable={unclassifiedFocusable};types={typeSummary}";
    }

''' + scanner_anchor
scanner = replace_once(
    scanner,
    scanner_anchor,
    scanner_add,
    'public Task<string> CaptureWindowDiagnosticsAsync(',
    'scanner diagnostics')
scanner_path.write_text(scanner, encoding='utf-8')


main_path = Path('src/HelpSys.Desktop/MainWindow.xaml.cs')
main = main_path.read_text(encoding='utf-8')
main_anchor = '    private readonly GuidanceSessionController _sessionState = new();\n'
main_add = main_anchor + '    private readonly DiagnosticModePolicy _diagnosticMode = new();\n'
main = replace_once(
    main,
    main_anchor,
    main_add,
    'private readonly DiagnosticModePolicy _diagnosticMode = new();',
    'diagnostic mode field')
main_path.write_text(main, encoding='utf-8')


quality_path = Path('src/HelpSys.Desktop/MainWindow.QualityFirst.cs')
quality = quality_path.read_text(encoding='utf-8')
if not quality.startswith('using System.Diagnostics;'):
    quality = 'using System.Diagnostics;\n' + quality
quality_anchor = '''            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);
'''
quality_add = '''            var candidates = await _scanner.CaptureCandidatesForProcessAsync(systemContext.ForegroundProcessId, 420, cancellationToken);
            if (!_sessionState.IsCurrent(generation)) return;

            if (_diagnosticMode.Enabled && systemContext.Browser is not null && systemContext.ForegroundWindowHandle != nint.Zero)
            {
                try
                {
                    var diagnostic = await _scanner.CaptureWindowDiagnosticsAsync(
                        systemContext.ForegroundWindowHandle,
                        systemContext.ForegroundProcessId,
                        cancellationToken);
                    Debug.WriteLine($"[HelpSys:UIA] processCandidates={candidates.Count};{diagnostic}");
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            var structuralEvidence = GuidanceEvidenceService.Build(false, candidates, _history, systemContext);
'''
quality = replace_once(
    quality,
    quality_anchor,
    quality_add,
    'Debug.WriteLine($"[HelpSys:UIA] processCandidates={candidates.Count};{diagnostic}")',
    'quality diagnostics hook')
quality_path.write_text(quality, encoding='utf-8')


contract_path = Path('tests/browser-uia-diagnostics-contract.mjs')
contract_path.write_text(r'''import fs from 'node:fs';

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
''', encoding='utf-8')

package_path = Path('package.json')
package = package_path.read_text(encoding='utf-8')
if 'node --check tests/browser-uia-diagnostics-contract.mjs' not in package:
    package = package.replace(
        'node --check tests/recovery-taxonomy-contract.mjs',
        'node --check tests/recovery-taxonomy-contract.mjs && node --check tests/browser-uia-diagnostics-contract.mjs',
        1)
if 'node tests/browser-uia-diagnostics-contract.mjs' not in package:
    package = package.replace(
        'node tests/recovery-taxonomy-contract.mjs',
        'node tests/recovery-taxonomy-contract.mjs && node tests/browser-uia-diagnostics-contract.mjs',
        1)
package_path.write_text(package, encoding='utf-8')

subprocess.run([
    'git', 'add',
    'src/HelpSys.Desktop/Services/UiAutomationScanner.cs',
    'package.json',
    'tests/browser-uia-diagnostics-contract.mjs'
], check=True)
