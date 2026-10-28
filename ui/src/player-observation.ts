import { emptyPlayer, type Player } from './model';

export interface PlayerObservation { player: Player; identityEpoch: number; bindingEpoch: number }
export const initialPlayback: PlayerObservation = { player: emptyPlayer, identityEpoch: 0, bindingEpoch: 0 };

// Advance at message receipt, not in an effect: a render may skip intermediate identities.
export function observePlayer(previous: PlayerObservation, player: Player): PlayerObservation {
  const titleChanged = player.title !== previous.player.title;
  const episodeChanged = Boolean(player.title) && player.episodeNumber !== previous.player.episodeNumber;
  const revisionChanged = player.mediaRevision !== previous.player.mediaRevision;
  // Only an adjacent same-work episode is safe to follow across a native revision change.
  const continuityLost = (episodeChanged || revisionChanged) && !(episodeChanged && player.mediaRevision === previous.player.mediaRevision + 1);
  return {
    player,
    identityEpoch: previous.identityEpoch + Number(titleChanged || episodeChanged || revisionChanged),
    bindingEpoch: previous.bindingEpoch + Number(titleChanged || continuityLost),
  };
}
