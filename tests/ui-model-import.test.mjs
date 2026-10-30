import test from 'node:test';
import assert from 'node:assert/strict';

test('capture tools can still import the typed UI model directly under Node', async () => {
  const { defaultSettings, validHello } = await import('../ui/src/model.ts');
  assert.equal(defaultSettings.fontSize, 22);
  assert.equal(validHello({ settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1,
    apiBase: '', capabilities: ['media-context-v1', 'appearance-v1'],
    appearance: { skin: 'fluent', reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' } }), true);
});
