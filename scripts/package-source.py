"""Package project sources without toolchains, device data, credentials or vendor DLLs."""
from pathlib import Path
import argparse
import os
import subprocess
import zipfile

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, default=root / 'artifacts' / 'ObstacleTimer-0.5.0-source.zip')
args = parser.parse_args()
excluded = {'.tools', 'artifacts', 'node_modules', 'bin', 'obj', 'build', '.gradle',
            '.git', '.idea', '.vs', 'data', 'xcuserdata', 'DerivedData', '__pycache__'}
private_suffixes = {'.pfx', '.pem', '.p12', '.p8', '.key', '.keystore', '.user', '.log', '.exe', '.dll', '.mp4', '.mov', '.webm', '.apk', '.ipa'}
excluded.add('certificates')
commit = 'source archive without Git metadata'
try:
    tracked = subprocess.run(['git', 'ls-files', '-z'], cwd=root, capture_output=True, check=True)
    paths = [root / name for name in tracked.stdout.decode('utf-8').split('\0') if name]
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
except (OSError, subprocess.SubprocessError):
    paths = []
    for folder, directories, names in os.walk(root):
        directories[:] = [name for name in directories if name not in excluded]
        paths.extend(Path(folder) / name for name in names)
files = []
for path in paths:
    relative = path.relative_to(root)
    if not path.is_file() or any(part in excluded for part in relative.parts):
        continue
    if relative.parts[:3] == ('web', 'phone', 'dist'):
        continue
    if path.suffix.lower() in private_suffixes or '.sqlite' in path.name or path.name in {'local.properties', '.DS_Store', '.env'} or path.name.startswith('.env.'):
        continue
    if relative.parts[:2] == ('vendor', 'timy') and path.suffix.lower() == '.dll':
        continue
    files.append((path, relative))
args.output.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED) as archive:
    archive.writestr('obstacle-timer/SOURCE_COMMIT.txt', commit + '\n')
    for path, relative in sorted(files, key=lambda item: item[1].as_posix()):
        archive.write(path, 'obstacle-timer/' + relative.as_posix())
print(f'{len(files)} source files: {args.output}')
