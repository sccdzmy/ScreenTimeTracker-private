"""Compare npm/NuGet lock graphs with a downloaded public advisory archive locally.
Usage: python security/audit-dependencies.py /path/to/advisory-database.tar.gz
The script has no network calls and never uploads the private dependency graph.
"""
import hashlib
import json
import re
import sys
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
pairs = set()
for folder in ['frontend', 'web-extension']:
    text = (ROOT / 'src' / folder / 'pnpm-lock.yaml').read_text()
    text = text.split('\npackages:\n', 1)[1].split('\nsnapshots:', 1)[0]
    for name, version in re.findall(r"^  ['\"]?((?:@[^/\s]+/)?[^'\":\s]+)@([^'\":\s]+)['\"]?:$", text, re.M):
        pairs.add(('npm', name, version))
for path in list((ROOT / 'src').rglob('project.assets.json')) + list((ROOT / 'tests').rglob('project.assets.json')):
    for name, item in json.loads(path.read_text())['libraries'].items():
        if item['type'] == 'package':
            name, version = name.rsplit('/', 1)
            pairs.add(('NuGet', name, version))


def version_key(value):
    value = value.split('+')[0]
    stable, _, pre = value.partition('-')
    numbers = tuple(int(n) for n in stable.split('.'))
    numbers = numbers + (0,) * (4 - len(numbers))
    parts = tuple((0, int(p)) if p.isdigit() else (1, p.lower()) for p in pre.split('.'))
    return numbers, not bool(pre), parts


def affected(version, spec):
    if version in spec.get('versions', []):
        return True
    for window in spec.get('ranges', []):
        if window['type'] not in ('SEMVER', 'ECOSYSTEM'):
            continue
        lower = None
        for event in window['events']:
            if 'introduced' in event:
                lower = event['introduced']
            else:
                inclusive = 'last_affected' in event
                upper = event.get('fixed', event.get('last_affected', event.get('limit')))
                if lower is not None and upper and (lower == '0' or version_key(version) >= version_key(lower)):
                    if version_key(version) <= version_key(upper) if inclusive else version_key(version) < version_key(upper):
                        return True
                lower = None
        if lower is not None and (lower == '0' or version_key(version) >= version_key(lower)):
            return True
    return False

for path in (ROOT / 'artifacts' / 'security-win-x64').glob('*.deps.json'):
    for name, item in json.loads(path.read_text())['libraries'].items():
        if item['type'] == 'package':
            name, version = name.rsplit('/', 1)
            pairs.add(('NuGet', name, version))

index = {}
for ecosystem, name, version in pairs:
    index.setdefault((ecosystem, name.lower()), []).append((name, version))
findings = []
reviewed = 0
archive = Path(sys.argv[1])
with tarfile.open(archive, 'r|gz') as data:
    for entry in data:
        if not entry.isfile() or not entry.name.endswith('.json') or '/github-reviewed/' not in entry.name:
            continue
        advisory = json.load(data.extractfile(entry))
        reviewed += 1
        if advisory.get('withdrawn'):
            continue
        for spec in advisory.get('affected', []):
            package = spec['package']
            for name, version in index.get((package['ecosystem'], package['name'].lower()), []):
                if affected(version, spec):
                    findings.append({'ecosystem': package['ecosystem'], 'name': name, 'version': version, 'advisory': advisory['id'], 'summary': advisory.get('summary'), 'url': 'https://github.com/advisories/' + advisory['id']})
report = {'source': 'https://github.com/github/advisory-database', 'archiveSha256': hashlib.file_digest(archive.open('rb'), 'sha256').hexdigest(), 'reviewedAdvisories': reviewed, 'packageVersionCount': len(pairs), 'npmCount': sum(e == 'npm' for e,n,v in pairs), 'nugetCount': sum(e == 'NuGet' for e,n,v in pairs), 'findings': findings, 'packages': [{'ecosystem': e, 'name': n, 'version': v} for e,n,v in sorted(pairs)]}
(ROOT / 'security' / 'dependency-audit.json').write_text(json.dumps(report, indent=2) + '\n')
print(json.dumps({k:v for k,v in report.items() if k != 'packages'}, indent=2))
sys.exit(bool(findings))
