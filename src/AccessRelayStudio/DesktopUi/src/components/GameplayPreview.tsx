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
      : status.preview?.url ? <img src={status.preview.url} alt={session.running ? 'Live gameplay capture' : 'Last captured gameplay preview'} />
        : <p className="subtle">Select a window and start capture.</p>}</div>
    <div className="remote-play-preview-details">{status.preview?.sourceWidth ? `${status.preview.sourceWidth} × ${status.preview.sourceHeight}` : ''}</div>
    {session.captureFailed && <Notice tone="danger">{session.message || 'The selected window could not be captured.'}</Notice>}
  </Card>;
}
