import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, rm, stat, writeFile } from 'node:fs/promises';
import { once } from 'node:events';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const api = resolve(import.meta.dirname, '../../.deps/includeapi/payload/api');
const node = join(api, 'node.exe');
assert.equal(spawnSync(node, ['--version'], { encoding: 'utf8' }).stdout.trim(), 'v22.23.4');
const temp = await mkdtemp(join(tmpdir(), 'includeapi-real-'));
let child;
try {
  const marker = join(temp, 'network-attempt');
  const guard = join(temp, 'network-guard.mjs');
  await writeFile(guard, `
    import net from 'node:net'; import dns from 'node:dns'; import dgram from 'node:dgram';
    import { syncBuiltinESMExports } from 'node:module'; import { writeFileSync } from 'node:fs';
    const block = () => { writeFileSync(${JSON.stringify(marker)}, 'blocked'); throw Error('network blocked by test'); };
    net.Socket.prototype.connect = block; dgram.Socket.prototype.send = block; globalThis.fetch = block;
    const lookup = dns.lookup; dns.lookup = function(host, ...args) { return host === '127.0.0.1' ? lookup.call(this, host, ...args) : block(); };
    for (const key of Object.keys(dns)) if (/^resolve|^reverse$|^lookupService$/.test(key) && typeof dns[key] === 'function') dns[key] = block;
    syncBuiltinESMExports();
  `);
  child = spawn(node, ['--max-old-space-size=256', '--import', pathToFileURL(guard).href, join(api, 'entry.mjs')], {
    cwd: temp, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
    env: { ...process.env, BILIBILI_COOKIE: 'poison', HTTP_PROXY: 'http://127.0.0.1:9', NODE_OPTIONS: '' }
  });
  let out = '', err = '';
  child.stdout.on('data', chunk => { out += chunk; }); child.stderr.on('data', chunk => { err += chunk; });
  const token = 'a'.repeat(64);
  child.stdin.write(JSON.stringify({ protocol: 1, token, sources: ['douban', '360', 'renren', 'hanjutv'] }) + '\n');
  for (let i = 0; i < 300 && !out.includes('\n') && child.exitCode === null; i++) await new Promise(r => setTimeout(r, 100));
  assert.ok(out.includes('\n'), `worker failed to start (exit ${child.exitCode}): ${err}`);
  const ready = JSON.parse(out.trim());
  assert.equal(ready.protocol, 1); assert.equal(ready.owner, token); assert.ok(ready.port > 0);
  const body = await new Promise((resolve, reject) => {
    const req = http.get({ host: '127.0.0.1', port: ready.port, path: `/${token}/health` }, res => {
      let text = ''; res.setEncoding('utf8'); res.on('data', chunk => { text += chunk; });
      res.on('end', () => { if (res.statusCode === 200) resolve(JSON.parse(text)); else reject(Error(`health status ${res.statusCode}`)); });
    }); req.on('error', reject);
  });
  assert.deepEqual(body, { success: true, owner: token, upstreamCommit: '4d325e096bbbf0c1ff261a9bfa638f7c58784f60' });
  await new Promise(r => setTimeout(r, 1000));
  await assert.rejects(stat(marker), { code: 'ENOENT' });
  const missing = await new Promise((resolve, reject) => {
    const req = http.get({ host: '127.0.0.1', port: ready.port, path: `/${token}/api/v2/bangumi/123` }, res => {
      let text = ''; res.setEncoding('utf8'); res.on('data', chunk => { text += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, body: JSON.parse(text) }));
    }); req.on('error', reject);
  });
  assert.equal(missing.status, 404); assert.equal(missing.body.success, false);
  assert.equal(missing.body.errorMessage, 'Anime not found');
  await assert.rejects(stat(marker), { code: 'ENOENT' });
  child.stdin.end(); await once(child, 'exit');
  assert.equal(child.exitCode, 0); assert.equal(out.trim(), JSON.stringify(ready)); assert.ok(!err.includes(token));
  console.log('PASS pinned Node v22.23.4 and real worker import/health/missing-bangumi, outbound network guard, stdin EOF');
} finally {
  if (child && child.exitCode === null && child.signalCode === null) { child.kill(); await once(child, 'exit').catch(() => {}); }
  await rm(temp, { recursive: true, force: true });
}
