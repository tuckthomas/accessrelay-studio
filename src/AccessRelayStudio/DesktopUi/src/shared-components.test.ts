import { expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
it('uses the shared control library on every app and game-plugin screen', () => {
  const root = resolve('src');
  function inspect(folder: string) {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      if (entry.name === 'components') continue;
      const file = join(folder, entry.name);
      if (entry.isDirectory()) inspect(file);
      else if (entry.name.endsWith('.tsx') && !entry.name.endsWith('.test.tsx') && entry.name !== 'ui.tsx')
        expect(readFileSync(file, 'utf8'), file).not.toMatch(/<(?:input|select|textarea|button)\b/);
    }
  }
  inspect(root);
});
