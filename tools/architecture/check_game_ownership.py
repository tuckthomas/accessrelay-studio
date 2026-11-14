"""The shipping host must not compile, bundle or import a game's implementation."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
assert not (root / 'games').exists(), 'Game implementations belong in their plugin repository.'
for relative in ('src/AccessRelayStudio.Runtime', 'src/AccessRelayStudio/Desktop',
                 'src/AccessRelayStudio/DesktopUi/src/game-plugins'):
    for file in (root / relative).rglob('*'):
        if file.suffix not in ('.cs', '.csproj', '.ts', '.tsx') or any(p in ('obj','bin','node_modules') for p in file.parts): continue
        value = file.read_text(encoding='utf-8-sig').lower()
        assert 'nba2k27' not in value and 'nba 2k27' not in value, str(file.relative_to(root))
for file in (root / 'src').rglob('*.csproj'):
    if any(p in ('obj', 'bin', 'node_modules') for p in file.parts): continue
    value = file.read_text(encoding='utf-8-sig').lower()
    assert 'games/nba' not in value and 'plugins/nba' not in value, str(file.relative_to(root))
print('SUCCESS: no game runtime, interface or source-project dependency is bundled in Studio.')
