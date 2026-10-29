import { record } from './protocol';

export interface AutomaticMatchStatus {
  state: 'waiting' | 'matching' | 'loading' | 'loaded' | 'failed' | 'manual';
  message: string;
  sourceTitle: string;
}
export function validAutomaticMatch(value: unknown): value is AutomaticMatchStatus {
  return record(value) && typeof value.state === 'string' &&
    ['waiting', 'matching', 'loading', 'loaded', 'failed', 'manual'].includes(value.state) &&
    typeof value.message === 'string' && value.message.length <= 1000 &&
    typeof value.sourceTitle === 'string' && value.sourceTitle.length <= 500;
}
