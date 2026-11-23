#!/usr/bin/python3
"""実起動宣言だけを抽出してORTの共存を検査する。導入・音声・推論はしない。"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
PROBE = r'''
import ctypes, json, sys
class ApiBase(ctypes.Structure):
    _fields_ = [('get_api', ctypes.CFUNCTYPE(ctypes.c_void_p, ctypes.c_uint32)),
                ('version', ctypes.CFUNCTYPE(ctypes.c_char_p))]
# Linux hostの絶対path先行loadでも、後の名前指定importを保証しない。
ctypes.CDLL(sys.argv[1], mode=ctypes.RTLD_LOCAL)
ort = ctypes.CDLL('libonnxruntime.so')
ort.OrtGetApiBase.restype = ctypes.POINTER(ApiBase)
api = ort.OrtGetApiBase().contents
print(json.dumps({'version': api.version().decode(), 'api31': bool(api.get_api(31))}))
'''


def export_line(relative):
    lines = (ROOT / relative).read_text().splitlines()
    matches = [line for line in lines if line.startswith('export LD_LIBRARY_PATH=')]
    assert len(matches) == 1, relative
    # install.shのunquoted heredocから生成される実際の宣言。
    return matches[0].replace(r'\$', '$')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--new-runtime', type=Path, required=True)
    parser.add_argument('--old-runtime', type=Path, required=True)
    args = parser.parse_args()
    current, legacy = args.new_runtime.resolve(), args.old_runtime.resolve()
    assert current.is_file() and legacy.is_file() and current != legacy
    for relative in ('scripts/linux/package-tarball.sh', 'scripts/linux/install.sh',
                     'scripts/linux/start-mobmate.sh', 'scripts/linux/piper_wrapper.sh'):
        subprocess.run(['/bin/bash', '-n', str(ROOT / relative)], check=True)
    service = (ROOT / 'scripts/linux/mobmate.service').read_text().splitlines()
    service_paths = [line.split('=', 2)[2] for line in service if line.startswith('Environment=LD_LIBRARY_PATH=')]
    assert len(service_paths) == 1
    failures = []
    # 所有するsymlinkのみをtempへ置き、原runtime/設定/サービスは触らない。
    with tempfile.TemporaryDirectory(prefix='linux-ort-launch-', dir=ROOT / 'temp') as directory:
        root = Path(directory)
        libs = root / 'libs'
        for name, target in (('sherpa-onnx', current), ('piper_plus', legacy)):
            folder = libs / name
            folder.mkdir(parents=True)
            (folder / 'libonnxruntime.so').symlink_to(target)
        inherited = str(libs / 'piper_plus')
        environment = {**os.environ, 'APP_DIR': str(root), 'LIBS_DIR': str(libs),
                       'PIPER_DIR': str(libs / 'piper_plus'), 'LD_LIBRARY_PATH': inherited}
        for relative in ('scripts/linux/package-tarball.sh', 'scripts/linux/install.sh',
                         'scripts/linux/start-mobmate.sh', 'scripts/linux/mobmate.service',
                         'scripts/linux/piper_wrapper.sh'):
            if relative.endswith('.service'):
                declaration = 'export LD_LIBRARY_PATH="' + service_paths[0].replace('%h/_MobMateV2', str(root)) + '"'
            else:
                declaration = export_line(relative)
            expanded = subprocess.run(['/bin/bash', '-c', declaration + '\nprintf "%s" "$LD_LIBRARY_PATH"'],
                                      env=environment, text=True, capture_output=True, check=True).stdout
            child = relative.endswith('piper_wrapper.sh')
            expected = legacy if child else current
            paths = expanded.split(':')
            provider = next((Path(path) / 'libonnxruntime.so' for path in paths
                             if (Path(path) / 'libonnxruntime.so').is_file()), None)
            result = subprocess.run([sys.executable, '-B', '-c', PROBE, str(current)],
                                    env={**environment, 'LD_LIBRARY_PATH': expanded},
                                    text=True, capture_output=True, check=True)
            observed = json.loads(result.stdout)
            wanted = {'version': '1.20.1', 'api31': False} if child else {'version': '1.31.1', 'api31': True}
            if provider is None or provider.resolve() != expected or observed != wanted:
                failures.append(f'{relative}: provider={provider}, observed={observed}')
            else:
                print(f'PASS {relative}: {observed}')
        assert not failures, '\n'.join(failures)
    print('PASS hostは新版ORT、Piper子processは既存専用providerを維持。音声/推論/実サービス導入の検証ではない。')


if __name__ == '__main__':
    main()
