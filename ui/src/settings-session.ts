import { HostCommandError } from './bridge-errors';
import { defaultSettings, validSettings, validSettingsSnapshot, type Settings } from './model';

export type SettingsStatus = 'preview' | 'saving' | 'saved' | 'recovering' | 'unknown' | 'error';
export interface SettingsView { settings: Settings; status: SettingsStatus; message: string }
type Transport = (type: 'update-settings' | 'get-settings', payload?: { settings: Settings }) => Promise<unknown>;
const keys = Object.keys(defaultSettings) as (keyof Settings)[];
function copy(s: Settings): Settings {
  return { areaFraction: s.areaFraction, fontSize: s.fontSize, scaleWithWindow: s.scaleWithWindow, speedMultiplier: s.speedMultiplier,
    opacity: s.opacity, outlineWidth: s.outlineWidth, density: s.density, rolling: s.rolling, top: s.top, bottom: s.bottom,
    dedupe: s.dedupe, keywords: s.keywords, offsetMs: s.offsetMs, enabled: s.enabled };
}

export class SettingsSession {
  private current: Settings;
  private confirmed: Settings;
  private nativeRevision = -1;
  private version = 0;
  private sentThrough = 0;
  private unknownThrough = 0;
  private knownThrough = 0;
  private pending = false;
  private reading = false;
  private connected = false;
  private active = true;
  private errorMessage = '';
  private recoveryMessage = '';
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly transport: Transport;
  private readonly changed: (view: SettingsView) => void;
  constructor(initial: Settings, transport: Transport, changed: (view: SettingsView) => void) {
    this.current = copy(initial); this.confirmed = copy(initial); this.transport = transport; this.changed = changed;
  }
  view(): SettingsView {
    const status = !this.connected ? 'preview' : this.reading && this.unknownThrough > this.knownThrough ? 'recovering' : this.pending ? 'saving' :
      this.unknownThrough > this.knownThrough ? 'unknown' : this.errorMessage ? 'error' : 'saved';
    return { settings: copy(this.current), status, message: this.recoveryMessage || this.errorMessage };
  }
  private publish() { if (this.active) this.changed(this.view()); }
  private accept(value: unknown) {
    if (!validSettingsSnapshot(value)) throw new Error('宿主返回了无效的显示设置快照。');
    if (value.settingsRevision < this.nativeRevision) return;
    if (value.settingsRevision === this.nativeRevision && !keys.every(key => value.settings[key] === this.confirmed[key]))
      throw new Error('宿主显示设置版本冲突，结果尚未确认。');
    this.nativeRevision = value.settingsRevision; this.confirmed = copy(value.settings);
  }
  private showConfirmed() {
    if (!this.pending && this.unknownThrough <= this.knownThrough) this.current = copy(this.confirmed);
  }
  connect(value: unknown) {
    if (!this.active) return;
    this.accept(value); this.connected = true; this.showConfirmed(); this.publish();
  }
  observe(value: unknown) {
    if (!this.active) return;
    try { this.accept(value); this.showConfirmed(); }
    catch { this.errorMessage = '宿主显示设置状态无效，已保留上次确认值。'; }
    this.publish();
  }
  edit(patch: Partial<Settings>) {
    if (!this.active) return;
    const next = copy({ ...this.current, ...patch });
    if (!validSettings(next)) { this.errorMessage = '设置值超出有效范围。'; this.publish(); return; }
    this.current = next; this.errorMessage = ''; this.recoveryMessage = '';
    if (!this.connected) { this.publish(); return; }
    ++this.version; this.pending = true;
    // Keep the first deadline so sustained slider input also reaches the host.
    if (this.timer === undefined) this.timer = setTimeout(() => {
      this.timer = undefined; void this.save(this.version, copy(this.current));
    }, 16);
    this.publish();
  }
  private async save(version: number, settings: Settings) {
    this.sentThrough = Math.max(this.sentThrough, version);
    try {
      const saved = await this.transport('update-settings', { settings: copy(settings) });
      if (!this.active) return;
      this.accept(saved); this.knownThrough = Math.max(this.knownThrough, version);
      if (version === this.version) { this.pending = false; this.errorMessage = ''; }
      if (this.unknownThrough <= this.knownThrough) this.recoveryMessage = '';
      this.showConfirmed(); this.publish();
    } catch (error) {
      if (!this.active) return;
      if (version === this.version) this.pending = false;
      if (error instanceof HostCommandError && error.code === 'settings-not-applied') {
        if (version === this.version) this.errorMessage = error.message;
        this.showConfirmed(); this.publish(); return;
      }
      if (version <= this.knownThrough) { this.showConfirmed(); this.publish(); return; }
      this.unknownThrough = Math.max(this.unknownThrough, version);
      this.recoveryMessage = '保存结果尚未确认，正在读取宿主保存值。';
      void this.recover();
    }
  }
  private async recover() {
    if (!this.active || !this.connected || this.reading || this.unknownThrough <= this.knownThrough) return;
    this.reading = true; this.publish();
    try {
      while (this.active && this.unknownThrough > this.knownThrough) {
        // Same-document messages are ordered; this synchronous host read follows writes already dispatched.
        // https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.webmessagereceived
        const barrier = this.sentThrough;
        const saved = await this.transport('get-settings');
        if (!this.active) return;
        this.accept(saved); this.knownThrough = Math.max(this.knownThrough, barrier);
        this.showConfirmed();
      }
      this.recoveryMessage = '';
    } catch {
      if (this.active && this.unknownThrough > this.knownThrough) this.recoveryMessage = '无法确认显示设置是否已保存，请重新同步设置。';
    } finally { this.reading = false; this.publish(); }
  }
  retryRecovery() { void this.recover(); }
  dismissMessage() { this.errorMessage = ''; this.recoveryMessage = ''; this.publish(); }
  dispose() { this.active = false; clearTimeout(this.timer); }
}

export const settingsStatusText: Record<SettingsStatus, string> = {
  preview: '仅预览，未保存', saving: '正在保存显示偏好…', saved: '显示偏好已保存', recovering: '正在确认保存结果…',
  unknown: '显示设置待确认', error: '显示设置需要检查',
};
