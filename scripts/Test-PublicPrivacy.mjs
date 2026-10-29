import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

const root = resolve(process.argv[2] ?? process.cwd());
function git(args) {
  const result = spawnSync('git', args, { cwd: root, windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
  if (result.status !== 0) throw new Error('Git index could not be inspected.');
  return result.stdout;
}
const entries = git(['ls-files', '--stage', '-z']).toString('utf8').split('\0').filter(Boolean);
const findings = [];
const report = (path, rule) => findings.push({ path, rule });
const placeholderHost = host => /(^|\.)(invalid|example\.com|example\.org|example\.net)$/i.test(host);
for (const entry of entries) {
  const match = /^(100644|100755) ([a-f0-9]{40,64}) 0\t(.+)$/s.exec(entry);
  if (!match) { report('(index)', 'unsupported-file-mode-or-unmerged-entry'); continue; }
  const [, , object, path] = match;
  if (/(^|\/)(?:\.deps|\.cache|node_modules|artifacts|tasks)(?:\/|$)/i.test(path) ||
      /(^|\/)(?:api\.dat|DanmuCompanion\.ps1|Start-DanmuCompanion\.ps1|启动弹幕助手\.bat|HANDOFF[^/]*|\.env(?:\.(?!example$).*)?)$/i.test(path) ||
      /\.(?:exe|dll|pdb|zip|pfx|p12|pem|key)$/i.test(path)) report(path, 'private-or-generated-file');
  const bytes = git(['cat-file', 'blob', object]);
  if (bytes.length > 2 * 1024 * 1024) { report(path, 'unexpected-large-source-file'); continue; }
  const text = bytes.toString('utf8');
  if (/\b(?:ghp_|github_pat_)[a-zA-Z0-9_]{20,}\b/.test(text)) report(path, 'github-token');
  if (/-----BEGIN(?: [A-Z]+)* PRIVATE KEY-----/.test(text)) report(path, 'private-key');
  if (/[A-Z]:[\\/]+Users[\\/]+[^\\/\s"']+|\/Users\/[^/\s"']+|\/home\/[^/\s"']+/i.test(text)) report(path, 'personal-home-path');
  for (const address of text.matchAll(/\b[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})\b/g)) {
    if (path === 'tests/Test-BridgeBoundary.ps1' && address[0] === ['password', 'danmu.local'].join('@')) continue;
    if (!path.startsWith('licenses/') && !placeholderHost(address[1]) && address[1] !== 'users.noreply.github.com') {
      report(path, 'non-placeholder-email'); break;
    }
  }
  for (const candidate of text.matchAll(/https?:\/\/[^\s"'<>`\\)]+/g)) {
    let url;
    try { url = new URL(candidate[0]); } catch { continue; }
    // This exact synthetic userinfo case proves that the virtual-origin guard rejects credentials.
    if (path === 'tests/Test-BridgeBoundary.ps1' && url.hostname === 'danmu.local' && url.username === 'user' && url.password === 'password') continue;
    if (placeholderHost(url.hostname)) continue;
    if (url.username || url.password) report(path, 'url-credentials');
    if (/^(?:10\.|192\.168\.|172\.(?:1[6-9]|2\d|3[01])\.)/.test(url.hostname)) report(path, 'private-network-url');
  }
}
if (entries.length === 0) report('(index)', 'empty-index');
console.log(JSON.stringify({ passed: findings.length === 0, scannedFiles: entries.length, findings,
  scope: 'Git index source checks; no network, no matched secret values in output.' }, null, 2));
process.exitCode = findings.length ? 1 : 0;
