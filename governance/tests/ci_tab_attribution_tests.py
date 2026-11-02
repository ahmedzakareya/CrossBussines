"""Exercise the real CI attribution resolver without Git or database writes."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
cases = [
    ('tab/tab-1-security', '', 'pull_request', 'TAB-1'),
    ('tab/tab-6-hr', '', 'pull_request', 'TAB-6'),
    ('feature/unattributed', '', 'pull_request', None),
    ('tab/tab-99-unknown', '', 'pull_request', None),
    ('integration', 'TAB-2', 'workflow_dispatch', 'TAB-2'),
    ('tab/tab-2-work', 'TAB-1', 'workflow_dispatch', None),
    ('integration', 'TAB-0', 'workflow_dispatch', None),
    ('integration', 'TAB-1', 'push', None),
]
for branch, requested, event, expected in cases:
    result = subprocess.run([
        'powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
        str(root / 'governance/tools/resolve-ci-tab.ps1'),
        '-Branch', branch, '-RequestedTab', requested, '-EventName', event,
    ], capture_output=True, text=True, timeout=20)
    if expected is None:
        assert result.returncode != 0, (branch, event, result.stdout)
    else:
        assert result.returncode == 0 and result.stdout.strip() == expected, result.stderr
print(f'{len(cases)} attribution controls passed; no repository or database writes.')
