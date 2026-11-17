import type * as React from 'react';
import type * as Ui from '../ui';
import type { ControllerTrainer } from '../ControllerTrainer';
import type { GameplayPreview, GameplayWindowControls } from '../components/GameplayPreview';

export type { PageProps } from '../ui';
export type VisionMode = 'live' | 'training';
export type GameControllerHost = { state: any; status: any; command: (action: string, args?: any) => Promise<any> };
export type VisionWorkspaceProps = Ui.PageProps & { mode: VisionMode; captureWindow?: GameplayWindowControls; controller?: GameControllerHost };
export type ControllerTuningProps = { command: Ui.PageProps['command']; variant?: string; id?: string; disabled?: boolean; onApplied?: () => Promise<void> };
export type GameUiHost = { apiVersion: 1; React: typeof React; ui: typeof Ui; GameplayPreview: typeof GameplayPreview; ControllerTrainer?: typeof ControllerTrainer };
export type GameUiModule = { VisionWorkspace?: React.ComponentType<VisionWorkspaceProps>; ControllerTuning?: React.ComponentType<ControllerTuningProps> };
export type GameUiDescriptor = { id: string; packageId: string; version: string; uiModule: string; coverUrl?: string | null };
