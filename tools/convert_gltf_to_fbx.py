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


def is_valid_fbx(dll, path):
    scene = dll.aiImportFile(os.fsencode(path), 0)
    if not scene:
        return False
    dll.aiReleaseImport(scene)
    return True


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
    source_value, dll_path, overwrite = arguments
    source = Path(source_value)
    output = source.with_suffix('.fbx')
    temporary = source.with_name(f'.{source.stem}.{os.getpid()}.fbx.tmp')
    sanitized = source.with_name(f'.{source.stem}.{os.getpid()}.sanitized.gltf')
    dll = load_assimp(dll_path)

    try:
        if output.exists() and not overwrite:
            if is_valid_fbx(dll, output):
                return source_value, 'skipped-existing', 'source glTF retained'
            return source_value, 'failed', 'existing FBX is invalid; enable overwrite to replace it'

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

        if not is_valid_fbx(dll, temporary):
            return source_value, 'failed', 'verification: ' + error_string(dll)

        os.replace(temporary, output)
        return source_value, 'converted', ''
    except Exception as exc:
        return source_value, 'failed', str(exc)
    finally:
        if temporary.exists():
            temporary.unlink()
        if sanitized.exists():
            sanitized.unlink()


def referenced_local_buffers(source, root):
    try:
        document = json.loads(source.read_text(encoding='utf-8'))
        result = set()
        for buffer in document.get('buffers', []):
            uri = buffer.get('uri')
            if not uri or uri.startswith('data:') or '://' in uri:
                continue
            path = (source.parent / uri).resolve()
            if path.is_relative_to(root):
                result.add(path)
        return result
    except (OSError, ValueError, TypeError, AttributeError):
        return None


def remove_converted_sources(all_sources, converted_sources, root):
    root = Path(root).resolve()
    converted = {
        path for path in (Path(value).resolve() for value in converted_sources)
        if path.is_relative_to(root)
    }
    references = {}
    references_complete = True
    for source in all_sources:
        buffers = referenced_local_buffers(source, root)
        if buffers is None:
            references_complete = False
            continue
        for buffer in buffers:
            references.setdefault(buffer, set()).add(source.resolve())

    for source in converted:
        if source.exists():
            source.unlink()
    if references_complete:
        for buffer, owners in references.items():
            if owners and owners.issubset(converted) and buffer.exists():
                buffer.unlink()


def main():
    parser = argparse.ArgumentParser(
        description='Convert every glTF under a directory to verified binary FBX.'
    )
    parser.add_argument('root', type=Path)
    parser.add_argument('--dll', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=min(8, os.cpu_count() or 1))
    parser.add_argument('--overwrite', action='store_true',
                        help='replace existing FBX files')
    parser.add_argument('--delete-source', action='store_true',
                        help='delete successfully converted glTF and unshared local buffers')
    args = parser.parse_args()

    root = args.root.resolve()
    dll_path = str(args.dll.resolve())
    stale_sanitized = sorted(
        path for path in root.rglob('*.gltf')
        if '.sanitized.' in path.name
    )
    if stale_sanitized:
        print(
            f'ERROR: found {len(stale_sanitized)} stale sanitized glTF files; '
            'remove them after confirming no converter is running',
            flush=True,
        )
        raise SystemExit(2)
    sources = sorted(root.rglob('*.gltf'))
    total = len(sources)
    if total == 0:
        print('ERROR: no glTF files found for FBX conversion', flush=True)
        raise SystemExit(2)
    workers = max(1, args.workers)
    failures = []
    converted_sources = []
    skipped = 0

    print(f'Found {total} glTF files; workers={workers}; overwrite={args.overwrite}', flush=True)
    work = ((str(path), dll_path, args.overwrite) for path in sources)
    with ProcessPoolExecutor(max_workers=workers) as executor:
        for index, (source, status, detail) in enumerate(executor.map(convert_one, work), 1):
            if status == 'failed':
                failures.append((source, detail))
            elif status == 'converted':
                converted_sources.append(source)
            else:
                skipped += 1
            if index % 100 == 0 or index == total:
                print(
                    f'Processed {index}/{total}; converted={len(converted_sources)}; '
                    f'skipped={skipped}; failed={len(failures)}',
                    flush=True,
                )

    if args.delete_source:
        remove_converted_sources(sources, converted_sources, root)

    if skipped:
        print(
            f'WARNING: {skipped} existing FBX files were not overwritten; source glTF retained',
            flush=True,
        )

    failure_log = root / 'fbx-conversion-failures.txt'
    if failures:
        failure_log.write_text(
            ''.join(f'{path}\t{detail}\n' for path, detail in failures),
            encoding='utf-8',
        )
        print(f'Failures retained as glTF; see {failure_log}', flush=True)
    elif failure_log.exists():
        failure_log.unlink()

    inventory = root / 'model-files.txt'
    fbx_files = sorted(root.rglob('*.fbx'))
    remaining_gltf = sorted(root.rglob('*.gltf'))
    inventory.write_text(
        ''.join(f'{path.relative_to(root)}\n' for path in fbx_files),
        encoding='utf-8-sig',
    )
    print(
        f'Complete: {len(fbx_files)} FBX files; {len(remaining_gltf)} glTF files remain',
        flush=True,
    )
    if failures:
        raise SystemExit(1)
    if skipped:
        raise SystemExit(3)


if __name__ == '__main__':
    main()
