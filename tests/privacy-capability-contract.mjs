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
  { re: /Windows\.Security\.Credentials/, label: 'Windows credential namespace' }
];

const browserSecretStoreTerms = [
  'Login Data',
  'Web Data',
  'Network\\Cookies',
  'Network/Cookies',
  'Local Storage',
  'Session Storage'
];

for (const file of walk('src/HelpSys.Desktop')) {
  const source = fs.readFileSync(file, 'utf8');
  for (const { re, label } of prohibitedApis)
    assert(!re.test(source), `${label} is prohibited in HelpSys Desktop: ${file}`);

  for (const line of source.split(/\r?\n/)) {
    const performsFileRead = /File\.(?:ReadAllBytes|ReadAllText|OpenRead|Open)\s*\(/.test(line) ||
      /new\s+FileStream\s*\(/.test(line) || /SQLiteConnection|SqliteConnection/.test(line);
    if (!performsFileRead) continue;
    for (const term of browserSecretStoreTerms)
      assert(!line.includes(term), `Browser secret-store access is prohibited (${term}): ${file}`);
  }
}

console.log('HelpSys prohibited-capability contract passed.');
