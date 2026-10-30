import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readdir, lstat, readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

export function releaseNames(version, includeApi = false) {
  if (!/^\d+\.\d+\.\d+-beta\.[1-9]\d*$/.test(version)) throw new Error('Invalid beta version.');
  const base = `DanmuCompanion-${version}-win-x64${includeApi ? '-includeAPI' : ''}`;
  return { base, installer: `${base}-setup.exe`, portable: `${base}-portable.zip` };
}

export function releaseVersions(properties, version) {
  releaseNames(version);
  const projectVersion = properties.match(/<Version>([^<]+)<\/Version>/)?.[1];
  const fileVersion = properties.match(/<FileVersion>([^<]+)<\/FileVersion>/)?.[1];
  const expected = version.replace('-beta.', '.');
  if (projectVersion !== version || fileVersion !== expected ||
      expected.split('.').some(part => Number(part) > 65535))
    throw new Error('Artifact, native version and numeric file version must agree.');
  return { fileVersion };
}

export function assertReleaseGuide(guide, version) {
  releaseNames(version);
  const heading = guide.replace(/^\ufeff/, '').split(/\r?\n/, 1)[0];
  if (!heading.startsWith('# ') || !heading.includes(version) ||
      (heading.match(/\d+\.\d+\.\d+-beta\.\d+/g) ?? []).some(found => found !== version))
    throw new Error('Release guide title must identify the artifact version.');
}

export function releaseSourceStatus(dirty, customDanAny, allowDirty) {
  if ((dirty || customDanAny) && !allowDirty) throw new Error('Dirty source or custom dan-any requires an explicit --allow-dirty candidate.');
  return { sourceDirty: Boolean(dirty || customDanAny), customDanAny: Boolean(customDanAny) };
}

export function candidateApiDirectory(path, includeApi, allowDirty, explicitOutput) {
  if (path === undefined) return null;
  if (!path || !includeApi || !allowDirty || !explicitOutput) throw new Error('Candidate API input requires --include-api, --allow-dirty and an explicit --output.');
  return resolve(path);
}

export function allowedEntry(path, apiEntries = null) {
  if (!path || path.startsWith('/') || /[\\:\x00-\x1f<>"|?*]/.test(path)) return false;
  const parts = path.split('/');
  if (parts.some(part => !part || part === '.' || part === '..' || /[. ]$/.test(part) ||
    /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(part))) return false;
  if (parts.some(part => /^(?:pwsh|powershell|System\.Management\.Automation|Microsoft\.PowerShell)(?:\.|$)/i.test(part))) return false;
  if (parts[0].toLowerCase() === 'webview2runtime') return false;
  if (/\.(?:ps1|psm1|psd1|bat|cmd|pdb|cs|csproj|lnk)$/i.test(path)) return false;
  if (path.startsWith('api/')) return apiEntries?.has(path) ?? false;
  return /^(DanmuCompanion(?:\.Probe)?\.exe|MicrosoftEdgeWebview2Setup\.exe|createdump\.exe|README\.md|LICENSE|DISTRIBUTION-LICENSE\.txt|THIRD-PARTY-NOTICES\.md|install-files\.txt|manifest\.json)$/.test(path) ||
    /^DanmuCompanion(?:\.Probe)?\.(?:deps|runtimeconfig)\.json$/.test(path) ||
    /^(?:[a-z]{2}(?:-[A-Za-z0-9]+)?\/)?[A-Za-z0-9_.-]+\.dll$/.test(path) ||
    /^assets\/[A-Za-z0-9_.-]+\.(?:ico|png|svg)$/.test(path) ||
    /^ui\/dist\/(?:index\.html|favicon\.(?:svg|ico)|assets\/[A-Za-z0-9_.-]+\.(?:js|css|svg|png|jpg|webp|woff2))$/.test(path) ||
    /^licenses\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.(?:txt|rtf|json|nuspec|html|md)$/.test(path);
}

export function assertPayloadBudget(records, extraBytes = 0, includeApi = false) {
  let total = extraBytes;
  for (const file of records) {
    if (!Number.isSafeInteger(file.length) || file.length < 0) throw new Error('Invalid payload length.');
    total += file.length;
  }
  if (!Number.isSafeInteger(total) || total < 0 || total > (includeApi ? 400 : 210) * 1024 * 1024) throw new Error('Payload exceeds its variant budget.');
  return total;
}

export async function fileHash(path, algorithm = 'sha256') {
  const hash = createHash(algorithm);
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

export async function validateApiProvenance(api, backend, { allowCustom = false } = {}) {
  for (const name of ['entry.mjs', 'package.json', 'package-lock.json', 'toolchain.json']) {
    if (await fileHash(join(api, name)) !== await fileHash(join(backend, name)))
      throw new Error(`Stale includeAPI input differs from current backend: ${name}`);
  }
  const toolchain = JSON.parse(await readFile(join(backend, 'toolchain.json'), 'utf8'));
  const runtime = JSON.parse(await readFile(join(api, 'runtime.json'), 'utf8'));
  if (runtime.customDanAny !== undefined && typeof runtime.customDanAny !== 'boolean') throw new Error('Invalid custom dan-any provenance.');
  if (runtime.customDanAny && !allowCustom)
    throw new Error('Custom dan-any builds cannot be released as official includeAPI artifacts.');
  if (toolchain.schemaVersion !== 1 || runtime.schemaVersion !== 1 || !/^[a-f0-9]{40}$/.test(toolchain.upstream?.commit ?? '') ||
      runtime.upstreamCommit !== toolchain.upstream.commit || runtime.nodeVersion !== toolchain.node?.version) throw new Error('includeAPI runtime provenance mismatch.');
  if (await fileHash(join(api, 'node.exe')) !== toolchain.node?.sha256 ||
      await fileHash(join(api, 'package-lock.json')) !== toolchain.productionLock?.sha256 ||
      await fileHash(join(api, 'package.json')) !== toolchain.packageManifestSha256)
    throw new Error('includeAPI Node or dependency provenance mismatch.');
  return { customDanAny: runtime.customDanAny === true };
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

export async function inventory(root, apiInventory = null) {
  const records = [], seen = new Set();
  const apiEntries = apiInventory == null ? null : new Map();
  if (apiEntries) for (const record of apiInventory) {
    if (!record.path?.startsWith('api/') || !Number.isSafeInteger(record.length) || record.length < 0 ||
        !/^[a-f0-9]{64}$/.test(record.sha256) || apiEntries.has(record.path.toLowerCase())) throw new Error('Invalid API inventory record.');
    apiEntries.set(record.path.toLowerCase(), record);
  }
  for (const path of await walkFiles(root)) {
    const expected = apiEntries?.get(path.toLowerCase());
    if (!allowedEntry(path, expected ? new Set([path]) : null)) throw new Error(`Forbidden payload entry: ${path}`);
    if (seen.has(path.toLowerCase())) throw new Error('Case-colliding payload names.');
    seen.add(path.toLowerCase());
    if (path === 'manifest.json') continue;
    const file = join(root, path), stat = await lstat(file);
    const actual = { path, length: stat.size, sha256: await fileHash(file) };
    if (expected && (expected.path !== path || expected.length !== actual.length || expected.sha256 !== actual.sha256))
      throw new Error(`API inventory hash or length mismatch: ${path}`);
    records.push(actual);
  }
  if (apiEntries && records.filter(record => record.path.startsWith('api/')).length !== apiEntries.size) throw new Error('API inventory contains missing files.');
  return records;
}
