import { Nba2K27SequenceTuning } from './nba2k27/ControllerTuning';
import { Nba2K27VisionWorkspace } from './nba2k27/VisionWorkspace';

// Reviewed, bundled presentation adapters. Pack imports cannot execute arbitrary UI code.
const workspaces = { nba2k27: Nba2K27VisionWorkspace };
export function gameVisionWorkspace(id: string) {
  return Object.hasOwn(workspaces, id) ? workspaces[id as keyof typeof workspaces] : undefined;
}

export function gameControllerTuning(id?: string) { return id === 'nba2k27' ? Nba2K27SequenceTuning : undefined; }
