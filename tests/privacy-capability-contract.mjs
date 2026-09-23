import fs from 'node:fs';
import path from 'node:path';

const assert = (condition, message) => { if (!condition) throw new Error(message); };

function walk(dir) {
  const files = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) files.push(...walk(full));
    else if (entry.isFile() && entry.name.endsWith('.cs')) files.push(full.replaceAll('\\', '/'));
  }
  return files;
}

const prohibitedApis = [
  { re: /Clipboard\.(?:GetText|GetData|GetDataObject|ContainsText)\s*\(/, label: 'clipboard read API' },
  { re: /\bCred(?:Read|Enumerate|Write|Delete)[AW]?\s*\(/, label: 'Windows Credential Manager API' },
  { re: /\bPasswordVault\b/, label: 'Windows PasswordVault' },
  { re: /\bPasswordCredential\b/, label: 'Windows PasswordCredential' },
  { re: /ProtectedData\.Unprotect\s*\(/, label: 'DPAPI credential decryption' },
  { re: /Windows\.Security\.Credentials/, label: 'Windows credential namespace' },
  { re: /File\.(?:WriteAllBytes|WriteAllText|WriteAllLines|AppendAllText|AppendAllLines)\s*\(/, label: 'automatic file persistence API' },
  { re: /new\s+StreamWriter\s*\(/, label: 'automatic stream/file writer' },
  { re: /File\.OpenWrite\s*\(/, label: 'automatic file write stream' },
  { re: /new\s+FileStream\s*\([^\n]*(?:FileMode\.(?:Create|CreateNew|Append|OpenOrCreate)|FileAccess\.Write)/, label: 'automatic writable FileStream' },
  { re: /\bSendInput\s*\(/, label: 'Windows input injection API' },
  { re: /\bmouse_event\s*\(/, label: 'legacy mouse injection API' },
  { re: /\bkeybd_event\s*\(/, label: 'legacy keyboard injection API' },
  { re: /\bSetCursorPos\s*\(/, label: 'automatic cursor movement API' },
  { re: /\bSendKeys\s*\.\s*Send(?:Wait)?\s*\(/, label: 'synthetic keyboard input API' },
  { re: /\bInputSimulator\b|WindowsInput\.Native/, label: 'input simulation library' },
  { re: /\bIUIAutomationInvokePattern\b|\bIUIAutomationValuePattern\b/, label: 'UI Automation action pattern interface' }
];

const browserSecretStoreTerms = [
  'Login Data', 'Web Data', 'Network\\Cookies', 'Network/Cookies', 'Cookies',
  'Local Storage', 'Session Storage', 'Local State'
];

const updaterPath = 'src/HelpSys.Desktop/Services/UpdateService.cs';
const updaterWriteLabels = new Set(['automatic file persistence API', 'automatic writable FileStream']);
for (const file of walk('src/HelpSys.Desktop')) {
  const source = fs.readFileSync(file, 'utf8');
  for (const { re, label } of prohibitedApis) {
    if (file === updaterPath && updaterWriteLabels.has(label)) continue;
    assert(!re.test(source), `${label} is prohibited in HelpSys Desktop: ${file}`);
  }

  for (const line of source.split(/\r?\n/)) {
    const performsFileRead = /File\.(?:ReadAllBytes|ReadAllText|ReadAllLines|OpenRead|Open)\s*\(/.test(line) ||
      /new\s+FileStream\s*\(/.test(line) || /SQLiteConnection|SqliteConnection/.test(line);
    if (!performsFileRead) continue;
    for (const term of browserSecretStoreTerms)
      assert(!line.includes(term), `Browser secret-store access is prohibited (${term}): ${file}`);
  }
}

const lowerScanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
const scannerFacade = fs.readFileSync('src/HelpSys.Desktop/UiAutomationScanner.cs', 'utf8');
const observerHost = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationObserverHost.cs', 'utf8');
const watcher = fs.readFileSync('src/HelpSys.Desktop/Services/GuidanceStateWatcher.cs', 'utf8');

assert(lowerScanner.includes('isPassword ? "[password field]"'), 'Password accessibility names must remain minimized inside the isolated observer scanner.');
assert(lowerScanner.includes('ValuePattern.Pattern') && lowerScanner.includes('if (!isPassword'), 'Observer may retain bounded ordinary input evidence but must exclude password values.');
assert(lowerScanner.includes('rawValue.Length <= 320'), 'Locally retained ordinary input evidence must remain bounded.');
assert(scannerFacade.includes('HelpSys.Services.UiAutomationScanner'), 'MainWindow UIA facade must delegate to the isolated observer implementation.');
assert(observerHost.includes('ApartmentState.MTA'), 'All provider-facing UI Automation work must run on one dedicated MTA thread.');
assert(observerHost.includes('BlockingCollection<WorkItem>'), 'Observer requests must be serialized onto the dedicated automation thread.');
assert(!watcher.includes('System.Windows.Automation') && watcher.includes('SetWinEventHook('), 'Guidance watcher must use WinEvent signals rather than in-process UIA subscriptions.');

const automationOwners = walk('src/HelpSys.Desktop')
  .filter(file => fs.readFileSync(file, 'utf8').includes('System.Windows.Automation'));
assert(
  automationOwners.length === 1 && automationOwners[0] === 'src/HelpSys.Desktop/Services/UiAutomationScanner.cs',
  `UI Automation must have a single desktop-code owner: ${automationOwners.join(', ')}`);

const contextModel = fs.readFileSync('src/HelpSys.Desktop/Models/SystemContextSnapshot.cs', 'utf8');
assert(contextModel.includes('public string? Url { get; init; } = MinimizeUrl(Url);'), 'Browser URL must be minimized at the snapshot storage boundary.');
assert(contextModel.includes('Origin only. UserInfo, path, query and fragment are deliberately discarded.'), 'HTTP(S) browser snapshots must explicitly discard path/query/fragment data.');
assert(contextModel.includes('return $"{scheme}://passwords";') && contextModel.includes('return $"{scheme}://localstorage";'), 'Sensitive internal browser routes must collapse to PrivacyGate-recognized danger categories.');
assert(contextModel.includes('return "unparseable";'), 'Malformed non-empty browser addresses must retain a local marker.');
assert(contextModel.includes('ForegroundWindowHandle'), 'System context must carry the locally verified foreground HWND without sending it to cloud evidence.');

const systemContext = fs.readFileSync('src/HelpSys.Desktop/Services/SystemContextService.cs', 'utf8');
assert(systemContext.includes('EventSystemForeground = 0x0003'), 'System context must observe Windows foreground-change events.');
assert(systemContext.includes('SetWinEventHook('), 'System context must track a real external foreground HWND instead of inferring one from Z-order.');
assert(systemContext.includes('WineventSkipownprocess'), 'Foreground tracking must skip HelpSys own-process events.');
assert(systemContext.includes('_lastExternalForeground'), 'System context must retain the last verified external foreground window.');
assert(systemContext.includes('ResolveVerifiedShellDesktopWindow'), 'System context must have an explicit verified Windows desktop fallback.');
assert(systemContext.includes('GetShellWindow()'), 'Desktop fallback must use the OS shell handle instead of guessing a behind-window surface.');
assert(systemContext.includes('ProcessName.Equals("explorer"'), 'Shell fallback must verify that the desktop handle belongs to Explorer.');
assert(!systemContext.includes('GwHwndNext'), 'System context must not walk behind HelpSys and guess the work surface from Z-order.');
assert(!systemContext.includes('GetWindow(cursor'), 'System context must not select an unrelated notification merely because it sits behind HelpSys.');
assert(!systemContext.includes('System.Windows.Automation') && !systemContext.includes('AutomationElement'), 'System context must not access UI Automation directly.');
assert(systemContext.includes('EnrichWithObservedElements'), 'Browser context must be derived from the already-observed snapshot.');
assert(systemContext.includes('role:browser_address'), 'Browser address semantics must come from observer candidates.');

const sentinel = fs.readFileSync('src/HelpSys.Desktop/MainWindow.PrivacySentinel.cs', 'utf8');
assert(sentinel.includes('PrivacySentinelEventSystemForeground = 0x0003'), 'Privacy Sentinel must observe every real foreground transition.');
assert(sentinel.includes('PrivacySentinelSkipOwnProcess'), 'Privacy Sentinel must ignore HelpSys own foreground events.');
assert(sentinel.includes('PrivacySentinel_AllowCloudAction'), 'Cloud-starting UI actions must have an immediate context-only privacy preflight.');
assert(sentinel.includes('_cloudGuide.PreflightPrivacy(context, Array.Empty<UiElementCandidate>())'), 'Privacy Sentinel must reuse the centralized Privacy Gate instead of duplicating policy.');
assert(sentinel.includes('e.Handled = true;'), 'Unsafe Guide/answer/voice/Commander starts must be intercepted before original handlers run.');
assert(sentinel.includes('foreground_transition_unverified'), 'Foreground-hook disagreement must fail closed as UNKNOWN.');
assert(sentinel.includes('_voiceCts?.Cancel()') && sentinel.includes('_commanderInteractionCts?.Cancel()'), 'Dangerous foreground transitions must cancel active cloud speech interactions.');
assert(sentinel.includes('_commander.SetEnabled(false)'), 'Commander wake monitoring must stop while Privacy Mode protects a dangerous screen.');

const capture = fs.readFileSync('src/HelpSys.Desktop/ScreenCaptureService.cs', 'utf8');
const redactionPolicy = fs.readFileSync('src/HelpSys.Desktop/Services/ScreenshotRedactionPolicy.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');

assert(!capture.includes('System.Windows.Automation') && !capture.includes('AutomationElement'), 'Screenshot capture must never perform a second UIA tree walk.');
assert(capture.includes('GwHwndPrev = 3'), 'Screenshot privacy must inspect windows above the selected target in Z-order.');
assert(capture.includes('var occludersBefore') && capture.includes('var occludersAfter'), 'Occluders must be checked before and after pixel capture.');
assert(capture.indexOf('var occludersBefore') < capture.indexOf('BitBlt('), 'Occluders must be checked before desktop pixels are copied.');
assert(capture.indexOf('var occludersAfter') > capture.indexOf('BitBlt('), 'Occluders must be rechecked after capture to close the race window.');
assert(capture.includes('count >= 320 || !visited.Add(hwnd)'), 'Z-order enumeration must remain bounded and cycle-safe.');
assert(capture.includes('int expectedProcessId') && capture.includes('nint expectedWindowHandle'), 'Screenshot capture must require both process identity and exact HWND.');
assert(capture.includes('targetPid <= 0 || targetPid != expectedProcessId'), 'Screenshot capture must reject an HWND whose process does not match the expected process.');
assert(redactionPolicy.includes('if (element.Password) return true;'), 'Snapshot redaction policy must always redact password elements.');
assert(redactionPolicy.includes('BearerRegex') && redactionPolicy.includes('PrivateKeyRegex') && redactionPolicy.includes('CardNumberRegex'), 'Snapshot redaction policy must detect high-confidence secret material.');
assert(quality.includes('ScreenshotRedactionPolicy.Build(candidates)'), 'Quality capture must use snapshot-derived redaction rectangles.');
assert(quality.includes('candidateProcessIds.Length > 1'), 'Normal mixed-process guidance candidates must still prevent screenshot creation.');
assert(quality.includes('expectedContext.ForegroundWindowHandle'), 'Quality capture must pass the verified foreground HWND into the capture service.');

const updater = fs.readFileSync(updaterPath, 'utf8');
assert(updater.includes('RequiredTag = "preview-latest"'), 'Updater must stay on the reviewed release channel.');
assert(updater.includes('AllowedDownloadHosts'), 'Updater downloads must be host constrained.');
assert(updater.includes('TryParseSha256') && updater.includes('VerifyDigestAsync'), 'Updater packages must be SHA-256 verified before extraction.');
assert(updater.includes('Path.GetFullPath(Path.Combine(destination, entry.FullName))'), 'Updater must defend against ZIP path traversal.');
assert(updater.includes('Environment.SpecialFolder.LocalApplicationData'), 'Update staging must stay in the per-user HelpSys data area.');
assert(!updater.includes('Clipboard') && !updater.includes('PasswordVault') && !updater.includes('Credential'), 'Updater must not read user secrets.');

console.log('HelpSys prohibited-capability, balanced privacy, verified-desktop and updater contract passed.');
