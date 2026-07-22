"""Merge fragmented glTF exports (one mesh per file) into a single glTF/FBX per model.

UModel exports every cooked mesh uasset as an individual glTF, so a model that
was assembled from many parts ends up as dozens of fragments. This tool merges
the fragments back together, grouping files either by directory (default) or by
smart filename prefixes, and optionally converts the merged glTF to FBX with
Assimp.

Grouping modes:
  dir    - every directory that contains glTF files becomes one merged model
           named after the directory. Best for per-model directories.
  prefix - within each directory, cluster files by their normalized name
           prefix (trailing numeric tokens like `_01`, `_polySurface123` are
           stripped). Singletons are left untouched. Best for mixed folders.

Files whose name contains _LOD1.._LOD9 are skipped when the group also has
_LOD0 or LOD-less files, so merged models keep only the highest detail level.
"""

import argparse
import ctypes
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


def scene_mesh_count(scene_ptr):
    # aiScene layout (64-bit): mFlags(4) + pad(4) + mRootNode(8) + mNumMeshes(4)
    return ctypes.c_uint.from_address(scene_ptr + 16).value


# ------------------------------------------------------------------ grouping

LOD_HIGH = re.compile(r'_LOD([1-9])\b', re.IGNORECASE)
NUMERIC_TOKEN = re.compile(r'^\d+$')
TRAILING_INDEX_TOKEN = re.compile(r'^[A-Za-z]*\d+$')


def normalize_prefix(stem):
    tokens = [token for token in re.split(r'[_\s]+', stem) if token]
    while len(tokens) > 1 and (
        NUMERIC_TOKEN.match(tokens[-1]) or TRAILING_INDEX_TOKEN.match(tokens[-1])
    ):
        tokens.pop()
    return '_'.join(tokens) if tokens else stem


def group_directory(directory, mode, min_files):
    files = sorted(directory.glob('*.gltf'))
    lod0_or_plain = [f for f in files if not LOD_HIGH.search(f.stem)]
    if lod0_or_plain:
        files = lod0_or_plain  # drop higher LODs when a better source exists
    if len(files) < min_files:
        return []
    if mode == 'dir':
        return [(directory.name, files)]

    clusters = {}
    for path in files:
        clusters.setdefault(normalize_prefix(path.stem), []).append(path)
    return [
        (name, members) for name, members in sorted(clusters.items())
        if len(members) >= min_files
    ]


# ------------------------------------------------------------------- merging

def load_document(path):
    document = json.loads(path.read_text(encoding='utf-8'))
    buffers = []
    for buffer in document.get('buffers', []):
        uri = buffer.get('uri')
        if uri is None:
            raise ValueError(f'{path.name}: embedded GLB-style buffer is not supported')
        buffers.append((path.parent / uri).read_bytes())
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


def merge_group(name, files, output_dir, keep_sources):
    sources = [(f.stem, load_document(f)) for f in files]
    merged, blob = merge_documents(name, sources)
    out_gltf = output_dir / (name + '.gltf')
    if not keep_sources:
        # Delete sources before writing: the output may share its name with a
        # source fragment (dir mode names the output after the directory).
        for f in files:
            f.unlink()
            bin_name = f.with_suffix('.bin')
            if bin_name.exists():
                bin_name.unlink()
    out_gltf.write_text(json.dumps(merged, separators=(',', ':')), encoding='utf-8')
    if blob:
        (output_dir / (name + '.bin')).write_bytes(blob)
    return out_gltf


# -------------------------------------------------------------- FBX output

def convert_to_fbx(dll, gltf_path, expected_meshes):
    output = gltf_path.with_suffix('.fbx')
    scene = dll.aiImportFile(os.fsencode(gltf_path), 0)
    if not scene:
        return output, 'import failed: ' + error_string(dll)
    try:
        result = dll.aiExportScene(scene, b'fbx', os.fsencode(output), 0)
        if result != 0:
            return output, 'export failed: ' + error_string(dll)
    finally:
        dll.aiReleaseImport(scene)
    verification = dll.aiImportFile(os.fsencode(output), 0)
    if not verification:
        return output, ('warning: FBX written but Assimp cannot read it back '
                        '(known Assimp FBX limitation; the file usually still opens in DCC tools)')
    meshes = scene_mesh_count(verification)
    dll.aiReleaseImport(verification)
    if meshes < expected_meshes:
        return output, (f'warning: Assimp reads back only {meshes}/{expected_meshes} meshes '
                        '(known Assimp FBX limitation; the file usually still opens in DCC tools)')
    return output, f'ok ({meshes} meshes)'


# --------------------------------------------------------------------- main

def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('root', type=Path, help='directory tree containing glTF fragments')
    parser.add_argument('--mode', choices=('dir', 'prefix'), default='dir')
    parser.add_argument('--min-files', type=int, default=2)
    parser.add_argument('--dll', type=Path, help='Assimp DLL for FBX conversion')
    parser.add_argument('--keep-sources', action='store_true',
                        help='keep source glTF/bin fragments after merging')
    parser.add_argument('--keep-gltf', action='store_true',
                        help='keep the merged glTF/bin next to the FBX')
    args = parser.parse_args()

    root = args.root.resolve()
    directories = sorted({p.parent for p in root.rglob('*.gltf')})
    if not directories:
        print('No glTF files found.')
        return

    dll = load_assimp(args.dll.resolve()) if args.dll else None
    merged_count = 0
    for directory in directories:
        groups = group_directory(directory, args.mode, args.min_files)
        for name, files in groups:
            out_gltf = merge_group(name, files, directory, args.keep_sources)
            detail = f'{len(files)} parts -> {out_gltf.name}'
            if dll:
                expected = len(json.loads(out_gltf.read_text(encoding='utf-8')).get('meshes', []))
                output, status = convert_to_fbx(dll, out_gltf, expected)
                detail += f' -> {output.name} [{status}]'
                if not args.keep_gltf and output.exists():
                    out_gltf.unlink()
                    bin_file = out_gltf.with_suffix('.bin')
                    if bin_file.exists():
                        bin_file.unlink()
            print(detail, flush=True)
            merged_count += 1
    print(f'Done: {merged_count} merged models.')


if __name__ == '__main__':
    main()
