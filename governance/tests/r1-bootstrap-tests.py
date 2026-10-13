"""Real Git/PowerShell refusal tests for the one-time immutable import, never database tests."""
from pathlib import Path
import os, shutil, subprocess, tempfile

repo = Path(__file__).resolve().parents[2]
helper = repo / 'governance/tools/check-r1-bootstrap.ps1'
approved = '6abc412fdaa9f35a0b082204ee6baf8db1d98ef0'
base = '480c3d49f7b862b0662af161db824360e80ff5df'
temporary = Path(tempfile.mkdtemp(prefix='crossbuy-r1-bootstrap-')).resolve()
fixture = temporary / 'repo'

def git(*arguments):
    return subprocess.run(['git', '-c', 'core.excludesFile=.gitignore', *arguments], cwd=fixture,
                          check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

def check(expected=0, **overrides):
    values = dict(Base=base, Branch='tab/tab-1-r1-qualified-20261013', Actor='ahmedzakareya',
                  RepositoryOwner='ahmedzakareya', SourceRepository='ahmedzakareya/CrossBussines',
                  TargetRepository='ahmedzakareya/CrossBussines')
    values.update(overrides)
    args = ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(fixture / helper.relative_to(repo))]
    for key, value in values.items():
        args.extend(['-' + key, value])
    result = subprocess.run(args, capture_output=True, text=True, timeout=30)
    assert result.returncode == expected, (result.stdout, result.stderr)

try:
    subprocess.run(['git', '-c', f'safe.directory={repo.as_posix()}', 'clone', '--shared', '--no-checkout',
                    '--single-branch', str(repo), str(fixture)], check=True, capture_output=True)
    git('checkout', '--detach', approved)
    target = fixture / helper.relative_to(repo)
    shutil.copyfile(helper, target)
    check()
    check(1, Actor='another-contributor')
    check(1, Branch='tab/tab-2-feature')
    check(1, Base='0' * 40)
    check(1, SourceRepository='fork/CrossBussines')
    # Allowed delivery-only files are committed; an unchanged source snapshot still passes.
    git('add', '--', helper.relative_to(repo).as_posix())
    git('-c', 'user.name=R1 Test', '-c', 'user.email=r1-test@example.invalid', 'commit', '-m', 'CI-only follow-up')
    check()
    module = fixture / 'CrossBuy/Views/Calendar/Index.cshtml'
    module.write_bytes(module.read_bytes() + b'\n<!-- unapproved application edit -->\n')
    git('add', '--', 'CrossBuy/Views/Calendar/Index.cshtml')
    git('-c', 'user.name=R1 Test', '-c', 'user.email=r1-test@example.invalid', 'commit', '-m', 'Unapproved module edit')
    check(1)
    print('7 bootstrap controls passed; wrong actor, branch, base, fork and application edits refused.')
finally:
    if temporary.parent != Path(tempfile.gettempdir()).resolve() or not temporary.name.startswith('crossbuy-r1-bootstrap-'):
        raise RuntimeError('Refusing cleanup outside the unique temporary fixture.')
    # Windows Git object files can be read-only; limit permission repair to this verified fixture.
    def repair(function, path, error):
        os.chmod(path, 0o700)
        function(path)
    shutil.rmtree(temporary, onerror=repair)
