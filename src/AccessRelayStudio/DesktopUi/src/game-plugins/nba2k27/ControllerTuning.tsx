import { useState } from 'react';
import { Select, SelectOption, Button, Card, Field, Icon, Notice, humanize, type PageProps } from '../../ui';

type Props = { command: PageProps['command']; variant?: string; id?: string; disabled?: boolean; onApplied?: () => Promise<void> };
export function Nba2K27SequenceTuning(props: Props) {
  return props.id ? <details><summary>NBA 2K27 feedback tuning</summary><FeedbackTuning {...props} /></details>
    : <Card title="Fine-tune from shot feedback" description="Optional manual tuning for NBA 2K27."><FeedbackTuning {...props} /></Card>;
}

function FeedbackTuning({command,variant,id,disabled,onApplied}:{command:PageProps['command'];variant?:string;id?:string;disabled?:boolean;onApplied?:()=>Promise<void>}){
  const [timing,setTiming]=useState('none'),[tempo,setTempo]=useState('none'),[busy,setBusy]=useState(false),[message,setMessage]=useState('');
  return <div className="stack"><div className="form-grid"><Field label="Timing feedback"><Select value={timing} onValueChange={selectionValue=>setTiming(selectionValue)}>
    {['none','early','slightlyEarly','excellent','slightlyLate','late'].map(value=><SelectOption value={value} key={value}>{value==='none'?'No adjustment':humanize(value)}</SelectOption>)}</Select></Field>
    <Field label="Tempo feedback"><Select value={tempo} onValueChange={selectionValue=>setTempo(selectionValue)}>{['none','fast','slightlyFast','excellent','slightlySlow','slow'].map(value=><SelectOption value={value} key={value}>{value==='none'?'No adjustment':humanize(value)}</SelectOption>)}</Select></Field></div>
    <p className="subtle">Applies a small timing adjustment to this saved sequence. Feedback is a tuning hint, not a measured latency correction.</p>
    <Button busy={busy} disabled={disabled||(timing==='none'&&tempo==='none')} onClick={async()=>{setBusy(true);setMessage('');try{await command('packAction',{action:'feedback',id,variant,timingFeedback:timing,tempoFeedback:tempo});await onApplied?.();setMessage('Feedback adjustment applied to the saved sequence.');}catch(e){setMessage(e instanceof Error?e.message:String(e));}finally{setBusy(false);}}}><Icon name="sliders" size={16}/>Apply feedback adjustment</Button>
    {message&&<Notice>{message}</Notice>}</div>;
}

