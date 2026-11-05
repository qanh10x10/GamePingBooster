"""Build a credential-free, self-contained Hollow installer. Never installs it."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

repo = Path(__file__).resolve().parent.parent
sha = lambda b: hashlib.sha256(b).hexdigest()


def run(*args):
    subprocess.run([str(a) for a in args], cwd=repo, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--iscc', required=True, type=Path)
    parser.add_argument('--output-dir', required=True, type=Path)
    parser.add_argument('--work-dir', type=Path)
    parser.add_argument('--wintun-zip', type=Path)
    args = parser.parse_args()
    version = (repo / 'VERSION').read_text().strip()
    assert re.fullmatch(r'\d+\.\d+\.\d+', version), 'Numeric release VERSION required'
    work = args.work_dir.resolve() if args.work_dir else Path(tempfile.mkdtemp(prefix='hollow-build-'))
    work.mkdir(parents=True, exist_ok=True)
    stage = work / 'payload'
    assert not stage.exists(), 'Use a fresh work directory; never package stale binaries'
    output = args.output_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    installer = output / ('HollowPingBooster-Setup-' + version + '.exe')
    assert not installer.exists(), 'Refusing to overwrite an existing installer'
    run(sys.executable, repo / 'installer/tests/HollowBrandingCheck.py')
    ui_check = repo / 'installer/tests/HollowUiCheck.py'
    if ui_check.exists():
        run(sys.executable, ui_check)
    for check in ['HollowSetupCheck.ps1', 'HollowPipeCheck.ps1', 'HollowRecoveryCheck.ps1']:
        run('powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', repo / 'installer/tests' / check)
    for project, part in [('Service', 'service'), ('App', 'app')]:
        run('dotnet', 'publish', repo / ('client/src/GamePingBooster.' + project) / ('GamePingBooster.' + project + '.csproj'),
            '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishAot=false',
            '-p:PublishTrimmed=false', '--nologo', '-v', 'minimal', '-o', work / part)
    stage.mkdir()
    for part in ['service', 'app']:
        for source in sorted((work / part).rglob('*')):
            if not source.is_file() or source.suffix == '.pdb':
                continue
            dest = stage / source.relative_to(work / part)
            if dest.exists():
                assert sha(dest.read_bytes()) == sha(source.read_bytes()), 'Conflicting runtime: ' + dest.name
            else:
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, dest)
    driver = args.wintun_zip or work / 'wintun-0.14.1.zip'
    if not driver.exists():
        urllib.request.urlretrieve('https://www.wintun.net/builds/wintun-0.14.1.zip', driver)
    assert sha(driver.read_bytes()) == '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51', 'Unexpected official driver archive'
    with zipfile.ZipFile(driver) as z:
        (stage / 'wintun.dll').write_bytes(z.read('wintun/bin/amd64/wintun.dll'))
        (stage / 'WINTUN-LICENSE.txt').write_bytes(z.read('wintun/LICENSE.txt'))
    for source, name in [(repo / 'LICENSE', 'APP-LICENSE'), (repo / 'README.md', 'README.md'),
                         (repo / 'docs/HOLLOW-SELFHOST.md', 'HOLLOW-SELFHOST.md'),
                         (repo / 'installer/setup-service.ps1', 'setup-service.ps1'),
                         (repo / 'installer/SETUP-NOTES.txt', 'SETUP-NOTES.txt')]:
        shutil.copy2(source, stage / name)
    for name in ['GamePingBooster.runtimeconfig.json', 'gpb-service.runtimeconfig.json']:
        opts = json.loads((stage / name).read_text())['runtimeOptions']
        assert opts.get('includedFrameworks') and not opts.get('framework'), 'Missing self-contained runtime'
    info = {'name': 'Hollow Ping Booster', 'version': version, 'service': 'HollowPingBooster',
            'relay': 'ping.hollow-agent.xyz:51820', 'self_contained': True, 'includes_credentials': False,
            'git_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
            'source_dirty': bool(subprocess.check_output(['git', 'status', '--porcelain'], cwd=repo)),
            'clean_machine_install': 'NOT TESTED'}
    (stage / 'BUILD-INFO.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
    for f in stage.rglob('*'):
        if f.is_file():
            assert f.name.lower() not in {'config.json', 'device.key', 'client-id', 'token.hex', 'refresh', '.env', 'psk'}
            assert f.suffix.lower() not in {'.key', '.pem', '.log', '.psk', '.pdb'}
    manifest = {f.relative_to(stage).as_posix(): sha(f.read_bytes()) for f in sorted(stage.rglob('*')) if f.is_file()}
    (stage / 'SHA256SUMS.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    run(args.iscc.resolve(), '/DAppVersion=' + version, '/DPayload=' + str(stage.resolve()),
        '/O' + str(output), repo / 'installer/HollowSetup.iss')
    data = installer.read_bytes()
    assert data[:2] == b'MZ' and b'Inno Setup Setup Data' in data
    digest = sha(data)
    (output / (installer.name + '.sha256')).write_text(digest + '  ' + installer.name + '\n')
    print(json.dumps({'installer': str(installer), 'sha256': digest, 'bytes': len(data),
                      'payload_files': len(manifest), 'installed_on_build_machine': False}, indent=2))


if __name__ == '__main__':
    main()
