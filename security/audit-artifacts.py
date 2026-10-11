"""Inventory runtime outputs without uploading artifacts or dependency information."""
import hashlib
import json
from pathlib import Path

root = Path(__file__).resolve().parent.parent
outputs = {
    'windows': root / 'artifacts' / 'security-win-x64',
    'frontend': root / 'src' / 'frontend' / 'dist',
    'chrome': root / 'src' / 'web-extension' / '.output' / 'chrome-mv3',
    'firefox': root / 'src' / 'web-extension' / '.output' / 'firefox-mv2',
}
report = {}
for kind, directory in outputs.items():
    if not directory.is_dir():
        raise RuntimeError('Build the output before auditing: ' + kind)
    files = [p for p in directory.rglob('*') if p.is_file()]
    entry = {'fileCount': len(files), 'containsNodeForge': any(b'node-forge' in p.read_bytes() for p in files if p.suffix in ('.js', '.json'))}
    if kind == 'windows':
        entry['runtime'] = json.loads((directory / 'ScreenTimeTracker.runtimeconfig.json').read_text())['runtimeOptions'].get('includedFrameworks')
        entry['packageLibraries'] = sorted(json.loads((directory / 'ScreenTimeTracker.deps.json').read_text())['libraries'])
        entry['executableSha256'] = hashlib.file_digest((directory / 'ScreenTimeTracker.exe').open('rb'), 'sha256').hexdigest()
        entry['includesFirefoxExtension'] = (directory / 'web-extension-firefox-mv2' / 'manifest.json').is_file()
        entry['includesChromeExtension'] = (directory / 'web-extension-chrome-mv3' / 'manifest.json').is_file()
    elif kind != 'frontend':
        manifest = json.loads((directory / 'manifest.json').read_text())
        entry['manifestVersion'] = manifest['manifest_version']
        entry['contentScripts'] = manifest['content_scripts']
        entry['browserSpecificSettings'] = manifest.get('browser_specific_settings')
        entry['backgroundSha256'] = hashlib.file_digest((directory / 'background.js').open('rb'), 'sha256').hexdigest()
    report[kind] = entry
(root / 'security' / 'artifact-audit.json').write_text(json.dumps(report, indent=2) + '\n')
assert not any(v['containsNodeForge'] for v in report.values()), 'Development-only forge appeared in a runtime artifact'
print('Runtime outputs inventoried; development-only node-forge absent.')
