import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readdir, lstat } from 'node:fs/promises';
import { join } from 'node:path';

export function releaseNames(version) {
  if (!/^\d+\.\d+\.\d+-beta\.[1-9]\d*$/.test(version)) throw new Error('Invalid beta version.');
  const base = `DanmuCompanion-${version}-win-x64`;
  return { base, installer: `${base}-setup.exe`, portable: `${base}-portable.zip` };
}

export function allowedEntry(path) {
  if (!path || path.startsWith('/') || /[\\:\x00-\x1f<>"|?*]/.test(path)) return false;
  const parts = path.split('/');
  if (parts.some(part => !part || part === '.' || part === '..' || /[. ]$/.test(part) ||
    /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(part))) return false;
  if (parts.some(part => /^(?:pwsh|powershell|System\.Management\.Automation|Microsoft\.PowerShell)(?:\.|$)/i.test(part))) return false;
  if (parts[0].toLowerCase() === 'webview2runtime') return false;
  if (/\.(?:ps1|psm1|psd1|bat|cmd|pdb|cs|csproj|lnk)$/i.test(path)) return false;
  return /^(DanmuCompanion(?:\.Probe)?\.exe|MicrosoftEdgeWebview2Setup\.exe|createdump\.exe|README\.md|LICENSE|DISTRIBUTION-LICENSE\.txt|THIRD-PARTY-NOTICES\.md|install-files\.txt|manifest\.json)$/.test(path) ||
    /^DanmuCompanion(?:\.Probe)?\.(?:deps|runtimeconfig)\.json$/.test(path) ||
    /^(?:[a-z]{2}(?:-[A-Za-z0-9]+)?\/)?[A-Za-z0-9_.-]+\.dll$/.test(path) ||
    /^assets\/[A-Za-z0-9_.-]+\.(?:ico|png|svg)$/.test(path) ||
    /^ui\/dist\/(?:index\.html|favicon\.svg|assets\/[A-Za-z0-9_.-]+\.(?:js|css|svg|png|jpg|webp|woff2))$/.test(path) ||
    /^licenses\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.(?:txt|rtf|json|nuspec|html|md)$/.test(path);
}

export function assertPayloadBudget(records, extraBytes = 0) {
  let total = extraBytes;
  for (const file of records) {
    if (!Number.isSafeInteger(file.length) || file.length < 0) throw new Error('Invalid payload length.');
    total += file.length;
  }
  if (!Number.isSafeInteger(total) || total < 0 || total > 210 * 1024 * 1024) throw new Error('Shared browser payload exceeds its 210 MiB budget.');
  return total;
}

export async function fileHash(path, algorithm = 'sha256') {
  const hash = createHash(algorithm);
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

export async function walkFiles(root, prefix = '') {
  const stat = await lstat(root);
  if (stat.isSymbolicLink() || !stat.isDirectory()) throw new Error('Payload root must be a real directory.');
  const result = [];
  for (const entry of await readdir(root, { withFileTypes: true })) {
    const relative = prefix + entry.name;
    if (entry.isSymbolicLink()) throw new Error('Reparse payload paths are forbidden.');
    if (entry.isDirectory()) result.push(...await walkFiles(join(root, entry.name), relative + '/'));
    else if (entry.isFile()) result.push(relative);
    else throw new Error('Unsupported payload file kind.');
  }
  return result.sort();
}

export async function inventory(root) {
  const records = [], seen = new Set();
  for (const path of await walkFiles(root)) {
    if (!allowedEntry(path)) throw new Error(`Forbidden payload entry: ${path}`);
    if (seen.has(path.toLowerCase())) throw new Error('Case-colliding payload names.');
    seen.add(path.toLowerCase());
    if (path === 'manifest.json') continue;
    const file = join(root, path), stat = await lstat(file);
    records.push({ path, length: stat.size, sha256: await fileHash(file) });
  }
  return records;
}
