// SPDX-License-Identifier: AGPL-3.0-only
// This adapter is distributed as part of the AGPL-covered includeAPI program.
import http from 'node:http';
import net from 'node:net';
import dns from 'node:dns';
import dgram from 'node:dgram';
import { syncBuiltinESMExports } from 'node:module';
import { timingSafeEqual } from 'node:crypto';

const COMMIT = '4d325e096bbbf0c1ff261a9bfa638f7c58784f60';
const SOURCES = new Set(['douban', '360', 'tencent', 'youku', 'iqiyi', 'imgo', 'bilibili', 'migu', 'renren', 'hanjutv', 'sohu', 'leshi', 'xigua', 'maiduidui', 'aiyifan', 'hongguo', 'bahamut', 'animeko']);
const MAX_LINE = 4096;
const MAX_URL = 4096;
const MAX_RESPONSE = 64 * 1024 * 1024;

// The host already clears Node startup options. Also clear upstream settings and
// proxy/cookie variables before importing code that may consult process.env.
for (const key of Object.keys(process.env)) {
  if (!['SYSTEMROOT', 'WINDIR', 'SYSTEMDRIVE', 'TEMP', 'TMP', 'PATH'].includes(key.toUpperCase())) delete process.env[key];
}
for (const method of ['log', 'info', 'warn', 'error', 'debug']) console[method] = () => {};

