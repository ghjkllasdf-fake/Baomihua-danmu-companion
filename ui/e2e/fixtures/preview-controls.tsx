import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Preview } from '../../src/components/Preview';
import { DisplaySettings } from '../../src/components/DisplaySettings';
import { defaultSettings, type Settings } from '../../src/model';
import '../../src/styles.css';

function PreviewFixture() {
  const [settings, setSettings] = useState(defaultSettings);
  function update(patch: Partial<Settings>) { setSettings(current => ({ ...current, ...patch })); }
  return <div className="app-shell"><main className="workspace"><div className="main-controls">
    <label>显示弹幕<input aria-label="显示弹幕" type="checkbox" role="switch" checked={settings.enabled} onChange={event => update({ enabled: event.target.checked })} /></label>
    <Preview settings={settings} /><DisplaySettings settings={settings} update={update} />
  </div></main></div>;
}

createRoot(document.getElementById('root')!).render(<PreviewFixture />);
