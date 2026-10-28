"""Package project sources without toolchains, device data, credentials or vendor DLLs."""
from pathlib import Path
import argparse
import zipfile

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root / 'artifacts' / 'ObstacleTimer-0.4.1-source.zip')
args = parser.parse_args()
excluded = {'.tools', 'artifacts', 'node_modules', 'bin', 'obj', 'build', '.gradle',
            '.git', '.idea', '.vs', 'data', 'xcuserdata', 'DerivedData', '__pycache__'}
private_suffixes = {'.pfx', '.key', '.keystore', '.user', '.log'}
files = []
for path in root.rglob('*'):
    relative = path.relative_to(root)
    if not path.is_file() or any(part in excluded for part in relative.parts):
        continue
    if relative.parts[:3] == ('web', 'phone', 'dist'):
        continue
    if path.suffix.lower() in private_suffixes or '.sqlite' in path.name or path.name in {'local.properties', '.DS_Store'}:
        continue
    if relative.parts[:2] == ('vendor', 'timy') and path.suffix.lower() == '.dll':
        continue
    files.append((path, relative))
args.output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED) as archive:
    for path, relative in sorted(files, key=lambda item: item[1].as_posix()):
        archive.write(path, 'obstacle-timer/' + relative.as_posix())
print(f'{len(files)} source files: {args.output}')
