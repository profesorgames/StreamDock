#!/usr/bin/env python3
"""Download hash-locked official artifacts, then build the embedded tools archive."""
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
CACHE = ROOT / '.payload-cache'
PAYLOAD = ROOT / 'payload'


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def download(source):
    path = CACHE / source['sha256']
    if path.exists() and sha256(path) == source['sha256']:
        return source, path
    print('Fetching', source['name'], source['version'], flush=True)
    temp = path.with_suffix('.partial')
    request = urllib.request.Request(source['url'], headers={'User-Agent': 'StreamDock-build/1.0'})
    with urllib.request.urlopen(request, timeout=180) as response, open(temp, 'wb') as out:
        shutil.copyfileobj(response, out)
    if sha256(temp) != source['sha256']:
        temp.unlink(missing_ok=True)
        raise RuntimeError('SHA256 mismatch for ' + source['name'])
    temp.replace(path)
    return source, path


def unpack(archive, destination, predicate=lambda _: True):
    with zipfile.ZipFile(archive) as z:
        for entry in z.infolist():
            if entry.is_dir() or not predicate(entry.filename):
                continue
            target = destination / entry.filename
            if not target.resolve().is_relative_to(destination.resolve()):
                raise RuntimeError('Unsafe archive path')
            target.parent.mkdir(parents=True, exist_ok=True)
            with z.open(entry) as src, open(target, 'wb') as dst:
                shutil.copyfileobj(src, dst)


def main():
    lock = json.loads((ROOT / 'scripts/dependencies.lock.json').read_text())
    CACHE.mkdir(exist_ok=True)
    PAYLOAD.mkdir(exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        sources = list(pool.map(download, lock['sources']))
    with tempfile.TemporaryDirectory(prefix='streamdock-payload-') as directory:
        stage = Path(directory)
        for source, archive in sources:
            kind = source['kind']
            if kind == 'exe':
                shutil.copyfile(archive, stage / 'yt-dlp.exe')
            elif kind == 'deno':
                unpack(archive, stage, lambda n: n == 'deno.exe')
            elif kind == 'python':
                unpack(archive, stage / 'python')
                (stage / 'python/python313._pth').write_text('python313.zip\n.\nLib/site-packages\n', encoding='utf-8')
            elif kind == 'wheel':
                unpack(archive, stage / 'python/Lib/site-packages')
            elif kind == 'ffmpeg':
                with zipfile.ZipFile(archive) as z:
                    for name in z.namelist():
                        if name.endswith(('/bin/ffmpeg.exe', '/bin/ffprobe.exe')):
                            with z.open(name) as src, open(stage / Path(name).name, 'wb') as dst:
                                shutil.copyfileobj(src, dst)
                        if ('LICENSE' in name or 'COPYING' in name) and not name.endswith('/'):
                            target = stage / 'licenses/ffmpeg' / Path(name).name
                            target.parent.mkdir(parents=True, exist_ok=True)
                            target.write_bytes(z.read(name))
        licenses = stage / 'licenses'
        licenses.mkdir(exist_ok=True)
        shutil.copyfile(ROOT / 'THIRD_PARTY_NOTICES.md', licenses / 'THIRD_PARTY_NOTICES.md')
        shutil.copyfile(ROOT / 'LICENSE', licenses / 'STREAMDOCK-LICENSE.txt')
        shutil.copyfile(ROOT / 'scripts/dependencies.lock.json', licenses / 'dependencies.lock.json')
        files = []
        output = PAYLOAD / 'tools.zip'
        with zipfile.ZipFile(output, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for file in sorted(stage.rglob('*')):
                if not file.is_file():
                    continue
                name = file.relative_to(stage).as_posix()
                # Stable archive metadata independent of checkout time and host OS.
                entry = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = 0o100644 << 16
                z.writestr(entry, file.read_bytes(), compresslevel=6)
                files.append({'Path': name, 'Sha256': sha256(file), 'Size': file.stat().st_size})
        digest = hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()
        manifest = {'Id': digest[:20], 'Files': files, 'Sources': lock['sources']}
        (PAYLOAD / 'tools.manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        print(f'Payload {digest[:20]}: {len(files)} files, {output.stat().st_size / 1048576:.1f} MiB')


if __name__ == '__main__':
    main()
