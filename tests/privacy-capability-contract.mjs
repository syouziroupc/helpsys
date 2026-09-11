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

for (const file of walk('src/HelpSys.Desktop')) {
  const source = fs.readFileSync(file, 'utf8');
  for (const { re, label } of prohibitedApis)
    assert(!re.test(source), `${label} is prohibited in HelpSys Desktop: ${file}`);

  for (const line of source.split(/\r?\n/)) {
    const performsFileRead = /File\.(?:ReadAllBytes|ReadAllText|ReadAllLines|OpenRead|Open)\s*\(/.test(line) ||
      /new\s+FileStream\s*\(/.test(line) || /SQLiteConnection|SqliteConnection/.test(line);
    if (!performsFileRead) continue;
    for (const term of browserSecretStoreTerms)
      assert(!line.includes(term), `Browser secret-store access is prohibited (${term}): ${file}`);
  }
}

const scanner = fs.readFileSync('src/HelpSys.Desktop/Services/UiAutomationScanner.cs', 'utf8');
assert(scanner.includes('isInput ? "[input field]"'), 'Edit/ComboBox accessibility names must be minimized before candidate storage.');
assert(scanner.includes('string.IsNullOrEmpty(valueValue.Current.Value) ? null : "present"'), 'UI input state must be reduced immediately to a fixed presence marker.');
assert(!scanner.includes('var raw = valueValue.Current.Value'), 'Raw UI input text must never be assigned to a local variable.');
assert(!scanner.includes('value = Trim(raw'), 'Raw UI input text must never be retained in UiElementCandidate.');

const capture = fs.readFileSync('src/HelpSys.Desktop/Services/ScreenCaptureService.cs', 'utf8');
const quality = fs.readFileSync('src/HelpSys.Desktop/MainWindow.QualityFirst.cs', 'utf8');
assert(capture.includes('GwHwndPrev = 3'), 'Screenshot privacy must inspect windows above the selected target in Z-order.');
assert(capture.includes('CaptureOccluderBounds(captureArea, cancellationToken)'), 'Screenshot privacy must derive occluder redactions locally.');
assert(capture.indexOf('var occluderRedactionsBefore') < capture.indexOf('BitBlt('), 'Occluders must be checked before the desktop pixels are copied.');
assert(capture.indexOf('var occluderRedactionsAfter') > capture.indexOf('BitBlt('), 'Occluders must be rechecked after capture to close the race window.');
assert(capture.includes('.Concat(occluderRedactionsBefore)') && capture.includes('.Concat(occluderRedactionsAfter)'), 'Both occluder scans must be part of the final redaction set.');
assert(capture.includes('count > maxWindows || !visited.Add(hwnd)'), 'Z-order enumeration must fail closed on overflow or cycles.');
assert(capture.includes('前面に重なった別画面を安全に除外できないため、画面画像は送信しません'), 'Occluder uncertainty must fail closed instead of sending the screenshot.');
assert(capture.includes('if (pid == 0) return false;'), 'Unknown process ownership must never be promoted to a shell/full-monitor capture.');
assert(capture.includes('操作対象のウィンドウを安全に特定できないため、画面画像を送信しません'), 'Unknown screenshot target must fail closed.');
assert(capture.includes('操作対象ウィンドウの領域を取得できないため、画面画像を送信しません'), 'Normal-window bounds failure must fail closed instead of falling back to a monitor capture.');
assert(capture.includes('if (shellSurface)\n            return monitorArea;'), 'Only a positively identified shell surface may use full-monitor capture.');

assert(capture.includes('int expectedProcessId'), 'Screenshot capture must accept the expected foreground process identity.');
assert(capture.includes('FindTopLevelWindowForProcess(expectedProcessId)'), 'Screenshot target selection must bind to the expected process instead of whichever window happens to be behind HelpSys.');
assert(capture.includes('targetProcessId != expectedProcessId'), 'Screenshot capture must reject a selected window whose process does not match the expected process.');
assert(capture.includes('EnumWindows('), 'Process-bound target selection must enumerate top-level windows explicitly.');
assert(quality.includes('candidateProcessIds.Length > 1'), 'Mixed-process guidance candidates must prevent screenshot creation.');
assert(quality.includes('_screenCapture.CaptureAsync(passwordBounds, expectedProcessId, cancellationToken)'), 'Quality and recovery screenshots must pass the process-bound target identity into the capture service.');

console.log('HelpSys prohibited-capability, persistence, input-injection, local-input-minimization and process-bound screenshot contract passed.');
