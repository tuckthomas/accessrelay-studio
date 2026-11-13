import { useEffect, useState } from 'react';
import { desktop } from './api';
import { GameplayPreview } from './components/GameplayPreview';
import { Button, Icon, Notice } from './ui';
import { applyTheme } from './theme';

export function DetachedGameplayView() {
  const [status, setStatus] = useState<any>({}), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  useEffect(() => {
    let active = true;
    const update = (value: any) => {
      if (!active) return;
      setStatus(value.vision?.status ?? value.vision ?? {});
      if (value.theme === 'dark' || value.theme === 'light') applyTheme(value.theme);
    };
    const unsubscribe = desktop.subscribe(message => {
      if (message.type === 'status') update(message.status);
    });
    void desktop.command('app.bootstrap').then(update).catch(e => { if (active) setError(String(e.message)); });
    return () => { active = false; unsubscribe(); };
  }, []);
  return <main className="detached-gameplay">
    {error && <Notice tone="danger">{error}</Notice>}
    <GameplayPreview status={status} onError={setError} window={{ attach: () => desktop.command('app.gameplayAttach') }}
      actions={<Button disabled={!status.session?.running || !!status.stopping || busy} onClick={async () => {
        setBusy(true); setError('');
        try { await desktop.command('vision.stop'); } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
        finally { setBusy(false); }
      }}><Icon name="stop" size={17} />Stop capture</Button>} />
  </main>;
}
