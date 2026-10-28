import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Preview } from '../../src/components/Preview';
import { defaultSettings } from '../../src/model';
import '../../src/styles.css';

const root = createRoot(document.getElementById('preview-host')!);
const mount = document.getElementById('mount-preview') as HTMLButtonElement;
mount.addEventListener('click', () => root.render(<StrictMode><Preview settings={defaultSettings} /></StrictMode>));
document.getElementById('unmount-preview')!.addEventListener('click', () => root.render(null));
mount.disabled = false;
