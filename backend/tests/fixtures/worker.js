import net from 'node:net';
import dns from 'node:dns';
let startupNetworkBlocked = false, startupDnsBlocked = false, startupFetchBlocked = false;
setTimeout(() => {
  try { const s = net.createConnection(9, '127.0.0.1'); s.on('error', () => {}); } catch { startupNetworkBlocked = true; }
  try { dns.lookup('example.invalid', () => {}); } catch { startupDnsBlocked = true; }
  try { const pending = fetch('https://example.invalid'); Promise.resolve(pending).catch(() => {}); } catch { startupFetchBlocked = true; }
}, 75);
console.log('upstream startup log must not be stdout');
console.error('upstream startup error must not leak');
export async function handleRequest(request, env, platform, clientIp) {
    const url = new URL(request.url);
    if (url.searchParams.get('keyword') === 'hang') return new Promise(() => {});
    if (url.searchParams.get('keyword') === 'large-valid') return new Response('x'.repeat(17130234), { headers: { 'content-type': 'application/json' } });
    if (url.searchParams.get('keyword') === 'oversized') {
      return new Response('x'.repeat(65 * 1024 * 1024), { headers: { 'content-type': 'application/json' } });
    }
    if (url.searchParams.get('keyword') === 'failure') return new Response('{"success":false}', { status: 429 });
    return Response.json({ url: request.url, forwarded: request.headers.get('x-forwarded-for'),
      cf: request.headers.get('cf-connecting-ip'), cookie: request.headers.get('cookie'), env,
      inherited: process.env.BILIBILI_COOKIE || null, proxy: process.env.HTTP_PROXY || null, startupNetworkBlocked, startupDnsBlocked, startupFetchBlocked, platform, clientIp });
}
