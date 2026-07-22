"""Verify whether split mesh parts keep world-space positions baked into geometry.

Imports every FBX in a directory with Assimp, re-exports each to a temporary
glTF, then computes the world-space AABB from accessor min/max and node
transforms. If part centers are spread out (instead of stacked at the origin),
merging the parts by simple concatenation will reassemble the model correctly.
"""

import argparse
import ctypes
import json
import math
import os
import shutil
import tempfile
from pathlib import Path


def load_assimp(dll_path):
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
    return dll


def error_string(dll):
    value = dll.aiGetErrorString()
    return value.decode('utf-8', errors='replace') if value else 'unknown'


def trs_to_matrix(node):
    if 'matrix' in node:
        m = node['matrix']  # column-major
        return [[m[0], m[4], m[8], m[12]],
                [m[1], m[5], m[9], m[13]],
                [m[2], m[6], m[10], m[14]],
                [m[3], m[7], m[11], m[15]]]
    t = node.get('translation', [0.0, 0.0, 0.0])
    q = node.get('rotation', [0.0, 0.0, 0.0, 1.0])
    s = node.get('scale', [1.0, 1.0, 1.0])
    x, y, z, w = q
    rot = [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ]
    return [
        [rot[0][0] * s[0], rot[0][1] * s[1], rot[0][2] * s[2], t[0]],
        [rot[1][0] * s[0], rot[1][1] * s[1], rot[1][2] * s[2], t[1]],
        [rot[2][0] * s[0], rot[2][1] * s[1], rot[2][2] * s[2], t[2]],
        [0.0, 0.0, 0.0, 1.0],
    ]


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def mat_point(m, p):
    return [sum(m[i][k] * (p[k] if k < 3 else 1.0) for k in range(4)) for i in range(3)]


IDENTITY = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]


def scene_aabb(document):
    lo = [math.inf] * 3
    hi = [-math.inf] * 3
    nodes = document.get('nodes', [])
    meshes = document.get('meshes', [])
    accessors = document.get('accessors', [])

    def visit(index, parent):
        node = nodes[index]
        world = mat_mul(parent, trs_to_matrix(node))
        if 'mesh' in node:
            for primitive in meshes[node['mesh']].get('primitives', []):
                position = primitive.get('attributes', {}).get('POSITION')
                if position is None:
                    continue
                accessor = accessors[position]
                amin = accessor.get('min')
                amax = accessor.get('max')
                if not amin or not amax:
                    continue
                for corner in range(8):
                    point = [amin[0] if corner & 1 else amax[0],
                             amin[1] if corner & 2 else amax[1],
                             amin[2] if corner & 4 else amax[2]]
                    wp = mat_point(world, point)
                    for axis in range(3):
                        lo[axis] = min(lo[axis], wp[axis])
                        hi[axis] = max(hi[axis], wp[axis])
        for child in node.get('children', []):
            visit(child, world)

    scene = document.get('scenes', [{}])[document.get('scene', 0)]
    for root in scene.get('nodes', []):
        visit(root, IDENTITY)
    return lo, hi


def measure(dll, fbx, workspace):
    gltf_path = workspace / (fbx.stem + '.gltf')
    scene = dll.aiImportFile(os.fsencode(fbx), 0)
    if not scene:
        return None, 'import: ' + error_string(dll)
    try:
        result = dll.aiExportScene(scene, b'gltf2', os.fsencode(gltf_path), 0)
        if result != 0:
            return None, 'export: ' + error_string(dll)
    finally:
        dll.aiReleaseImport(scene)
    document = json.loads(gltf_path.read_text(encoding='utf-8'))
    lo, hi = scene_aabb(document)
    if math.isinf(lo[0]):
        return None, 'no geometry'
    return (lo, hi), ''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=Path)
    parser.add_argument('--dll', type=Path, required=True)
    args = parser.parse_args()

    dll = load_assimp(args.dll.resolve())
    files = sorted(args.directory.glob('*.fbx'))
    print(f'{len(files)} FBX files in {args.directory}')

    results = []
    workspace = Path(tempfile.mkdtemp(prefix='bounds-'))
    try:
        for fbx in files:
            box, error = measure(dll, fbx, workspace)
            if box is None:
                print(f'  SKIP {fbx.name}: {error}')
                continue
            lo, hi = box
            center = [(lo[i] + hi[i]) / 2 for i in range(3)]
            size = [hi[i] - lo[i] for i in range(3)]
            results.append((fbx.name, center, size))
            print(f'  {fbx.name}: center=({center[0]:9.2f},{center[1]:9.2f},{center[2]:9.2f}) '
                  f'size=({size[0]:8.2f},{size[1]:8.2f},{size[2]:8.2f})')
    finally:
        shutil.rmtree(workspace, ignore_errors=True)

    if not results:
        print('No measurable files.')
        return

    centers = [r[1] for r in results]
    sizes = [r[2] for r in results]
    spread = [max(c[i] for c in centers) - min(c[i] for c in centers) for i in range(3)]
    typical_size = sorted(max(s) for s in sizes)[len(sizes) // 2]
    max_spread = max(spread)
    print()
    print(f'Center spread: ({spread[0]:.2f}, {spread[1]:.2f}, {spread[2]:.2f})')
    print(f'Median part size (max axis): {typical_size:.2f}')
    verdict = 'POSITIONS BAKED - simple merge will reassemble the model' \
        if max_spread > typical_size * 0.5 else \
        'CENTERS CLUSTERED - parts likely pivot-centered, merged result needs manual placement'
    print(f'Verdict: {verdict}')


if __name__ == '__main__':
    main()
