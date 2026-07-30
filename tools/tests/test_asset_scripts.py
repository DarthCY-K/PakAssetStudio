import json
import os
import re
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))

import convert_gltf_to_fbx as converter
import merge_gltf as merger


class LocalizationResourceTests(unittest.TestCase):
    @staticmethod
    def load_without_duplicate_keys(path):
        def reject_duplicates(pairs):
            result = {}
            for key, value in pairs:
                if key in result:
                    raise ValueError(f'duplicate JSON key in {path}: {key}')
                result[key] = value
            return result

        return json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=reject_duplicates)

    def test_shipped_languages_have_identical_key_sets(self):
        language_root = TOOLS.parent / 'PakAssetStudio' / 'Languages'
        chinese = self.load_without_duplicate_keys(language_root / 'zh-CN.json')
        english = self.load_without_duplicate_keys(language_root / 'en-US.json')

        keys = set(chinese['strings'])
        self.assertEqual(keys, set(english['strings']))
        for key in keys:
            chinese_args = set(re.findall(r'\{(\d+)(?::[^}]*)?\}', chinese['strings'][key]))
            english_args = set(re.findall(r'\{(\d+)(?::[^}]*)?\}', english['strings'][key]))
            self.assertEqual(chinese_args, english_args, f'placeholder mismatch: {key}')

        referenced = set()
        source_root = TOOLS.parent / 'PakAssetStudio'
        for path in list(source_root.rglob('*.cs')) + list(source_root.rglob('*.xaml')):
            text = path.read_text(encoding='utf-8-sig')
            referenced.update(re.findall(r'(?:Text|TextFormat)\(\"([A-Za-z0-9_]+)\"', text))
            referenced.update(re.findall(r'\{l:Loc\s+([A-Za-z0-9_]+)\}', text))
        self.assertFalse(referenced - keys, f'missing localization keys: {referenced - keys}')


