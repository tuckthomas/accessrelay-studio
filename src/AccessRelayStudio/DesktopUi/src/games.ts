export type GameMeta = {
  id: string;
  title: string;
  subtitle: string;
  accent: string;
};

export function getGameMeta(gameId: string, preferredTitle?: string, preferredSubtitle?: string): GameMeta {
  const cleaned = (preferredTitle || '').trim();
  const label = cleaned || gameId
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map(part => {
      const match = /^([a-z]+)(\d[a-z0-9]*)$/i.exec(part);
      if (match) return `${match[1].toUpperCase()} ${match[2].toUpperCase()}`;
      return part.charAt(0).toUpperCase() + part.slice(1);
    })
    .join(' ');
  return {
    id: gameId,
    title: label || 'Game Add-On',
    subtitle: preferredSubtitle || 'Game-specific scripts, vision add-ons, and presets',
    accent: '#3b82f6',
  };
}
