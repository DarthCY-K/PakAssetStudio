import argparse
import ctypes
import json
import os
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path


_DLL = None


def load_assimp(dll_path):
    global _DLL
    if _DLL is None:
        dll = ctypes.CDLL(dll_path)
        dll.aiImportFile.argtypes = [ctypes.c_char_p, ctypes.c_uint]
        dll.aiImportFile.restype = ctypes.c_void_p
        dll.aiExportScene.argtypes = [
            ctypes.c_void_p,
            ctypes.c_char_p,
            ctypes.c_char_p,
            ctypes.c_uint,
        ]
        dll.aiExportScene.restype = ctypes.c_int
        dll.aiReleaseImport.argtypes = [ctypes.c_void_p]
        dll.aiGetErrorString.restype = ctypes.c_char_p
        _DLL = dll
    return _DLL


def error_string(dll):
    value = dll.aiGetErrorString()
    return value.decode('utf-8', errors='replace') if value else 'unknown Assimp error'


def write_without_empty_primitives(source, destination):
    document = json.loads(source.read_text(encoding='utf-8'))
    removed = 0
    for mesh in document.get('meshes', []):
        retained = []
        for primitive in mesh.get('primitives', []):
            position = primitive.get('attributes', {}).get('POSITION')
            if position is not None and document['accessors'][position].get('count', 0) == 0:
                removed += 1
            else:
                retained.append(primitive)
        mesh['primitives'] = retained
    if removed == 0:
        return 0

    accessor_refs = set()
    for mesh in document.get('meshes', []):
        for primitive in mesh.get('primitives', []):
            if 'indices' in primitive:
                accessor_refs.add(primitive['indices'])
            accessor_refs.update(primitive.get('attributes', {}).values())
            for target in primitive.get('targets', []):
                accessor_refs.update(target.values())
    for skin in document.get('skins', []):
        if 'inverseBindMatrices' in skin:
            accessor_refs.add(skin['inverseBindMatrices'])
    for animation in document.get('animations', []):
        for sampler in animation.get('samplers', []):
            accessor_refs.update((sampler['input'], sampler['output']))

    accessor_map = {old: new for new, old in enumerate(sorted(accessor_refs))}
    document['accessors'] = [document['accessors'][old] for old in sorted(accessor_refs)]
    for mesh in document.get('meshes', []):
        for primitive in mesh.get('primitives', []):
            if 'indices' in primitive:
                primitive['indices'] = accessor_map[primitive['indices']]
            primitive['attributes'] = {
                key: accessor_map[value] for key, value in primitive.get('attributes', {}).items()
            }
            for target in primitive.get('targets', []):
                for key, value in target.items():
                    target[key] = accessor_map[value]
    for skin in document.get('skins', []):
        if 'inverseBindMatrices' in skin:
            skin['inverseBindMatrices'] = accessor_map[skin['inverseBindMatrices']]
    for animation in document.get('animations', []):
        for sampler in animation.get('samplers', []):
            sampler['input'] = accessor_map[sampler['input']]
            sampler['output'] = accessor_map[sampler['output']]

    buffer_view_refs = set()
    for accessor in document.get('accessors', []):
        if 'bufferView' in accessor:
            buffer_view_refs.add(accessor['bufferView'])
        sparse = accessor.get('sparse')
        if sparse:
            buffer_view_refs.add(sparse['indices']['bufferView'])
            buffer_view_refs.add(sparse['values']['bufferView'])
    for image in document.get('images', []):
        if 'bufferView' in image:
            buffer_view_refs.add(image['bufferView'])

    view_map = {old: new for new, old in enumerate(sorted(buffer_view_refs))}
    document['bufferViews'] = [
        document['bufferViews'][old] for old in sorted(buffer_view_refs)
    ]
    for accessor in document.get('accessors', []):
        if 'bufferView' in accessor:
            accessor['bufferView'] = view_map[accessor['bufferView']]
        sparse = accessor.get('sparse')
        if sparse:
            sparse['indices']['bufferView'] = view_map[sparse['indices']['bufferView']]
            sparse['values']['bufferView'] = view_map[sparse['values']['bufferView']]
    for image in document.get('images', []):
        if 'bufferView' in image:
            image['bufferView'] = view_map[image['bufferView']]

    destination.write_text(json.dumps(document, separators=(',', ':')), encoding='utf-8')
    return removed


def convert_one(arguments):
    source_value, dll_path = arguments
    source = Path(source_value)
    output = source.with_suffix('.fbx')
    temporary = source.with_suffix('.fbx.tmp')
    sanitized = source.with_suffix('.sanitized.gltf')
    dll = load_assimp(dll_path)

    try:
        if output.exists():
            verification = dll.aiImportFile(os.fsencode(output), 0)
            if verification:
                dll.aiReleaseImport(verification)
                source.unlink()
                return source_value, 'existing-valid', ''
            output.unlink()

        scene = dll.aiImportFile(os.fsencode(source), 0)
        if not scene:
            original_error = error_string(dll)
            removed = write_without_empty_primitives(source, sanitized)
            if removed:
                scene = dll.aiImportFile(os.fsencode(sanitized), 0)
            if not scene:
                return source_value, 'failed', 'import: ' + original_error
        try:
            result = dll.aiExportScene(scene, b'fbx', os.fsencode(temporary), 0)
            if result != 0:
                return source_value, 'failed', 'export: ' + error_string(dll)
        finally:
            dll.aiReleaseImport(scene)

        verification = dll.aiImportFile(os.fsencode(temporary), 0)
        if not verification:
            return source_value, 'failed', 'verification: ' + error_string(dll)
        dll.aiReleaseImport(verification)

        os.replace(temporary, output)
        source.unlink()
        return source_value, 'converted', ''
    except Exception as exc:
        return source_value, 'failed', str(exc)
    finally:
        if temporary.exists():
            temporary.unlink()
        if sanitized.exists():
            sanitized.unlink()


def main():
    parser = argparse.ArgumentParser(
        description='Convert every glTF under a directory to verified binary FBX.'
    )
    parser.add_argument('root', type=Path)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=min(8, os.cpu_count() or 1))
    args = parser.parse_args()

    root = args.root.resolve()
    dll_path = str(args.dll.resolve())
    sources = sorted(root.rglob('*.gltf'))
    total = len(sources)
    failures = []
    converted = 0

    print(f'Found {total} glTF files; workers={args.workers}', flush=True)
    work = ((str(path), dll_path) for path in sources)
    with ProcessPoolExecutor(max_workers=args.workers) as executor:
        for index, (source, status, detail) in enumerate(executor.map(convert_one, work), 1):
            if status == 'failed':
                failures.append((source, detail))
            else:
                converted += 1
            if index % 100 == 0 or index == total:
                print(
                    f'Processed {index}/{total}; converted={converted}; failed={len(failures)}',
                    flush=True,
                )

    failure_log = root / 'fbx-conversion-failures.txt'
    if failures:
        failure_log.write_text(
            ''.join(f'{path}\t{detail}\n' for path, detail in failures),
            encoding='utf-8',
        )
        print(f'Failures retained as glTF; see {failure_log}', flush=True)
        raise SystemExit(1)
    if failure_log.exists():
        failure_log.unlink()

    inventory = root / 'model-files.txt'
    fbx_files = sorted(root.rglob('*.fbx'))
    inventory.write_text(
        ''.join(f'{path.relative_to(root)}\n' for path in fbx_files),
        encoding='utf-8-sig',
    )
    print(f'Complete: {len(fbx_files)} FBX files; 0 glTF files remain', flush=True)


if __name__ == '__main__':
    main()