class MergeTests(unittest.TestCase):
    def test_delete_sources_requires_assimp_validation(self):
        with tempfile.TemporaryDirectory() as value:
            with patch.object(sys, 'argv', ['merge_gltf.py', value, '--delete-sources']):
                with self.assertRaises(SystemExit) as raised:
                    merger.main()

        self.assertEqual(2, raised.exception.code)

    def test_explicit_mode_never_groups_unrelated_models(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            for name in (
                'Vehicle_part01.gltf',
                'Vehicle_part02.gltf',
                'Chair01.gltf',
                'Chair02.gltf',
            ):
                (root / name).write_text('{}', encoding='utf-8')

            groups = merger.group_directory(root, 'explicit', 2)

            self.assertEqual(1, len(groups))
            self.assertEqual('Vehicle', groups[0][0])
            self.assertEqual(
                ['Vehicle_part01.gltf', 'Vehicle_part02.gltf'],
                [path.name for path in groups[0][1]],
            )

    def test_lod_filtering_is_scoped_to_each_explicit_group(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            for name in (
                'A_part01_LOD0.gltf',
                'A_part02_LOD10.gltf',
                'B_part01_LOD1.gltf',
                'B_part02_LOD1.gltf',
                'C_part01_LOD1.gltf',
                'C_part02_LOD2.gltf',
            ):
                (root / name).write_text('{}', encoding='utf-8')

            groups = dict(merger.group_directory(root, 'explicit', 1))

            self.assertEqual(['A_part01_LOD0.gltf'], [path.name for path in groups['A']])
            self.assertEqual(2, len(groups['B']))
            self.assertEqual(['C_part01_LOD1.gltf'], [path.name for path in groups['C']])

    def test_delete_sources_never_deletes_a_buffer_outside_the_group_directory(self):
        with tempfile.TemporaryDirectory() as value:
            parent = Path(value)
            root = parent / 'Group'
            root.mkdir()
            outside = parent / 'outside.bin'
            outside.write_bytes(b'keep')
            sources = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'buffers': [{'uri': '../outside.bin', 'byteLength': 4}],
                }), encoding='utf-8')
                sources.append(source)

            merger.delete_group_sources(sources)

            self.assertFalse(any(source.exists() for source in sources))
            self.assertEqual(b'keep', outside.read_bytes())

    def test_delete_sources_keeps_buffers_when_other_gltf_is_unreadable(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            shared = root / 'Shared.bin'
            shared.write_bytes(b'keep')
            sources = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'buffers': [{'uri': shared.name, 'byteLength': 4}],
                }), encoding='utf-8')
                sources.append(source)
            (root / 'Unreadable.gltf').write_text('{', encoding='utf-8')

            merger.delete_group_sources(sources)

            self.assertTrue(shared.exists())

    def test_unsupported_gltf_extensions_fail_without_deleting_sources(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            sources = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'extensionsUsed': ['KHR_draco_mesh_compression'],
                }), encoding='utf-8')
                sources.append(source)

            with self.assertRaises(ValueError):
                merger.merge_group('Model', sources, root)

            self.assertTrue(all(source.exists() for source in sources))
            self.assertFalse((root / 'Model__merged.gltf').exists())

    def test_overwrite_does_not_delete_non_managed_previous_buffer(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            unrelated = root / 'Model__merged.user.bin'
            unrelated.write_bytes(b'keep')
            (root / 'Model__merged.gltf').write_text(json.dumps({
                'asset': {'version': '2.0'},
                'buffers': [{'uri': unrelated.name, 'byteLength': 4}],
            }), encoding='utf-8')
            sources = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'scene': 0,
                    'scenes': [{'nodes': []}],
                }), encoding='utf-8')
                sources.append(source)

            merger.merge_group('Model', sources, root, overwrite=True)

            self.assertEqual(b'keep', unrelated.read_bytes())

    def test_failed_commit_preserves_existing_merged_document_and_sources(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            old_output = root / 'Model__merged.gltf'
            old_output.write_text('{"asset":{"version":"2.0"}}', encoding='utf-8')
            sources = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'scene': 0,
                    'scenes': [{'nodes': []}],
                }), encoding='utf-8')
                sources.append(source)

            real_replace = os.replace

            def fail_document_replace(source, destination):
                if Path(destination).suffix == '.gltf':
                    raise OSError('simulated commit failure')
                return real_replace(source, destination)

            with patch.object(merger.os, 'replace', side_effect=fail_document_replace):
                with self.assertRaises(OSError):
                    merger.merge_group('Model', sources, root, overwrite=True)

            self.assertEqual('{"asset":{"version":"2.0"}}', old_output.read_text(encoding='utf-8'))
            self.assertTrue(all(source.exists() for source in sources))
            self.assertFalse(any(root.glob('*.tmp')))

    def test_merge_preserves_sources_and_uses_separate_atomic_output(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            sources = []
            source_buffers = []
            for index in (1, 2):
                source = root / f'Model_part{index:02}.gltf'
                source_buffer = source.with_suffix('.bin')
                source_buffer.write_bytes(bytes([index]) * 4)
                source.write_text(json.dumps({
                    'asset': {'version': '2.0'},
                    'scene': 0,
                    'scenes': [{'nodes': []}],
                    'buffers': [{'uri': source_buffer.name, 'byteLength': 4}],
                }), encoding='utf-8')
                sources.append(source)
                source_buffers.append(source_buffer)

            output, status = merger.merge_group('Model', sources, root)

            merged = json.loads(output.read_text(encoding='utf-8'))
            merged_buffer = root / merged['buffers'][0]['uri']
            self.assertEqual('merged', status)
            self.assertEqual('Model__merged.gltf', output.name)
            self.assertTrue(merged_buffer.exists())
            self.assertIn('Model__merged.', merged_buffer.name)
            self.assertTrue(all(source.exists() for source in sources))
            self.assertTrue(all(buffer.exists() for buffer in source_buffers))
            self.assertFalse(any(root.glob('*.tmp')))


class FakeVerificationDll:
    def aiImportFile(self, path, flags):
        return 1

    def aiReleaseImport(self, scene):
        return None


class FakeImportFailureDll:
    def aiImportFile(self, path, flags):
        return 0

    def aiGetErrorString(self):
        return b'import failed'


class FakeVerificationFailureDll:
    def __init__(self):
        self.imports = 0

    def aiImportFile(self, path, flags):
        self.imports += 1
        return 1 if self.imports == 1 else 0

    def aiExportScene(self, scene, format_id, path, flags):
        Path(os.fsdecode(path)).write_bytes(b'unverified-new-output')
        return 0

    def aiReleaseImport(self, scene):
        return None

    def aiGetErrorString(self):
        return b'verification failed'


class ConversionTests(unittest.TestCase):
    def test_stale_sanitized_input_prevents_false_success(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            (root / '.Model.123.sanitized.gltf').write_text('{}', encoding='utf-8')
            with patch.object(
                sys, 'argv', ['convert_gltf_to_fbx.py', value, '--dll', 'unused.dll']
            ):
                with self.assertRaises(SystemExit) as raised:
                    converter.main()

        self.assertEqual(2, raised.exception.code)

    def test_real_assimp_converts_and_reimports_minimal_triangle(self):
        dll_value = os.environ.get('ASSIMP_TEST_DLL')
        if not dll_value:
            self.skipTest('ASSIMP_TEST_DLL is not configured')
        dll_path = Path(dll_value).resolve()
        self.assertTrue(dll_path.is_file(), f'Assimp DLL not found: {dll_path}')

        with tempfile.TemporaryDirectory() as value:
            root = Path(value) / '模型 Test'
            root.mkdir()
            source = root / 'Triangle.gltf'
            buffer = root / 'Triangle.bin'
            buffer.write_bytes(struct.pack(
                '<9f3H',
                0.0, 0.0, 0.0,
                1.0, 0.0, 0.0,
                0.0, 1.0, 0.0,
                0, 1, 2,
            ))
            source.write_text(json.dumps({
                'asset': {'version': '2.0'},
                'scene': 0,
                'scenes': [{'nodes': [0]}],
                'nodes': [{'mesh': 0}],
                'meshes': [{'primitives': [{
                    'attributes': {'POSITION': 0},
                    'indices': 1,
                    'mode': 4,
                }]}],
                'buffers': [{'uri': buffer.name, 'byteLength': 42}],
                'bufferViews': [
                    {'buffer': 0, 'byteOffset': 0, 'byteLength': 36, 'target': 34962},
                    {'buffer': 0, 'byteOffset': 36, 'byteLength': 6, 'target': 34963},
                ],
                'accessors': [
                    {
                        'bufferView': 0, 'componentType': 5126, 'count': 3,
                        'type': 'VEC3', 'min': [0, 0, 0], 'max': [1, 1, 0],
                    },
                    {'bufferView': 1, 'componentType': 5123, 'count': 3, 'type': 'SCALAR'},
                ],
            }), encoding='utf-8')

            with patch.object(sys, 'argv', [
                'convert_gltf_to_fbx.py', str(root), '--dll', str(dll_path),
                '--workers', '1', '--overwrite',
            ]):
                converter.main()

            output = source.with_suffix('.fbx')
            self.assertTrue(output.is_file())
            self.assertIn('Triangle.fbx', (root / 'model-files.txt').read_text(encoding='utf-8-sig'))
            dll = converter.load_assimp(str(dll_path))
            self.assertTrue(converter.is_valid_fbx(dll, output))

    def test_existing_fbx_without_overwrite_retains_new_gltf(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            source = root / 'Model.gltf'
            output = root / 'Model.fbx'
            source.write_text('{"asset":{"version":"2.0"}}', encoding='utf-8')
            output.write_bytes(b'existing')

            with patch.object(converter, 'load_assimp', return_value=FakeVerificationDll()):
                _, status, _ = converter.convert_one((str(source), 'unused.dll', False))

            self.assertEqual('skipped-existing', status)
            self.assertTrue(source.exists())
            self.assertEqual(b'existing', output.read_bytes())

    def test_failed_overwrite_preserves_previous_fbx_and_source(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            source = root / 'Model.gltf'
            output = root / 'Model.fbx'
            source.write_text('{"asset":{"version":"2.0"}}', encoding='utf-8')
            output.write_bytes(b'previous-valid-output')

            with patch.object(converter, 'load_assimp', return_value=FakeImportFailureDll()):
                _, status, _ = converter.convert_one((str(source), 'unused.dll', True))

            self.assertEqual('failed', status)
            self.assertTrue(source.exists())
            self.assertEqual(b'previous-valid-output', output.read_bytes())

    def test_failed_verification_preserves_previous_fbx_and_removes_temporary(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value)
            source = root / 'Model.gltf'
            output = root / 'Model.fbx'
            source.write_text('{"asset":{"version":"2.0"}}', encoding='utf-8')
            output.write_bytes(b'previous-valid-output')

            with patch.object(converter, 'load_assimp', return_value=FakeVerificationFailureDll()):
                _, status, _ = converter.convert_one((str(source), 'unused.dll', True))

            self.assertEqual('failed', status)
            self.assertTrue(source.exists())
            self.assertEqual(b'previous-valid-output', output.read_bytes())
            self.assertFalse(any(root.glob('*.tmp')))

    def test_source_cleanup_keeps_buffers_needed_by_failed_gltf(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value).resolve()
            shared = root / 'Shared.bin'
            shared.write_bytes(b'data')
            first = root / 'First.gltf'
            second = root / 'Second.gltf'
            document = {
                'asset': {'version': '2.0'},
                'buffers': [{'uri': 'Shared.bin', 'byteLength': 4}],
            }
            first.write_text(json.dumps(document), encoding='utf-8')
            second.write_text(json.dumps(document), encoding='utf-8')

            converter.remove_converted_sources([first, second], [str(first)], root)

            self.assertFalse(first.exists())
            self.assertTrue(second.exists())
            self.assertTrue(shared.exists())

    def test_source_cleanup_keeps_buffers_when_any_gltf_is_unreadable(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value).resolve()
            shared = root / 'Shared.bin'
            shared.write_bytes(b'data')
            converted = root / 'Converted.gltf'
            converted.write_text(json.dumps({
                'asset': {'version': '2.0'},
                'buffers': [{'uri': shared.name, 'byteLength': 4}],
            }), encoding='utf-8')
            unreadable = root / 'Unreadable.gltf'
            unreadable.write_text('{', encoding='utf-8')

            converter.remove_converted_sources(
                [converted, unreadable], [str(converted)], root
            )

            self.assertFalse(converted.exists())
            self.assertTrue(shared.exists())

    def test_source_cleanup_removes_only_unshared_local_buffers(self):
        with tempfile.TemporaryDirectory() as value:
            root = Path(value).resolve()
            source = root / 'Model.gltf'
            buffer = root / 'Model.bin'
            source.write_text(json.dumps({
                'asset': {'version': '2.0'},
                'buffers': [{'uri': 'Model.bin', 'byteLength': 4}],
            }), encoding='utf-8')
            buffer.write_bytes(b'data')

            converter.remove_converted_sources([source], [str(source)], root)

            self.assertFalse(source.exists())
            self.assertFalse(buffer.exists())


if __name__ == '__main__':
    unittest.main()
