import { useEffect, useState } from 'react';
import { Select, SelectOption, Textarea, Badge, Button, Card, Field, Modal, Notice, Toggle } from '../ui';
import { isDesignPreview } from '../api';

export function AssistantPanel({ initial, command }: { initial: any; command: (action: string, args?: any) => Promise<any> }) {
  const [panel, setPanel] = useState(initial);
  const [drafts, setDrafts] = useState(false), [offline, setOffline] = useState(false), [minutes, setMinutes] = useState(15);
  const [review, setReview] = useState<any>(null), [busy, setBusy] = useState(''), [error, setError] = useState(''), [message, setMessage] = useState('');
  useEffect(() => { if (initial) setPanel(initial); }, [initial]);
  useEffect(() => {
    let active = true;
    const timer = window.setInterval(() => { void command('assistantState').then(value => { if (active) setPanel(value); }).catch(() => {}); }, 3000);
    return () => { active = false; window.clearInterval(timer); };
  }, [command]);
  const run = async (action: string, args: any = {}) => {
    setBusy(action); setError(''); setMessage('');
    try {
      const value = await command(action, args);
      if (action === 'assistantReview') setReview(value);
      else { if (value?.access) setPanel(value); if (action === 'assistantApply') { setReview(null); setMessage('Draft applied. Output and sequences remain off. Undo is available until this setup changes.'); }
        else if (action === 'assistantUndo') setMessage('The previous setup was restored with processing off.'); }
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'The assistant action could not finish.'); }
    finally { setBusy(''); }
  };
  const access = panel?.access;
  return <Card className="assistant-panel" title="Assistant access" description="Let an MCP assistant inspect Studio, suggest reversible changes and help review local training work."
    actions={<Badge tone={access?.enabled ? 'success' : 'neutral'}>{access?.enabled ? 'Access enabled' : 'Access off'}</Badge>}>
    <div className="stack">
      {isDesignPreview && <Notice>Design preview: assistant controls and drafts are sample interface data. No MCP bridge is running.</Notice>}
      <p className="subtle">Access starts off each time Studio opens. Your assistant client can share returned information with its own AI provider. This connection exposes no raw audio or screenshots and cannot activate controllers, capture, speech or models.</p>
      {!panel?.bridgeAvailable && <Notice tone="warning">Open the complete published app to include the assistant bridge and rehearsal worker.</Notice>}
      <Toggle label="Allow configuration drafts" description="Drafts can be rehearsed and proposed. You review and apply them here; the assistant cannot apply them." checked={drafts} onChange={setDrafts} />
      <Toggle label="Allow offline training workflows" description="Use only datasets and tools you register in Model Training. Jobs use CPU and keep new models observation-only." checked={offline} onChange={setOffline} />
      <Field label="Access duration"><Select value={minutes} onValueChange={selectionValue => setMinutes(Number(selectionValue))}>
        {[15, 30, 60].map(value => <SelectOption key={value} value={value}>{value} minutes</SelectOption>)}</Select></Field>
      <div className="row"><Button variant="primary" disabled={!panel?.bridgeAvailable} busy={busy === 'assistantConfigure'}
        onClick={() => void run('assistantConfigure', { enabled: true, drafts, offline, minutes })}>{access?.enabled ? 'Renew assistant access' : 'Enable assistant access'}</Button>
        <Button variant="danger" disabled={!access?.enabled} onClick={() => void run('assistantConfigure', { enabled: false, minutes: 15 })}>Revoke assistant access</Button></div>
      {access?.enabled && <p role="status">Expires {access.expiresAtUtc ? new Date(access.expiresAtUtc).toLocaleTimeString() : 'soon'}. Draft permission: {access.drafts ? 'on' : 'off'}. Offline permission: {access.offline ? 'on' : 'off'}.</p>}
      <p className="subtle">Renewing access or changing permissions cancels pending drafts and jobs. Stop everything also revokes access. All local clients running as your Windows user share this access window.</p>
      <details><summary>Connect your assistant client</summary><div className="stack"><p>Open Studio, enable access, then add this stdio server configuration to your MCP-compatible client. No web server or API key is supplied by Studio.</p>
        <Textarea aria-label="MCP client configuration" readOnly rows={8} value={panel?.clientConfiguration || 'Available in the installed app.'} spellCheck={false} /></div></details>
      <div className="stack"><h3>Proposed changes</h3>
        {!panel?.drafts?.length && <p className="subtle">No pending drafts.</p>}
        {panel?.drafts?.map((draft: any) => <div className="detail-line" key={draft.id}><span>{draft.profileName} · {draft.changes.join(' ')}</span><div className="row">
          <Button onClick={() => void run('assistantReview', { id: draft.id })}>Review draft</Button>
          <Button onClick={() => void run('assistantDiscard', { id: draft.id })}>Discard draft</Button></div></div>)}
        <Button disabled={!panel?.canUndo} busy={busy === 'assistantUndo'} onClick={() => void run('assistantUndo')}>Undo last assistant change</Button>
      </div>
      {message && <Notice>{message}</Notice>}
      {error && <Notice tone="danger">{error}</Notice>}
    </div>
    <Modal open={!!review} onClose={() => setReview(null)} title="Review assistant draft" description="Review the exact change. Stop processing and save or discard other edits before applying."
      footer={<><Button onClick={() => setReview(null)}>Keep reviewing later</Button><Button variant="primary" busy={busy === 'assistantApply'} onClick={() => void run('assistantApply', { id: review.view.id })}>Apply with processing off</Button></>} wide>
      {review && <div className="stack"><Notice>{review.warning} Controller navigation can stay on; release its switch after applying.</Notice><ul>{review.view.changes.map((change: string) => <li key={change}>{change}</li>)}</ul>
        {review.view.simulation && <p>Production-engine rehearsal: {review.view.simulation.valid ? 'passed specified synthetic events' : 'failed'}. Hardware and general safety certification: unavailable.</p>}
        <details><summary>Exact original and proposed setup</summary><h3>Original</h3><pre>{JSON.stringify(review.original, null, 2)}</pre><h3>Proposed</h3><pre>{JSON.stringify(review.proposed, null, 2)}</pre></details></div>}
    </Modal>
  </Card>;
}
