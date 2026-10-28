import { describe, expect, it } from 'vitest';
import { emptyPlayer } from './model';
import { initialPlayback, observePlayer } from './player-observation';

describe('UI-local media identity observations', () => {
  const first = { ...emptyPlayer, title: 'work A', episodeNumber: 1 };
  it('records first recognition without mutating earlier observations', () => {
    const next = observePlayer(initialPlayback, first);
    expect(next).toEqual({ player: first, identityEpoch: 1, bindingEpoch: 1 });
    expect(initialPlayback).toEqual({ player: emptyPlayer, identityEpoch: 0, bindingEpoch: 0 });
  });
  it('does not invalidate a source for ordinary timing, pause or window updates', () => {
    const previous = observePlayer(initialPlayback, first);
    const next = observePlayer(previous, { ...first, positionMs: 20000, state: 'paused', sequence: 50, seekRevision: 9 });
    expect(next.identityEpoch).toBe(previous.identityEpoch);
    expect(next.bindingEpoch).toBe(previous.bindingEpoch);
    expect(next.player.positionMs).toBe(20000);
  });
  it('invalidates an in-flight request but preserves same-work follow eligibility on episode changes', () => {
    const previous = observePlayer(initialPlayback, first);
    const next = observePlayer(previous, { ...first, episodeNumber: 2, mediaRevision: 1 });
    expect(next.identityEpoch).toBe(previous.identityEpoch + 1);
    expect(next.bindingEpoch).toBe(previous.bindingEpoch);
  });
  it('remembers loss and return even when React only renders the final identical media', () => {
    const previous = observePlayer(initialPlayback, first);
    const lost = observePlayer(previous, emptyPlayer);
    const returned = observePlayer(lost, first);
    expect(returned.player).toEqual(previous.player);
    expect(returned.identityEpoch).toBe(previous.identityEpoch + 2);
    expect(returned.bindingEpoch).toBe(previous.bindingEpoch + 2);
  });
  it('remembers a different work between two observations of the same work', () => {
    const previous = observePlayer(initialPlayback, first);
    const other = observePlayer(previous, { ...first, title: 'work B' });
    const returned = observePlayer(other, first);
    expect(returned.identityEpoch).toBe(previous.identityEpoch + 2);
    expect(returned.bindingEpoch).toBe(previous.bindingEpoch + 2);
  });
  it('ignores episode metadata without a title', () => {
    const unknown = observePlayer(initialPlayback, { ...emptyPlayer, episodeNumber: 3 });
    expect(unknown.identityEpoch).toBe(0);
    expect(unknown.bindingEpoch).toBe(0);
  });
  it.each([' work A', 'work A ', 'WORK A'])('matches native raw-title identity for %j', title => {
    const known = observePlayer(initialPlayback, first);
    const next = observePlayer(known, { ...first, title });
    expect(next.identityEpoch).toBe(known.identityEpoch + 1);
    expect(next.bindingEpoch).toBe(known.bindingEpoch + 1);
  });
  it('does not silently normalize Unicode titles that native treats as different', () => {
    const known = observePlayer(initialPlayback, { ...first, title: 'caf\u00e9' });
    const next = observePlayer(known, { ...first, title: 'cafe\u0301' });
    expect(next.identityEpoch).toBe(known.identityEpoch + 1);
    expect(next.bindingEpoch).toBe(known.bindingEpoch + 1);
  });
  it.each([
    { name: 'same final media', revision: 5, episode: 1 },
    { name: 'episode change without a native revision', revision: 4, episode: 2 },
    { name: 'skipped identities before an episode change', revision: 6, episode: 2 },
    { name: 'host revision reset', revision: 0, episode: 2 },
  ])('invalidates source continuity for $name', ({ revision, episode }) => {
    const known = observePlayer(initialPlayback, { ...first, mediaRevision: 4 });
    const next = observePlayer(known, { ...first, mediaRevision: revision, episodeNumber: episode });
    expect(next.identityEpoch).toBe(known.identityEpoch + 1);
    expect(next.bindingEpoch).toBe(known.bindingEpoch + 1);
  });
  it('preserves a bound source for one native revision and the next episode of the same work', () => {
    const known = observePlayer(initialPlayback, { ...first, mediaRevision: 4 });
    const next = observePlayer(known, { ...first, mediaRevision: 5, episodeNumber: 2 });
    expect(next.identityEpoch).toBe(known.identityEpoch + 1);
    expect(next.bindingEpoch).toBe(known.bindingEpoch);
  });
});
