"""Merge fragmented glTF exports (one mesh per file) into a single glTF/FBX per model.

UModel exports cooked mesh assets as individual glTF files. Asset directory
layout alone does not prove that files belong to one assembled model, so this
tool defaults to conservative explicit-part naming and preserves all sources.
Directory and broad prefix grouping remain manual CLI modes.

Grouping modes:
  explicit - conservatively merge only names ending in explicit part markers
             such as `_part01`, `_piece02`, `_mesh03`, or `_polySurface4`.
  prefix   - cluster files by normalized trailing numeric tokens.
  dir      - merge a whole directory; retained for manual CLI use only.

The application uses `explicit`, keeps every source file, and writes a separate
`__merged.gltf`. LOD filtering is applied inside each group, never across an
entire directory.
"""

import argparse
import ctypes
import hashlib
import json
import os
import re
from pathlib import Path


# ---------------------------------------------------------------- Assimp FFI

_DLL = None


def load_assimp(dll_path):
    global _DLL
    if _DLL is None:
        dll = ctypes.CDLL(str(dll_path))
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


# ------------------------------------------------------------------ grouping

LOD_TOKEN = re.compile(r'_LOD(\d+)(?=$|[_\-.])', re.IGNORECASE)
EXPLICIT_PART = re.compile(
    r'^(?P<prefix>.+?)[_\s-](?:part|piece|mesh|polysurface)[_\s-]*(?:\d+)$',
    re.IGNORECASE,
)
NUMERIC_TOKEN = re.compile(r'^\d+$')
TRAILING_INDEX_TOKEN = re.compile(r'^[A-Za-z]*\d+$')


def normalize_prefix(stem):
    tokens = [token for token in re.split(r'[_\s]+', stem) if token]
    while len(tokens) > 1 and (
        NUMERIC_TOKEN.match(tokens[-1]) or TRAILING_INDEX_TOKEN.match(tokens[-1])
    ):
        tokens.pop()
    return '_'.join(tokens) if tokens else stem


def retain_best_lod(files):
    def level(path):
        match = LOD_TOKEN.search(path.stem)
        return int(match.group(1)) if match else 0

    best = min(level(path) for path in files)
    return [path for path in files if level(path) == best]


def explicit_prefix(stem):
    match = EXPLICIT_PART.match(LOD_TOKEN.sub('', stem))
    return match.group('prefix') if match else None


def group_directory(directory, mode, min_files):
    files = sorted(
        path for path in directory.glob('*.gltf')
        if not path.stem.endswith('__merged') and '.sanitized.' not in path.name
    )
    if mode == 'dir':
        members = retain_best_lod(files) if files else []
        return [(directory.name, members)] if len(members) >= min_files else []

    clusters = {}
    for path in files:
        name = explicit_prefix(path.stem) if mode == 'explicit' else normalize_prefix(path.stem)
        if name:
            clusters.setdefault(name, []).append(path)
    groups = []
    for name, members in sorted(clusters.items()):
        members = retain_best_lod(members)
        if len(members) >= min_files:
            groups.append((name, members))
    return groups


# ------------------------------------------------------------------- merging

def load_document(path):
    document = json.loads(path.read_text(encoding='utf-8'))
    if document.get('extensionsUsed') or document.get('extensionsRequired'):
        raise ValueError(f'{path.name}: glTF extensions are not supported by the safe merger')
    buffers = []
    for buffer in document.get('buffers', []):
        uri = buffer.get('uri')
        if uri is None or uri.startswith('data:'):
            raise ValueError(f'{path.name}: embedded buffers are not supported')
        if '://' in uri:
            raise ValueError(f'{path.name}: remote buffers are not supported')
        buffer_path = (path.parent / uri).resolve()
        if not buffer_path.is_relative_to(path.parent.resolve()):
            raise ValueError(f'{path.name}: buffer path escapes the source directory')
        buffers.append(buffer_path.read_bytes())
    return document, buffers


