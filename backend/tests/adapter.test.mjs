import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, mkdir, cp, rm, writeFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { once } from 'node:events';
import test from 'node:test';

const token = 'a'.repeat(64);
const config = { protocol: 1, token, sources: ['douban', '360', 'renren', 'hanjutv'] };
const root = resolve(import.meta.dirname, '..');
const commit = '4d325e096bbbf0c1ff261a9bfa638f7c58784f60';

async function fixture(t, override = {}) {
  const dir = await mkdtemp(join(tmpdir(), 'includeapi-'));
  await mkdir(join(dir, 'upstream', 'danmu_api'), { recursive: true });
  await cp(join(root, 'entry.mjs'), join(dir, 'entry.mjs'));
  await cp(join(root, 'tests', 'fixtures', 'worker.js'), join(dir, 'upstream', 'danmu_api', 'worker.js'));
  await writeFile(join(dir, 'upstream', 'package.json'), '{"type":"module"}');
  const child = spawn(process.execPath, [join(dir, 'entry.mjs')], {
    cwd: dir, stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, BILIBILI_COOKIE: 'poison', HTTP_PROXY: 'http://127.0.0.1:9', ...override }
  });
  let out = '', err = '';
  child.stdout.on('data', chunk => { out += chunk; });
  child.stderr.on('data', chunk => { err += chunk; });
  t.after(async () => {
    if (child.exitCode === null && child.signalCode === null) { child.kill(); await once(child, 'exit').catch(() => {}); }
    await rm(dir, { recursive: true, force: true });
  });
  const ready = async () => {
    for (let i = 0; i < 200 && !out.includes('\n') && child.exitCode === null; i++) await new Promise(r => setTimeout(r, 25));
    return JSON.parse(out.split('\n')[0]);
  };
  return { child, ready, output: () => ({ out, err }) };
}
function request(port, path, { method = 'GET', headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, path, method, headers }, res => {
      const chunks = []; res.on('data', c => chunks.push(c)); res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString() }));
    });
    req.on('error', reject); req.end();
  });
}

test('gated startup, authenticated loopback health, clean environment, EOF shutdown', async t => {
  const f = await fixture(t);
  await new Promise(r => setTimeout(r, 150));
  assert.equal(f.output().out, '');
  f.child.stdin.write(JSON.stringify(config) + '\n');
  const ready = await f.ready();
  assert.equal(ready.protocol, 1); assert.equal(ready.owner, token); assert.ok(ready.port > 0);
  let r = await request(ready.port, `/${token}/health`);
  assert.deepEqual(JSON.parse(r.body), { success: true, owner: token, upstreamCommit: commit });
  await new Promise(r => setTimeout(r, 200));
  r = await request(ready.port, `/${token}/api/v2/search/anime?keyword=example`, { headers: { 'x-forwarded-for': '1.2.3.4', 'cf-connecting-ip': '5.6.7.8', cookie: 'secret' } });
  const body = JSON.parse(r.body);
  assert.equal(body.forwarded, null); assert.equal(body.cf, '127.0.0.1'); assert.equal(body.cookie, null);
  assert.deepEqual(body.env, { TOKEN: token, SOURCE_ORDER: config.sources.join(',') });
  assert.equal(body.inherited, null); assert.equal(body.proxy, null); assert.equal(body.startupNetworkBlocked, true);
  assert.equal(body.startupDnsBlocked, true); assert.equal(body.startupFetchBlocked, true);
  assert.equal(body.platform, 'node'); assert.equal(body.clientIp, '127.0.0.1');
  f.child.stdin.end(); await once(f.child, 'exit');
  assert.equal(f.child.exitCode, 0);
  assert.equal(f.output().out.trim(), JSON.stringify(ready));
  assert.equal(f.output().err.includes(token), false);
});

test('reject invalid startup JSON and sources before listening', async t => {
  for (const value of ['not json', JSON.stringify({ ...config, token: 'wrong' }), JSON.stringify({ ...config, sources: [] }), JSON.stringify({ ...config, sources: ['douban', 'douban'] }), JSON.stringify({ ...config, sources: ['local'] }), 'x'.repeat(4097)]) {
    const f = await fixture(t); f.child.stdin.write(value + '\n');
    await once(f.child, 'exit'); assert.notEqual(f.child.exitCode, 0); assert.equal(f.output().out, '');
  }
});

test('rejects unauthorized host, origin, methods, routes, malformed parameters and excessive inputs', async t => {
  const f = await fixture(t); f.child.stdin.write(JSON.stringify(config) + '\n'); const { port } = await f.ready();
  for (const [path, options] of [
    ['/health', {}], [`/${'b'.repeat(64)}/health`, {}], [`/${token}/health`, { headers: { host: `evil.test:${port}` } }],
    [`/${token}/health`, { headers: { origin: 'https://evil.test' } }], [`/${token}/health`, { method: 'POST' }],
    [`/${token}/api/env/set`, {}], [`/${token}/api/v2/comment?url=http://evil`, {}],
    [`/${token}/api/v2/comment/1?format=json&duration=true&url=http://evil`, {}],
    [`/${token}/api/v2/comment/1?format=xml&duration=true`, {}], [`/${token}/api/v2/bangumi/12x`, {}],
    [`/${token}/api/v2/bangumi/12?foo=bar`, {}], [`/${token}/api/v2/search/anime?keyword=x&keyword=y`, {}],
    [`/${token}/api/v2/search/anime?keyword=${'a'.repeat(600)}`, {}], [`/${token}/api/v2/search/anime?keyword=x&url=http://evil`, {}],
    [`/${token}/api/v2/search/anime?keyword=x`, { headers: { 'content-length': '1' } }],
    [`/${token}/api/v2/../health`, {}]
  ]) { const result = await request(port, path, options); assert.ok(result.status >= 400, `${path}: ${result.status}`); }
  for (const path of [`/${token}/api/v2/bangumi/123`, `/${token}/api/v2/comment/123?format=json&duration=true`]) assert.equal((await request(port, path)).status, 200);
});

test('preserves upstream status and rejects oversized responses', async t => {
  const f = await fixture(t); f.child.stdin.write(JSON.stringify(config) + '\n'); const { port } = await f.ready();
  assert.equal((await request(port, `/${token}/api/v2/search/anime?keyword=failure`)).status, 429);
  const valid = await request(port, `/${token}/api/v2/search/anime?keyword=large-valid`);
  assert.equal(valid.status, 200); assert.equal(Buffer.byteLength(valid.body), 17130234);
  const large = await request(port, `/${token}/api/v2/search/anime?keyword=oversized`);
  assert.equal(large.status, 502); assert.ok(large.body.length < 1024);
});




test('caps simultaneous business work even if clients disconnect', async t => {
  const f = await fixture(t); f.child.stdin.write(JSON.stringify(config) + '\n'); const { port } = await f.ready();
  const clients = [];
  for (let i = 0; i < 4; i++) {
    const req = http.get({ host: '127.0.0.1', port, path: `/${token}/api/v2/search/anime?keyword=hang` });
    req.on('error', () => {}); clients.push(req);
  }
  t.after(() => clients.forEach(c => c.destroy()));
  await new Promise(r => setTimeout(r, 300));
  clients.forEach(c => c.destroy());
  assert.equal((await request(port, `/${token}/api/v2/search/anime?keyword=example`)).status, 503);
  assert.equal((await request(port, `/${token}/health`)).status, 200);
});
