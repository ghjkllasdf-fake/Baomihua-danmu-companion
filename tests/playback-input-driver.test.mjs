import test from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, isAbsolute, join, relative, sep } from 'node:path';
import * as inputDriver from '../scripts/PlaybackInputDriver.mjs';
const { performPlaybackButton } = inputDriver;

const window = { id: 123, app: 'fixture-only', title: 'fixture player' };
async function run(t, letter = 'a') {
  const dir = await fs.mkdtemp(join(tmpdir(), 'playback-input-driver-'));
  t.after(async () => {
    const root = await fs.realpath(tmpdir()), target = await fs.realpath(dir), rel = relative(root, target);
    if (!rel || isAbsolute(rel) || rel.startsWith('..') || rel.split(sep).length !== 1
      || !basename(target).startsWith('playback-input-driver-')) throw new Error('Unsafe test cleanup path');
    await fs.rm(target, { recursive: true });
  });
  const metadata = { runDirectory: dir, nonce: letter.repeat(32) };
  await fs.writeFile(join(dir, 'run.json'), JSON.stringify(metadata), { flag: 'wx' });
  return metadata;
}
function client(failRefresh = false) {
  return {
    clicks: 0,
    async click(input) { assert.equal(input.window.id, window.id); assert.equal(input.element_index, 16); this.clicks++; },
    async get_window_state() { if (failRefresh) throw new Error('fixture refresh failure'); return { window, accessibility: {} }; },
  };
}
async function answer(run, id, expectedState, nonce = run.nonce) {
  const command = join(run.runDirectory, `command-${id}-0-arm.json`);
  for (let attempt = 0; attempt < 100; attempt++) {
    try { await fs.readFile(command); break; } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      if (attempt === 99) throw new Error('No arm command');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  }
  await fs.writeFile(join(run.runDirectory, `armed-${id}.json`), JSON.stringify({
    nonce, arm: { id, expectedState, nativeMs: 1000 },
    baseline: { hwnd: '123', windowTitle: window.title },
  }), { flag: 'wx' });
}
const args = (run, client, id = 'pause1', expectedState = 'paused') =>
  ({ run, client, window, elementIndex: 16, id, expectedState, timeoutMs: 1500 });