def merge_documents(name, sources):
    """Merge (document, buffers) pairs into one glTF document + binary blob.

    All source buffer data is concatenated into a single binary blob exposed as
    one glTF buffer; bufferView offsets are rebased onto that single buffer.
    """
    merged = {
        'asset': {'version': '2.0', 'generator': 'PakAssetStudio merge_gltf'},
        'scene': 0,
        'scenes': [{'nodes': [0]}],
        'nodes': [{'name': name, 'children': []}],
        'buffers': [],
    }
    blob = bytearray()

    def append_list(key):
        return merged.setdefault(key, [])

    for source_name, (document, buffers) in sources:
        base = {
            key: len(merged.get(key, []))
            for key in ('accessors', 'bufferViews', 'images', 'materials',
                        'meshes', 'nodes', 'samplers', 'skins', 'textures')
        }
        # append each source buffer's bytes, 4-byte aligned, remembering where
        # each one landed in the merged blob
        buffer_bases = []
        for data in buffers:
            while len(blob) % 4:
                blob.append(0)
            buffer_bases.append(len(blob))
            blob.extend(data)

        for view in document.get('bufferViews', []):
            view = dict(view)
            view['byteOffset'] = view.get('byteOffset', 0) + buffer_bases[view.get('buffer', 0)]
            view['buffer'] = 0
            append_list('bufferViews').append(view)

        for accessor in document.get('accessors', []):
            accessor = dict(accessor)
            if 'bufferView' in accessor:
                accessor['bufferView'] += base['bufferViews']
            sparse = accessor.get('sparse')
            if sparse:
                sparse = {k: dict(v) if isinstance(v, dict) else v for k, v in sparse.items()}
                sparse['indices'] = dict(sparse['indices'])
                sparse['values'] = dict(sparse['values'])
                sparse['indices']['bufferView'] += base['bufferViews']
                sparse['values']['bufferView'] += base['bufferViews']
                accessor['sparse'] = sparse
            append_list('accessors').append(accessor)

        for image in document.get('images', []):
            image = dict(image)
            if 'bufferView' in image:
                image['bufferView'] += base['bufferViews']
            append_list('images').append(image)
        append_list('samplers').extend(document.get('samplers', []))

        for texture in document.get('textures', []):
            texture = dict(texture)
            if 'sampler' in texture:
                texture['sampler'] += base['samplers']
            if 'source' in texture:
                texture['source'] += base['images']
            append_list('textures').append(texture)

        for material in document.get('materials', []):
            material = json.loads(json.dumps(material))  # deep copy
            def shift_texture(ref):
                if isinstance(ref, dict) and 'index' in ref:
                    ref['index'] += base['textures']
            for key in ('baseColorTexture', 'metallicRoughnessTexture'):
                pbr = material.get('pbrMetallicRoughness', {})
                if key in pbr:
                    shift_texture(pbr[key])
            for key in ('normalTexture', 'occlusionTexture', 'emissiveTexture'):
                if key in material:
                    shift_texture(material[key])
            for extension in material.get('extensions', {}).values():
                for ref in extension.values():
                    shift_texture(ref)
            append_list('materials').append(material)

        for mesh in document.get('meshes', []):
            mesh = json.loads(json.dumps(mesh))
            for primitive in mesh.get('primitives', []):
                if 'indices' in primitive:
                    primitive['indices'] += base['accessors']
                primitive['attributes'] = {
                    k: v + base['accessors'] for k, v in primitive.get('attributes', {}).items()
                }
                if 'material' in primitive:
                    primitive['material'] += base['materials']
                for target in primitive.get('targets', []):
                    for key in target:
                        target[key] += base['accessors']
            append_list('meshes').append(mesh)

        for node in document.get('nodes', []):
            node = dict(node)
            if 'mesh' in node:
                node['mesh'] += base['meshes']
            if 'skin' in node:
                node['skin'] += base['skins']
            if 'children' in node:
                node['children'] = [c + base['nodes'] for c in node['children']]
            append_list('nodes').append(node)

        for skin in document.get('skins', []):
            skin = dict(skin)
            skin['joints'] = [j + base['nodes'] for j in skin.get('joints', [])]
            if 'skeleton' in skin:
                skin['skeleton'] += base['nodes']
            if 'inverseBindMatrices' in skin:
                skin['inverseBindMatrices'] += base['accessors']
            append_list('skins').append(skin)

        # animations are dropped: the pipeline exports with -noanim

        scene = document.get('scenes', [{}])[document.get('scene', 0)]
        roots = [r + base['nodes'] for r in scene.get('nodes', [])]
        wrapper = {'name': source_name}
        if roots:
            wrapper['children'] = roots
        merged['nodes'].append(wrapper)
        merged['nodes'][0]['children'].append(len(merged['nodes']) - 1)

    if blob:
        merged['buffers'] = [{'byteLength': len(blob), 'uri': name + '.bin'}]
    else:
        merged.pop('buffers')

    for key in list(merged.keys()):
        if isinstance(merged[key], list) and not merged[key]:
            merged.pop(key)
    return merged, bytes(blob)


