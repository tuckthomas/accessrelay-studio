import * as React from 'react';
import * as ui from '../ui';
import { GameplayPreview } from '../components/GameplayPreview';
import type { GameUiDescriptor, GameUiHost, GameUiModule } from './api';

export const gameUiHost: GameUiHost = { apiVersion: 1, React, ui, GameplayPreview };
type Entry = { module?: GameUiModule; error?: string };
const Context = React.createContext<Record<string, Entry>>({});
const cache = new Map<string, Promise<GameUiModule>>();
function load(descriptor: GameUiDescriptor) {
  let pending = cache.get(descriptor.uiModule);
  if (!pending) {
    pending = (async () => {
      const url = new URL(descriptor.uiModule);
      if (url.origin !== 'https://plugins.accessrelay.test' || !url.pathname.startsWith('/plugins/') || url.search || url.hash)
        throw new Error('The plugin interface must be a checked, installed Studio resource.');
      const entry = await import(/* @vite-ignore */ url.href);
      if (typeof entry.createPlugin !== 'function') throw new Error('The plugin has no compatible interface entry point.');
      const module = entry.createPlugin(gameUiHost) as GameUiModule;
      if (!module || typeof module.VisionWorkspace !== 'function') throw new Error('The plugin has no compatible training workspace.');
      return module;
    })();
    cache.set(descriptor.uiModule, pending);
  }
  return pending;
}
export function GamePluginProvider({ descriptors = [], initial = {}, children }: {
  descriptors?: GameUiDescriptor[]; initial?: Record<string, GameUiModule>; children: React.ReactNode
}) {
  const [entries, setEntries] = React.useState<Record<string, Entry>>(() => Object.fromEntries(Object.entries(initial).map(([id, module]) => [id, { module }])));
  const identity = JSON.stringify(descriptors);
  React.useEffect(() => {
    if (!descriptors.length) {
      setEntries(Object.fromEntries(Object.entries(initial).map(([id, module]) => [id, { module }])));
      return;
    }
    let active = true;
    setEntries({});
    for (const descriptor of descriptors) void load(descriptor).then(module => {
      if (active) setEntries(previous => ({ ...previous, [descriptor.id]: { module } }));
    }).catch(reason => {
      if (active) setEntries(previous => ({ ...previous, [descriptor.id]: { error: reason instanceof Error ? reason.message : String(reason) } }));
    });
    return () => { active = false; };
  }, [identity]);
  return <Context.Provider value={entries}>{children}</Context.Provider>;
}
export function useGameVisionWorkspace(id: string) { return React.useContext(Context)[id]?.module?.VisionWorkspace; }
export function useGameControllerTuning(id?: string) { return React.useContext(Context)[id ?? '']?.module?.ControllerTuning; }
export function useGamePluginError(id: string) { return React.useContext(Context)[id]?.error; }
