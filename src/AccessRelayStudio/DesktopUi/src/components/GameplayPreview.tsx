import type { ReactNode } from 'react';
import { Button, Card, Icon, Notice } from '../ui';

export interface GameplayWindowControls {
  detached?: boolean;
  detach?: () => Promise<any>;
  attach?: () => Promise<any>;
}

/** A presentation of the shared native capture session; this component never starts a worker. */
export function GameplayPreview({ status = {}, actions, window: captureWindow = {}, onError }: {
  status?: any; actions?: ReactNode; window?: GameplayWindowControls; onError?: (message: string) => void;
}) {
  const session = status.session ?? {}, bringBack = captureWindow.detached || !captureWindow.detach;
  const signal = status.preview?.signal, region = signal?.acquired ? signal.region : null;
  const move = async () => {
    try { await (bringBack ? captureWindow.attach?.() : captureWindow.detach?.()); }
    catch (error) { onError?.(error instanceof Error ? error.message : String(error)); }
  };
  return <Card className="remote-play-preview" title="Gameplay capture" actions={<div className="button-group">
    {actions}
    {(captureWindow.detach || captureWindow.attach) && <Button onClick={() => void move()}>
      <Icon name={bringBack ? 'arrowLeft' : 'arrowUpRight'} size={17} />{bringBack ? 'Bring back' : 'Detach'}
    </Button>}
  </div>}>
    <div className="remote-play-stage">{captureWindow.detached
      ? <p className="subtle">Capture is open in a separate window.</p>
      : status.preview?.url ? <div className="remote-play-picture" style={{ aspectRatio: `${status.preview.sourceWidth} / ${status.preview.sourceHeight}` }}>
        <img src={status.preview.url} alt={session.running ? 'Live gameplay capture' : 'Last captured gameplay preview'} />
        {region && <svg viewBox="0 0 1000 1000" preserveAspectRatio="none" className="remote-play-signal" aria-label="Detected signal region">
          <rect x={region.left * 1000} y={region.top * 1000} width={region.width * 1000} height={region.height * 1000}
            fill="none" stroke="var(--accent)" strokeWidth={2} vectorEffect="non-scaling-stroke" /></svg>}
      </div>
        : <p className="subtle">{status.xbox?.direct ? 'Connect to your Xbox and start capture.' : 'Select a window and start capture.'}</p>}</div>
    <div className="remote-play-preview-details">{status.preview?.sourceWidth ? `${status.preview.sourceWidth} × ${status.preview.sourceHeight}` : ''}</div>
    {session.captureFailed && <Notice tone="danger">{session.message || 'The selected window could not be captured.'}</Notice>}
  </Card>;
}