test('one driver operates on two explicitly supplied runs without retaining the earlier directory', async t => {
  const first = await run(t, 'a'), second = await run(t, 'b'), control = client();
  for (const metadata of [first, second]) {
    const reply = answer(metadata, 'pause1', 'paused');
    const result = await performPlaybackButton(args(metadata, control));
    await reply;
    assert.equal(result.driver.runDirectory, metadata.runDirectory);
    const input = JSON.parse(await fs.readFile(join(metadata.runDirectory, 'command-pause1-1-input.json'), 'utf8'));
    assert.equal(input.nonce, metadata.nonce);
    assert.equal(input.action, 'play-pause-button');
  }
  assert.equal(control.clicks, 2);
});
test('a completed old run refuses a new action before writing or clicking', async t => {
  const metadata = await run(t), control = client();
  await fs.writeFile(join(metadata.runDirectory, 'sources.json'), '{}');
  await assert.rejects(performPlaybackButton(args(metadata, control)), /closed/);
  assert.equal(control.clicks, 0);
});
test('an old armed response cannot be replayed and is never replaced', async t => {
  const metadata = await run(t), control = client(), path = join(metadata.runDirectory, 'armed-pause1.json');
  await fs.writeFile(path, 'old-response');
  await assert.rejects(performPlaybackButton(args(metadata, control)), /already exists/);
  assert.equal(await fs.readFile(path, 'utf8'), 'old-response');
  assert.equal(control.clicks, 0);
});
test('an existing immutable command is not overwritten', async t => {
  const metadata = await run(t), control = client(), path = join(metadata.runDirectory, 'command-pause1-0-arm.json');
  await fs.writeFile(path, 'old-command');
  await assert.rejects(performPlaybackButton(args(metadata, control)), /already exists/);
  assert.equal(await fs.readFile(path, 'utf8'), 'old-command');
  assert.equal(control.clicks, 0);
});
test('wrong run nonce in a newly published response refuses input', async t => {
  const metadata = await run(t), control = client(), reply = answer(metadata, 'pause1', 'paused', 'wrong');
  await assert.rejects(performPlaybackButton(args(metadata, control)), /identity/);
  await reply;
  assert.equal(control.clicks, 0);
});
test('invalid action paths are rejected before creating records or clicking', async t => {
  const metadata = await run(t), control = client();
  await assert.rejects(performPlaybackButton({ ...args(metadata, control), id: '../outside' }), /Invalid/);
  assert.equal(control.clicks, 0);
  assert.deepEqual(await fs.readdir(metadata.runDirectory), ['run.json']);
});
test('full accessibility refresh waits for the native delivery record', async t => {
  const metadata = await run(t), control = client(), journal = join(metadata.runDirectory, 'observations.jsonl');
  let delivery = Promise.resolve();
  control.click = async () => {
    control.clicks++;
    delivery = new Promise(resolve => setTimeout(resolve, 50)).then(() => fs.writeFile(journal, `${JSON.stringify({
      type: 'sample', nativeMs: 1250, state: 'paused', hwnd: '123', windowTitle: window.title,
    })}\n`));
  };
  control.get_window_state = async () => { await fs.access(journal); return { window, accessibility: {} }; };
  const reply = answer(metadata, 'pause1', 'paused');
  try {
    const result = await performPlaybackButton(args(metadata, control));
    assert.equal(result.driver.nativeDeliveryObservedBeforeRefresh, true);
  } finally { await reply; await delivery; }
});
test('failed refresh after input stays uncertain and cannot create input confirmation', async t => {
  const metadata = await run(t), control = client(true), reply = answer(metadata, 'pause1', 'paused');
  await assert.rejects(performPlaybackButton(args(metadata, control)), error => error.inputMayHaveOccurred === true);
  await reply;
  assert.equal(control.clicks, 1);
  await assert.rejects(fs.access(join(metadata.runDirectory, 'command-pause1-1-input.json')), { code: 'ENOENT' });
});
test('a screenshot-bound button point uses actual coordinate input without an accessibility index', async t => {
  const metadata = await run(t), control = client(), reply = answer(metadata, 'pause1', 'paused');
  const buttonPoint = { windowId: window.id, screenshotId: 'fresh-fixture-shot', x: 90, y: 80, width: 200, height: 100 };
  control.click = async input => {
    assert.deepEqual(input, { window, screenshotId: buttonPoint.screenshotId, x: 90, y: 80 });
    control.clicks++;
  };
  const result = await performPlaybackButton({ ...args(metadata, control), elementIndex: undefined, buttonPoint });
  await reply;
  assert.equal(control.clicks, 1);
  assert.equal(result.driver.inputKind, 'point');
});
test('ambiguous, foreign, missing-screenshot and out-of-bounds button points refuse input', async t => {
  const metadata = await run(t), control = client();
  const point = { windowId: window.id, screenshotId: 'fresh', x: 90, y: 80, width: 200, height: 100 };
  for (const change of [{ windowId: 456 }, { screenshotId: '' }, { x: -1 }, { y: 100 }, { x: NaN }, { width: 0 }]) {
    await assert.rejects(performPlaybackButton({ ...args(metadata, control), elementIndex: undefined, buttonPoint: { ...point, ...change } }), /Invalid/);
  }
  await assert.rejects(performPlaybackButton({ ...args(metadata, control), buttonPoint: point }), /Invalid/);
  assert.equal(control.clicks, 0);
  assert.deepEqual(await fs.readdir(metadata.runDirectory), ['run.json']);
});
test('owned close publishes one complete immutable command and refuses a completed or foreign run', async t => {
  const metadata = await run(t), closePath = join(metadata.runDirectory, 'command-close.json');
  await inputDriver.requestPlaybackClose(metadata);
  assert.deepEqual(JSON.parse(await fs.readFile(closePath, 'utf8')), { nonce: metadata.nonce, type: 'close' });
  await assert.rejects(inputDriver.requestPlaybackClose(metadata), /already exists/);
  assert.equal((await fs.readdir(metadata.runDirectory)).some(name => name.endsWith('.tmp')), false);
  const closed = await run(t, 'b');
  await fs.writeFile(join(closed.runDirectory, 'sources.json'), '{}');
  await assert.rejects(inputDriver.requestPlaybackClose(closed), /closed/);
  const foreign = await run(t, 'c');
  await assert.rejects(inputDriver.requestPlaybackClose({ ...foreign, nonce: 'd'.repeat(32) }), /identity/);
  await assert.rejects(fs.access(join(foreign.runDirectory, 'command-close.json')), { code: 'ENOENT' });
});