let server;
let stopped = false;
let configured = false;
let startup = Buffer.alloc(0);
const connect = net.Socket.prototype.connect;
let networkEnabled = false;
let activeRequests = 0;
const blocked = () => { throw new Error('Network disabled during startup'); };
function guardMethods(object, names) {
  for (const name of names) {
    const original = object[name];
    if (typeof original !== 'function') continue;
    object[name] = function (...args) { return networkEnabled ? original.apply(this, args) : blocked(); };
  }
}
const dnsMethods = ['lookupService', 'resolve', 'resolve4', 'resolve6', 'resolveAny', 'resolveCaa', 'resolveCname', 'resolveMx', 'resolveNaptr', 'resolveNs', 'resolvePtr', 'resolveSoa', 'resolveSrv', 'resolveTxt', 'reverse'];
for (const object of [dns, dns.promises]) {
  const lookup = object.lookup;
  object.lookup = function (host, ...args) {
    return networkEnabled || host === '127.0.0.1' ? lookup.call(this, host, ...args) : blocked();
  };
}
guardMethods(dns, dnsMethods);
guardMethods(dns.promises, dnsMethods);
guardMethods(dns.Resolver.prototype, dnsMethods);
guardMethods(dns.promises.Resolver.prototype, dnsMethods);
guardMethods(dgram.Socket.prototype, ['send']);
const originalFetch = globalThis.fetch;
globalThis.fetch = (...args) => networkEnabled ? originalFetch(...args) : blocked();
syncBuiltinESMExports();
const deadline = setTimeout(() => fail(), 20000);
function fail() {
  clearTimeout(deadline);
  process.stderr.write('includeAPI startup failed\n');
  process.exit(1);
}
function shutdown() {
  stopped = true;
  clearTimeout(deadline);
  if (!server) return process.exit(0);
  server.close(() => process.exit(0));
  const timer = setTimeout(() => process.exit(0), 1500);
  timer.unref();
}
function validConfig(value) {
  return value && typeof value === 'object' && !Array.isArray(value) &&
    Object.keys(value).sort().join(',') === 'protocol,sources,token' && value.protocol === 1 &&
    typeof value.token === 'string' && /^[a-f0-9]{64}$/.test(value.token) &&
    Array.isArray(value.sources) && value.sources.length >= 1 && value.sources.length <= 18 &&
    value.sources.every(s => typeof s === 'string' && SOURCES.has(s)) && new Set(value.sources).size === value.sources.length;
}
function safeReply(res, status, value) {
  if (res.destroyed || res.headersSent) return;
  const body = JSON.stringify(value);
  res.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'content-length': Buffer.byteLength(body), 'cache-control': 'no-store', 'connection': 'close' });
  res.end(body);
}
function matchesToken(value, token) {
  return value.length === token.length && timingSafeEqual(Buffer.from(value), Buffer.from(token));
}
function permitted(req, token, port) {
  if (req.method !== 'GET' || req.headers.host !== `127.0.0.1:${port}` || 'origin' in req.headers ||
      req.headers['transfer-encoding'] !== undefined || (req.headers['content-length'] !== undefined && req.headers['content-length'] !== '0')) return null;
  const raw = req.url;
  if (typeof raw !== 'string' || Buffer.byteLength(raw) > MAX_URL || !raw.startsWith('/') || raw.includes('#') || /%(?![\da-fA-F]{2})/.test(raw)) return null;
  const at = raw.indexOf('?');
  const pathname = at < 0 ? raw : raw.slice(0, at);
  const match = /^\/([a-f0-9]{64})(\/.*)$/.exec(pathname);
  if (!match || !matchesToken(match[1], token)) return null;
  const url = new URL(raw, `http://127.0.0.1:${port}`);
  const query = url.searchParams;
  const path = match[2];
  if (path === '/health' && at < 0) return { health: true };
  if (path === '/api/v2/search/anime' && [...query.keys()].length === 1 && query.has('keyword')) {
    const keyword = query.get('keyword');
    if (keyword.trim() && Buffer.byteLength(keyword) <= 512) return { url };
  }
  if (/^\/api\/v2\/bangumi\/\d{1,20}$/.test(path) && at < 0) return { url };
  if (/^\/api\/v2\/comment\/\d{1,20}$/.test(path) && [...query.keys()].length === 2 &&
      query.get('format') === 'json' && query.get('duration') === 'true' && query.has('format') && query.has('duration')) return { url };
  return null;
}
async function respond(req, res, worker, env, token) {
  const route = permitted(req, token, server.address().port);
  if (!route) return safeReply(res, 403, { success: false });
  if (route.health) return safeReply(res, 200, { success: true, owner: token, upstreamCommit: COMMIT });
  if (activeRequests >= 4) return safeReply(res, 503, { success: false, errorMessage: 'Local API busy' });
  activeRequests++;
  if (!networkEnabled) {
    networkEnabled = true;
    net.Socket.prototype.connect = connect;
  }
  const abort = new AbortController();
  const timeout = setTimeout(() => {
    abort.abort();
    safeReply(res, 504, { success: false, errorMessage: 'Local API timed out' });
  }, 180000);
  res.once('close', () => abort.abort());
  try {
    const request = new Request(route.url, { method: 'GET', headers: { 'cf-connecting-ip': '127.0.0.1' }, signal: abort.signal });
    const response = await worker(request, env, 'node', '127.0.0.1');
    if (!(response instanceof Response)) throw new Error('Invalid worker response');
    const reader = response.body?.getReader();
    const chunks = []; let size = 0;
    if (reader) {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        size += value.byteLength;
        if (size > MAX_RESPONSE) { void reader.cancel().catch(() => {}); throw new Error('Response limit'); }
        chunks.push(Buffer.from(value));
      }
    }
    if (res.destroyed) return;
    res.writeHead(response.status, { 'content-type': response.headers.get('content-type') || 'application/json; charset=utf-8', 'content-length': size, 'cache-control': 'no-store', 'connection': 'close' });
    res.end(Buffer.concat(chunks, size));
  } catch {
    safeReply(res, 502, { success: false, errorMessage: 'Local API request failed' });
  } finally {
    clearTimeout(timeout);
    activeRequests--;
  }
}
async function start(config) {
  // Refuse outbound socket connections until the first permitted business request.
  net.Socket.prototype.connect = blocked;
  const worker = (await import('./upstream/danmu_api/worker.js')).handleRequest;
  if (stopped) return;
  if (typeof worker !== 'function') throw new Error('Missing worker handler');
  const env = Object.freeze({ TOKEN: config.token, SOURCE_ORDER: config.sources.join(',') });
  server = http.createServer({ maxHeaderSize: 8192 }, (req, res) => void respond(req, res, worker, env, config.token));
  server.maxConnections = 32;
  server.headersTimeout = 10000;
  server.requestTimeout = 15000;
  server.on('error', () => fail());
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  if (stopped) return shutdown();
  clearTimeout(deadline);
  process.stdout.write(JSON.stringify({ protocol: 1, port: server.address().port, owner: config.token }) + '\n');
}
process.stdin.on('data', chunk => {
  if (configured) return fail();
  startup = Buffer.concat([startup, chunk]);
  if (startup.length > MAX_LINE) return fail();
  const newline = startup.indexOf(10);
  if (newline < 0) return;
  if (newline !== startup.length - 1) return fail();
  let config;
  try { config = JSON.parse(startup.subarray(0, newline).toString('utf8')); } catch { return fail(); }
  startup = Buffer.alloc(0);
  if (!validConfig(config)) return fail();
  configured = true;
  start(config).catch(() => fail());
});
process.stdin.on('end', shutdown);
process.stdin.on('error', shutdown);
process.on('uncaughtException', fail);
process.on('unhandledRejection', fail);
