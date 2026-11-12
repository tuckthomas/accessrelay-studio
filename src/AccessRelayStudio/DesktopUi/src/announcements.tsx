import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';

type Announcement = { polite: string; alert: string; commandError: string; sequence: number };
const empty: Announcement = { polite: '', alert: '', commandError: '', sequence: 0 };
export const AnnouncementContext = createContext(empty);

// Compare semantic transitions, not frame counters, audio levels or timings.
export function useStatusAnnouncements(ready: boolean, controller: any, voice: any, vision: any, commandError: string) {
  const previous = useRef<ReturnType<typeof observe> | null>(null);
  const [announcement, setAnnouncement] = useState(empty);
  useEffect(() => {
    if (!ready) return;
    const current = observe(controller, voice, vision, commandError), before = previous.current;
    previous.current = current;
    if (!before) {
      const alert = [...new Set([current.feedbackError, current.voiceError, current.visionError, commandError].filter(Boolean))].join(' ');
      if (alert) setAnnouncement(value => ({ polite: '', alert, commandError, sequence: value.sequence + 1 }));
      return;
    }
    const polite: string[] = [], alerts: string[] = [];
    if (current.feedbackError && current.feedbackError !== before.feedbackError) alerts.push(current.feedbackError);
    if (current.connected !== before.connected) polite.push(current.connected ? 'Controller connected.' : 'Controller disconnected.');
    if (current.voiceError && current.voiceError !== before.voiceError) alerts.push(current.voiceError);
    else if (current.voicePhase !== before.voicePhase) polite.push(current.voicePhase === 'starting' ? 'Starting voice processing.'
      : current.voicePhase === 'switching' ? 'Switching voice profile.'
      : current.voicePhase === 'running' ? 'Voice processing is live.' : 'Voice processing stopped.');
    else if (current.voicePhase === 'running' && current.voiceMessage !== before.voiceMessage && current.voiceMessage) polite.push(current.voiceMessage);
    if (current.visionError && current.visionError !== before.visionError) alerts.push(current.visionError);
    else if (current.running !== before.running) polite.push(current.running ? 'Remote Play live view started.' : 'Remote Play live view stopped.');
    if (current.modelReady && !before.modelReady) polite.push('Vision model ready.');
    if (current.armed !== before.armed) polite.push(current.armed ? 'Vision assistance armed.' : 'Vision assistance disarmed.');
    if (current.recording !== before.recording) polite.push(current.recording ? 'Training clip recording started.' : 'Training clip recording stopped.');
    if (current.notice && current.notice !== before.notice) polite.push(current.notice);
    if (commandError && commandError !== before.commandError) alerts.push(commandError);
    if (polite.length || alerts.length || commandError !== before.commandError) setAnnouncement(value => ({
      polite: [...new Set(polite)].join(' '), alert: [...new Set(alerts)].join(' '), commandError, sequence: value.sequence + 1
    }));
  }, [ready, controller, voice, vision, commandError]);
  return useMemo(() => ({ ...announcement, commandError }), [announcement, commandError]);
}

function observe(controller: any, voice: any, vision: any, commandError: string) {
  const session = vision.session || {};
  return {
    connected: !!controller.connected,
    feedbackError: String(controller.feedback?.error || ''),
    voicePhase: voice.isStarting ? (voice.operation === 'switch' ? 'switching' : 'starting') : voice.isRunning ? 'running' : 'stopped',
    voiceMessage: String(voice.message || ''), voiceError: String(voice.error || voice.clipError || ''),
    running: !!(vision.running || session.running), modelReady: !!session.modelReady,
    armed: !!vision.output?.armed, recording: !!vision.training?.isRecording,
    visionError: session.captureFailed || session.modelLoadFailed || session.sourceStale ? String(session.message || 'Vision observation is unavailable.') : '',
    notice: String(vision.notice || ''), commandError
  };
}

export function LiveAnnouncements() {
  const announcement = useContext(AnnouncementContext);
  const previous = useRef(announcement);
  const [spoken, setSpoken] = useState({ polite: '', alert: '' });
  useEffect(() => {
    // A newly opened modal starts with empty regions, rather than replaying an old
    // announcement. Each modal has its own regions because its background is inert.
    if (previous.current === announcement) return;
    previous.current = announcement;
    setSpoken({ polite: '', alert: '' });
    const timer = window.setTimeout(() => setSpoken(announcement), 50);
    return () => window.clearTimeout(timer);
  }, [announcement]);
  return <div className="sr-only" data-status-announcer="">
    <div role="status" aria-label="Studio status updates" aria-live="polite" aria-atomic="true">{spoken.polite}</div>
    <div role="alert" aria-label="Studio errors" aria-live="assertive" aria-atomic="true">{spoken.alert}</div>
  </div>;
}
