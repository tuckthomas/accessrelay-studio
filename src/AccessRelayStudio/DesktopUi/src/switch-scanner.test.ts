import { describe, expect, it } from 'vitest';
import { navigationEvents, type NavigationSnapshot } from './switch-scanner';

describe('ordered host navigation events', () => {
  const snapshot: NavigationSnapshot = { enabled: true, generation: 2, nowMs: 100,
    events: [{ sequence: 1, timestampMs: 80, action: 'step' }, { sequence: 2, timestampMs: 90, action: 'activate' }] };
  it('preserves order and ignores repeat and older snapshots', () => {
    const cursor = { generation: -1, sequence: 0 };
    expect(navigationEvents(snapshot, cursor, 105)).toEqual(['step', 'activate']);
    expect(navigationEvents(snapshot, cursor, 110)).toEqual([]);
    expect(navigationEvents({ ...snapshot, generation: 1 }, cursor, 110)).toEqual([]);
    expect(navigationEvents({ ...snapshot, generation: 3 }, cursor, 110)).toEqual(['step', 'activate']);
  });
  it('rejects loss, stale delivery, malformed ordering and future timestamps', () => {
    for (const value of [
      { ...snapshot, events: [snapshot.events[1]] },
      { ...snapshot, events: [snapshot.events[1], snapshot.events[0]] },
      { ...snapshot, events: [{ ...snapshot.events[0], timestampMs: 101 }] },
      { ...snapshot, events: Array(33).fill(snapshot.events[0]) }
    ]) expect(() => navigationEvents(value, { generation: -1, sequence: 0 }, 105)).toThrow();
    expect(() => navigationEvents(snapshot, { generation: -1, sequence: 0 }, 1200)).toThrow(/delayed or lost/);
  });
});