def delete_group_sources(files):
    group_root = files[0].parent.resolve()
    selected = {
        path for path in (source.resolve() for source in files)
        if path.parent == group_root
    }
    references = {}
    references_complete = True
    for gltf in files[0].parent.glob('*.gltf'):
        try:
            document = json.loads(gltf.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            references_complete = False
            continue
        for buffer in document.get('buffers', []):
            uri = buffer.get('uri')
            if not uri or uri.startswith('data:') or '://' in uri:
                continue
            path = (gltf.parent / uri).resolve()
            if path.is_relative_to(group_root):
                references.setdefault(path, set()).add(gltf.resolve())
    for source in selected:
        if source.exists():
            source.unlink()
    if references_complete:
        for buffer, owners in references.items():
            if owners and owners.issubset(selected) and buffer.exists():
                buffer.unlink()


def merge_group(name, files, output_dir, overwrite=False):
    output_dir = Path(output_dir).resolve()
    merged_name = name + '__merged'
    out_gltf = output_dir / (merged_name + '.gltf')
    if out_gltf.exists() and not overwrite:
        return out_gltf, 'skipped-existing'

    sources = [(f.stem, load_document(f)) for f in files]
    merged, blob = merge_documents(merged_name, sources)
    previous_buffers = set()
    if out_gltf.exists():
        try:
            previous = json.loads(out_gltf.read_text(encoding='utf-8'))
            for buffer in previous.get('buffers', []):
                uri = buffer.get('uri')
                if uri and not uri.startswith('data:') and '://' not in uri:
                    previous_buffers.add((output_dir / uri).resolve())
        except (OSError, ValueError):
            pass

    output_dir.mkdir(parents=True, exist_ok=True)
    temporary_token = f'{os.getpid()}-{id(files)}'
    out_bin = None
    temporary_bin = None
    if blob:
        digest = hashlib.sha256(blob).hexdigest()[:16]
        out_bin = output_dir / f'{merged_name}.{digest}.bin'
        merged['buffers'][0]['uri'] = out_bin.name
        temporary_bin = output_dir / f'.{out_bin.name}.{temporary_token}.tmp'
    temporary_gltf = output_dir / f'.{out_gltf.name}.{temporary_token}.tmp'
    try:
        if out_bin is not None and temporary_bin is not None:
            temporary_bin.write_bytes(blob)
            os.replace(temporary_bin, out_bin)
        temporary_gltf.write_text(
            json.dumps(merged, separators=(',', ':')), encoding='utf-8'
        )
        os.replace(temporary_gltf, out_gltf)
    finally:
        if temporary_gltf.exists():
            temporary_gltf.unlink()
        if temporary_bin is not None and temporary_bin.exists():
            temporary_bin.unlink()

    for previous_buffer in previous_buffers:
        managed_previous_buffer = (
            previous_buffer != out_bin
            and previous_buffer.parent == output_dir.resolve()
            and re.fullmatch(
                re.escape(merged_name) + r'\.[0-9a-f]{16}\.bin',
                previous_buffer.name,
                re.IGNORECASE,
            ) is not None
            and previous_buffer.exists()
        )
        if managed_previous_buffer:
            previous_buffer.unlink()

    return out_gltf, 'merged'


# -------------------------------------------------------------- FBX output

def convert_to_fbx(dll, gltf_path, overwrite=False):
    output = gltf_path.with_suffix('.fbx')
    temporary = gltf_path.with_name(f'.{gltf_path.stem}.{os.getpid()}.fbx.tmp')
    if output.exists() and not overwrite:
        return output, 'skipped-existing', False
    try:
        scene = dll.aiImportFile(os.fsencode(gltf_path), 0)
        if not scene:
            return output, 'import failed: ' + error_string(dll), False
        try:
            result = dll.aiExportScene(scene, b'fbx', os.fsencode(temporary), 0)
            if result != 0:
                return output, 'export failed: ' + error_string(dll), False
        finally:
            dll.aiReleaseImport(scene)
        verification = dll.aiImportFile(os.fsencode(temporary), 0)
        if not verification:
            return output, 'verification failed: ' + error_string(dll), False
        dll.aiReleaseImport(verification)
        os.replace(temporary, output)
        return output, 'ok', True
    finally:
        if temporary.exists():
            temporary.unlink()


# --------------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('root', type=Path, help='directory tree containing glTF fragments')
    parser.add_argument('--mode', choices=('explicit', 'prefix', 'dir'), default='explicit')
    parser.add_argument('--min-files', type=int, default=2)
    parser.add_argument('--dll', type=Path, help='Assimp DLL for FBX conversion')
    source_group = parser.add_mutually_exclusive_group()
    source_group.add_argument('--keep-sources', dest='keep_sources', action='store_true',
                              help='keep source glTF/bin fragments (default)')
    source_group.add_argument('--delete-sources', dest='keep_sources', action='store_false',
                              help='delete fragments only after Assimp verifies the result; requires --dll')
    parser.set_defaults(keep_sources=True)
    parser.add_argument('--overwrite', action='store_true',
                        help='replace an existing __merged output')
    parser.add_argument('--keep-gltf', action='store_true',
                        help='keep the merged glTF/bin next to the FBX')
    args = parser.parse_args()
    if not args.keep_sources and not args.dll:
        parser.error('--delete-sources requires --dll so the result can be verified first')

    root = args.root.resolve()
    directories = sorted({p.parent for p in root.rglob('*.gltf')})
    if not directories:
        print('No glTF files found.')
        return

    dll = load_assimp(args.dll.resolve()) if args.dll else None
    merged_count = 0
    conversion_failures = 0
    for directory in directories:
        groups = group_directory(directory, args.mode, args.min_files)
        for name, files in groups:
            out_gltf, merge_status = merge_group(
                name, files, directory, args.overwrite
            )
            detail = f'{len(files)} parts -> {out_gltf.name} [{merge_status}]'
            if dll:
                output, status, converted = convert_to_fbx(dll, out_gltf, args.overwrite)
                detail += f' -> {output.name} [{status}]'
                if not converted:
                    conversion_failures += 1
                if converted and merge_status == 'merged' and not args.keep_sources:
                    delete_group_sources(files)
                if not args.keep_gltf and converted:
                    buffers = load_document(out_gltf)[0].get('buffers', [])
                    out_gltf.unlink()
                    for buffer in buffers:
                        uri = buffer.get('uri')
                        if uri and not uri.startswith('data:') and '://' not in uri:
                            buffer_path = (out_gltf.parent / uri).resolve()
                            if buffer_path.is_relative_to(root) and buffer_path.exists():
                                buffer_path.unlink()
            print(detail, flush=True)
            if merge_status == 'merged':
                merged_count += 1
    print(f'Done: {merged_count} merged models.')
    if conversion_failures:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
