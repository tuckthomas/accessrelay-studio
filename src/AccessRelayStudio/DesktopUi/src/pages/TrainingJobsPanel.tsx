import { useEffect, useState } from 'react';
import { Button, Card, Notice } from '../ui';

export function TrainingJobsPanel({ initial, command }: { initial: any; command: (action: string, args?: any) => Promise<any> }) {
  const [panel, setPanel] = useState(initial), [busy, setBusy] = useState(''), [error, setError] = useState('');
  useEffect(() => { if (initial) setPanel(initial); }, [initial]);
  useEffect(() => {
    let active = true;
    const refresh = () => { void command('assistantState').then(value => { if (active) setPanel(value); }).catch(() => {}); };
    refresh(); const timer = window.setInterval(refresh, 3000);
    return () => { active = false; window.clearInterval(timer); };
  }, [command]);
  const run = async (action: string, args: any) => {
    setBusy(action); setError('');
    try { setPanel(await command(action, args)); }
    catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); }
    finally { setBusy(''); }
  };
  return <div className="stack">
    {error && <Notice tone="danger">{error}</Notice>}
    <Card className="training-jobs" title="Offline training jobs"><div className="stack">
        <p>Choose reviewed train/validation sessions, a threshold file, a trusted Python executable with your offline dependencies, and a new-results workspace. Input registrations last until Studio closes. Clients receive opaque IDs rather than filesystem paths. Logs and results stay in your chosen workspace.</p>
        <div className="row">{[['dataset', 'Register dataset folder'], ['thresholds', 'Register thresholds'], ['python', 'Choose Python interpreter'], ['workspace', 'Choose output workspace']].map(([kind, label]) =>
          <Button key={kind} busy={busy === 'assistantRegister'} onClick={() => void run('assistantRegister', { kind })}>{label}</Button>)}</div>
        <p>Interpreter: {panel?.registration?.interpreterRegistered ? 'registered' : 'not chosen'} · Workspace: {panel?.registration?.workspaceRegistered ? 'registered' : 'not chosen'}</p>
        {panel?.registration?.workspacePath && <p className="subtle">Local output folder: <code>{panel.registration.workspacePath}</code></p>}
        {panel?.registration?.inputs?.map((item: any) => <div className="detail-line" key={item.id}><span>{item.label}{item.path ? ' · ' + item.path : ''}</span><code>{item.id}</code></div>)}
        {panel?.jobs?.map((job: any) => <div className="stack" key={job.id}><div className="detail-line"><span role="status">{job.kind} · {job.state}{job.errorCode ? ' · ' + (({ workerRejectedInputs: 'Check reviewed inputs and installed offline dependencies.', offlineWorkerFailed: 'Inspect the local job logs.', jobResourceLimit: 'The resource budget was reached.', cancelledOrExpired: 'Cancelled or access expired.', reportBounds: 'The bounded report could not be read.' } as Record<string, string>)[job.errorCode] || 'Inspect the local job logs.') : ''}</span>
          {['queued', 'running'].includes(job.state) && <Button onClick={() => void run('assistantCancelJob', { id: job.id })}>Cancel job</Button>}</div>
          {job.report && <pre className="subtle">{JSON.stringify(job.report, null, 2)}</pre>}</div>)}
        <Notice>Jobs use bounded CPU/time/storage budgets. Starting another foreground controller, capture or voice action cancels the offline job. Reviewed labels, held-out data and actual hardware evidence remain separate requirements.</Notice>
      </div></Card>
  </div>;
}
