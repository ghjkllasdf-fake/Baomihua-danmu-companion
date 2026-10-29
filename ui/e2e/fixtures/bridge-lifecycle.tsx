import { createRoot } from 'react-dom/client';
import { App } from '../../src/App';
import { request } from '../../src/bridge';
import { HostCommandError } from '../../src/bridge-errors';
import '../../src/styles.css';
import '../../src/compact.css';

createRoot(document.getElementById('root')!).render(<App />);
document.getElementById('rehello')!.addEventListener('click', () => {
  void request('hello').then(() => { document.getElementById('rehello-result')!.textContent = 'ready'; }, error => {
    document.getElementById('rehello-result')!.textContent = error instanceof HostCommandError ? error.code : 'failed';
  });
});
