import * as fs from 'node:fs/promises';
import { isAbsolute, join, resolve } from 'node:path';

async function absent(path) {
  try { await fs.lstat(path); } catch (error) { if (error.code === 'ENOENT') return; throw error; }
  throw new Error(`Measurement record already exists: ${path}`);
}
async function publish(path, value) {
  const temporary = `${path}.tmp`;
  await fs.writeFile(temporary, JSON.stringify(value), { flag: 'wx' });
  // link() publishes the completed file atomically and refuses an existing name.
  // rename() would replace an earlier action/confirmation on a reused run.
  await fs.link(temporary, path);
  await fs.unlink(temporary);
}
async function assertOpenRun(run) {
  const { runDirectory: directory, nonce } = run;
  if (!isAbsolute(directory) || !/^[a-f0-9]{32}$/.test(nonce)) throw new Error('Invalid measurement run');
  const metadata = JSON.parse(await fs.readFile(join(directory, 'run.json'), 'utf8'));
  if (metadata.nonce !== nonce || resolve(metadata.runDirectory) !== resolve(directory)) throw new Error('Run identity mismatch');
  try { await fs.access(join(directory, 'sources.json')); throw new Error('Measurement run is already closed'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}
export async function requestPlaybackClose(run) {
  await assertOpenRun(run);
  const path = join(run.runDirectory, 'command-close.json');
  await absent(path);
  await publish(path, { nonce: run.nonce, type: 'close' });
}
async function waitForNativeDelivery(directory, arm, expectedState, hwnd, title) {
  const deadline = performance.now() + 500;
  while (performance.now() < deadline) {
    let handle;
    try {
      handle = await fs.open(join(directory, 'observations.jsonl'), 'r');
      const { size } = await handle.stat(), start = Math.max(0, size - 256 * 1024);
      const buffer = Buffer.alloc(size - start);
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, start);
      const lines = buffer.subarray(0, bytesRead).toString('utf8').split(/\r?\n/);
      if (start > 0) lines.shift();
      lines.pop(); // A concurrently written final line may be incomplete.
      const samples = lines.filter(Boolean).map(line => JSON.parse(line)).filter(row => row.type === 'sample');
      if (samples.some(row => row.nativeMs > arm.nativeMs && row.state === expectedState
        && row.hwnd === hwnd && row.windowTitle === title)) return true;
      if (samples.at(-1)?.nativeMs >= arm.nativeMs + 500) return false;
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    finally { await handle?.close(); }
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  return false;
}
export async function performPlaybackButton({ run, client, window, elementIndex, buttonPoint, id, expectedState, timeoutMs = 20000 }) {
  const directory = run.runDirectory, nonce = run.nonce, title = window.title, hwnd = String(window.id);
  const element = buttonPoint === undefined && Number.isSafeInteger(elementIndex) && elementIndex >= 0;
  const point = elementIndex === undefined && buttonPoint !== null && typeof buttonPoint === 'object'
    && buttonPoint.windowId === window.id && typeof buttonPoint.screenshotId === 'string' && buttonPoint.screenshotId.trim().length > 0
    && Number.isSafeInteger(buttonPoint.width) && buttonPoint.width > 0 && Number.isSafeInteger(buttonPoint.height) && buttonPoint.height > 0
    && Number.isFinite(buttonPoint.x) && buttonPoint.x >= 0 && buttonPoint.x < buttonPoint.width
    && Number.isFinite(buttonPoint.y) && buttonPoint.y >= 0 && buttonPoint.y < buttonPoint.height;
  if (!isAbsolute(directory) || !/^[a-f0-9]{32}$/.test(nonce) || !/^[a-z0-9]{1,24}$/.test(id)
    || !['paused', 'playing'].includes(expectedState) || !(element || point)
    || !title || !/^[1-9]\d*$/.test(hwnd) || !Number.isFinite(timeoutMs) || timeoutMs < 50 || timeoutMs > 20000)
    throw new Error('Invalid measurement input');
  await assertOpenRun(run);
  const armedPath = join(directory, `armed-${id}.json`);
  const armPath = join(directory, `command-${id}-0-arm.json`);
  const inputPath = join(directory, `command-${id}-1-input.json`);
  const driverPath = join(directory, `driver-${id}.json`);
  for (const path of [armedPath, armPath, inputPath, driverPath]) await absent(path);
  await publish(armPath, { nonce, type: 'arm', id, expectedState });
  const deadline = performance.now() + timeoutMs;
  let armed;
  while (performance.now() < deadline) {
    try { armed = JSON.parse(await fs.readFile(armedPath, 'utf8')); break; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  if (!armed) throw new Error('No eligible fresh automatic opposite baseline; no input sent');
  if (armed.nonce !== nonce || armed.arm?.id !== id || armed.arm?.expectedState !== expectedState
    || !Number.isFinite(armed.arm?.nativeMs) || armed.arm.nativeMs < 0
    || armed.baseline?.windowTitle !== title || armed.baseline?.hwnd !== hwnd) throw new Error('Arm identity mismatch; no input sent');
  const started = performance.now();
  let state, returned, nativeDeliveryObservedBeforeRefresh;
  try {
    await client.click(point
      ? { window, screenshotId: buttonPoint.screenshotId, x: buttonPoint.x, y: buttonPoint.y }
      : { window, element_index: elementIndex });
    returned = performance.now();
    // Read our own journal before requesting a full player AX tree. The latter
    // can contend with the production probe's UIA calls during the response.
    nativeDeliveryObservedBeforeRefresh = await waitForNativeDelivery(directory, armed.arm, expectedState, hwnd, title);
    state = await client.get_window_state({ window, include_screenshot: false, include_text: true });
    if (String(state.window.id) !== hwnd || state.window.title !== title) throw new Error('Input window changed');
    await publish(inputPath, { nonce, type: 'input-completed', id, action: 'play-pause-button', windowTitle: title });
  } catch (cause) {
    const error = new Error('Input or refresh outcome is uncertain; reobserve before retrying', { cause });
    error.inputMayHaveOccurred = true;
    await fs.writeFile(join(directory, `driver-input-uncertain-${id}.json`), JSON.stringify({ id, inputMayHaveOccurred: true }), { flag: 'wx' }).catch(() => {});
    throw error;
  }
  const driver = { id, expectedState, runDirectory: directory, inputKind: point ? 'point' : 'element', inputApiRoundTripMs: returned - started, nativeDeliveryObservedBeforeRefresh,
    scope: 'Driver duration only; no cross-clock latency inference' };
  await fs.writeFile(driverPath, JSON.stringify(driver), { flag: 'wx' });
  return { state, driver };
}
