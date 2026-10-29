import { HostCommandError, HostTimeoutError } from './bridge-errors';
import { validHello, type Hello } from './model';
import { record, revision, validPayload, validResult, type CommandType, type HostRequest, type Result } from './protocol';

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: string, callback: (event: MessageEvent) => void): void;
}
declare global { interface Window { chrome?: { webview?: WebViewBridge } } }
const pending = new Map<string, { type: CommandType; resolve: (value: unknown) => void; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>();
const listeners = new Set<(data: unknown) => void>();
export type Connection = { status: 'checking' } | { status: 'failed'; message: string } | { status: 'ready'; hello: Hello };
const connectionListeners = new Set<(connection: Connection) => void>();
let connection: Connection = { status: 'checking' }, handshakeEpoch = 0;
function connectionChanged(next: Connection) {
  connection = next;
  for (const listener of connectionListeners) {
    if (connection !== next) return;
    listener(next);
  }
}
function rejectPending(message: string) {
  for (const [id, entry] of pending) {
    clearTimeout(entry.timer); pending.delete(id); entry.reject(new HostCommandError(message, 'host-not-ready'));
  }
}
export function revokeConnection(message: string) {
  stateReady = false; handshakeEpoch++;
  rejectPending('连接校验未通过，先前请求结果尚未确认。');
  connectionChanged({ status: 'failed', message });
}
let sequence = 0;
let stateReady = false, lastStateSequence = 0;
export const inDesktop = Boolean(window.chrome?.webview);
const documentId = inDesktop ? crypto.randomUUID() : 'preview';
window.chrome?.webview?.addEventListener('message', event => {
  const msg: unknown = event.data;
  if (!record(msg)) return;
  const value = msg;
  if (value.type === 'state') {
    if (!stateReady || typeof value.sequence !== 'number' || !Number.isSafeInteger(value.sequence) || value.sequence <= lastStateSequence) return;
    lastStateSequence = value.sequence; listeners.forEach(listener => listener(value.data)); return;
  }
  if (value.type !== 'response' || typeof value.id !== 'string') return;
  const request = pending.get(value.id);
  if (!request) return;
  clearTimeout(request.timer); pending.delete(value.id);
  if (request.type === 'hello' && value.ok === true) {
    const data = value.data;
    const watermark = data && typeof data === 'object' ? (data as Record<string, unknown>).stateSequence : undefined;
    if (!revision(watermark)) { request.reject(new HostCommandError('宿主握手缺少有效的状态版本，请重新打开新版。', 'invalid-response')); return; }
    if (!validHello(data)) { request.reject(new HostCommandError('宿主版本或握手数据不兼容，请关闭后重新打开新版工作台。', 'host-incompatible')); return; }
    stateReady = true; lastStateSequence = Math.max(lastStateSequence, watermark);
    connectionChanged({ status: 'ready', hello: data });
    if (!stateReady) { request.reject(new HostCommandError('宿主连接校验未通过，请重新打开新版工作台。', 'host-incompatible')); return; }
  }
  if (value.ok === true) request.resolve(request.type === 'update-settings'
    ? { settings: value.data, settingsRevision: value.settingsRevision } : value.data);
  else if (value.ok === false && typeof value.error === 'string' && value.error.length <= 1000 && (value.code === undefined || typeof value.code === 'string' && value.code.length <= 64))
    request.reject(new HostCommandError(value.error, typeof value.code === 'string' ? value.code : 'command-failed'));
  else request.reject(new HostCommandError('宿主响应格式无效，请重试。', 'invalid-response'));
});
export function request<C extends HostRequest>(...command: C): Promise<Result<C[0]>> {
  const type: C[0] = command[0];
  if (!window.chrome?.webview) return Promise.reject(new Error('当前为浏览器预览，请从新版桌面入口连接服务。'));
  const payload: unknown = command[1] ?? {};
  if (!validPayload(type, payload)) return Promise.reject(new HostCommandError('请求参数无效，未发送给宿主。', 'invalid-payload'));
  if (type !== 'hello' && !stateReady) return Promise.reject(new HostCommandError('尚未完成安全握手，请重新打开新版工作台。', 'host-not-ready'));
  if (type === 'hide' && (connection.status !== 'ready' || !connection.hello.capabilities.includes('tray-flyout-v1')))
    return Promise.reject(new HostCommandError('宿主不支持托盘面板。', 'unsupported-capability'));
  if ((type === 'get-local-api' || type === 'set-local-sources') &&
    (connection.status !== 'ready' || !connection.hello.capabilities.includes('embedded-api-v1')))
    return Promise.reject(new HostCommandError('宿主不支持本地弹幕服务设置。', 'unsupported-capability'));
  if (type === 'hello') {
    stateReady = false; handshakeEpoch++;
    rejectPending('连接正在重新确认，先前请求结果尚未确认。');
    connectionChanged({ status: 'checking' });
  }
  const epoch = handshakeEpoch;
  const id = `${documentId}:r${++sequence}`;
  return new Promise<Result<C[0]>>((resolve, reject) => {
    const fail = (error: Error) => {
      if (type === 'hello' && epoch === handshakeEpoch) connectionChanged({ status: 'failed', message: error.message });
      reject(error);
    };
    const timeoutMs = ['update-settings', 'get-settings', 'get-startup', 'set-startup', 'save-api'].includes(type) ? 10000 : 190000;
    const timer = setTimeout(() => { pending.delete(id); fail(new HostTimeoutError()); }, timeoutMs);
    pending.set(id, { type, resolve: value => {
      if (validResult(type, value)) resolve(value);
      else fail(new HostCommandError('宿主响应数据无效，结果尚未确认。', 'invalid-response'));
    }, reject: fail, timer });
    try { window.chrome!.webview!.postMessage({ v: 1, id, type, payload }); }
    catch { clearTimeout(timer); pending.delete(id); fail(new HostCommandError('宿主消息发送失败，结果尚未确认。', 'transport-failed')); }
  });
}
export function subscribeConnection(listener: (connection: Connection) => void): () => void {
  connectionListeners.add(listener); listener(connection);
  return () => { connectionListeners.delete(listener); };
}
export function subscribe(listener: (data: unknown) => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}
