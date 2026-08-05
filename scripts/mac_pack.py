#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""macOS .app 组装器（纯标准库，Windows 本机与 macOS CI 通用）。

产出 PakAssetStudio.app：
  Contents/MacOS/    = dotnet publish 输出（含 Languages/）+ Tools/（repak、umodel、assimp、
                       convert_gltf_to_fbx.py、merge_gltf.py）+ LICENSE/README.md/THIRD-PARTY-NOTICES
  Contents/Info.plist
  Contents/Resources/icon.icns（可选）

并生成 zip + SHA-256 与 THIRD-PARTY-MANIFEST.txt（mac 版，格式与 Windows 版一致）。

用法（Windows 本机，dev 快速验证，无 repak mac 二进制）：
  tools/python/runtime/python.exe scripts/mac_pack.py --publish-dir artifacts/publish/osx-arm64 --version dev --skip-tools

正式发布（macOS CI 或本机，提供 repak mac 二进制）：
  python3 scripts/mac_pack.py --publish-dir <dir> --repak <universal-repak> --assimp <libassimp.dylib> \
      --version X.Y.Z --output artifacts/release --arch universal
"""

import argparse
import hashlib
import os
import plistlib
import re
import shutil
import stat
import subprocess
import sys
import zipfile

APP_NAME = "PakAssetStudio"
EXECUTABLE = "PakAssetStudio.Avalonia"
BUNDLE_ID = "com.darthcyk.pakassetstudio"
MIN_MACOS = "14.0"  # .NET 10 官方支持矩阵最低 macOS 版本

# zip 中需要保留可执行位的文件（相对 Contents/MacOS/）
EXEC_FILES = {EXECUTABLE, os.path.join("Tools", "repak", "repak")}


def repo_root():
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run_version_cmd(cmd, timeout=30):
    """运行版本查询命令；失败返回 None。"""
    try:
        out = subprocess.run(
            cmd, capture_output=True, timeout=timeout, check=False
        ).stdout.decode("utf-8", "replace").strip()
        return out or None
    except Exception:
        return None


def copy_with_mode(src, dst, mode):
    shutil.copy2(src, dst)
    try:
        os.chmod(dst, mode)
    except OSError:
        pass  # Windows 上 chmod 仅支持只读位，zip 阶段另行写入权限位


def copy_tree(src_dir, dst_dir):
    for name in os.listdir(src_dir):
        s = os.path.join(src_dir, name)
        d = os.path.join(dst_dir, name)
        if os.path.isdir(s):
            shutil.copytree(s, d, dirs_exist_ok=True)
        else:
            shutil.copy2(s, d)


def assemble_app(publish_dir, tools_sources, app_dir, version, icon):
    macos_dir = os.path.join(app_dir, "Contents", "MacOS")
    resources_dir = os.path.join(app_dir, "Contents", "Resources")
    os.makedirs(macos_dir, exist_ok=True)
    os.makedirs(resources_dir, exist_ok=True)

    # 1) dotnet publish 输出整体进 MacOS/
    copy_tree(publish_dir, macos_dir)

    # 2) Tools/ 组装（Windows 版由 csproj Content Include 完成，mac 版在此组装）
    for rel, src in tools_sources:
        if src is None:
            continue
        dst = os.path.join(macos_dir, rel)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if os.path.isdir(src):
            shutil.copytree(src, dst, dirs_exist_ok=True)
        else:
            copy_with_mode(src, dst, 0o755 if rel in EXEC_FILES else 0o644)

    # 3) 仓库根文档
    root = repo_root()
    for doc in ("LICENSE", "README.md", "THIRD-PARTY-NOTICES"):
        src = os.path.join(root, doc)
        if os.path.isfile(src):
            shutil.copy2(src, os.path.join(macos_dir, doc))

    # 4) Info.plist（CFBundleShortVersionString 只取 X.Y.Z 主版本，预发布后缀归 CFBundleVersion）
    semver_match = re.match(r"^\d+\.\d+\.\d+", version)
    short_version = semver_match.group(0) if semver_match else "0.0.0"
    info = {
        "CFBundleDevelopmentRegion": "zh_CN",
        "CFBundleExecutable": EXECUTABLE,
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleName": "PAK Asset Studio",
        "CFBundleDisplayName": "PAK Asset Studio",
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": short_version,
        "CFBundleVersion": short_version,
        "LSMinimumSystemVersion": MIN_MACOS,
        "NSHighResolutionCapable": True,
        "NSPrincipalClass": "NSApplication",
        "LSApplicationCategoryType": "public.app-category.developer-tools",
    }
    if icon:
        shutil.copy2(icon, os.path.join(resources_dir, "icon.icns"))
        info["CFBundleIconFile"] = "icon.icns"
    with open(os.path.join(app_dir, "Contents", "Info.plist"), "wb") as f:
        f.write(plistlib.dumps(info, sort_keys=False))


def write_manifest(app_dir, repak_path, assimp_path, assimp_version, skip_tools):
    """生成 THIRD-PARTY-MANIFEST.txt（mac 版）。尽力取真实版本，失败以 sha256 兜底。"""
    macos_dir = os.path.join(app_dir, "Contents", "MacOS")
    lines = [
        "PAK Asset Studio third-party runtime manifest (macOS)",
        "Generated (UTC): %s" % __import__("datetime").datetime.now(
            __import__("datetime").timezone.utc
        ).isoformat(),
        "",
    ]

    def artifact(rel):
        return os.path.join(macos_dir, rel)

    # repak
    if not skip_tools:
        repak_rel = os.path.join("Tools", "repak", "repak")
        repak_ver = None
        if repak_path and (os.name == "posix" or repak_path.lower().endswith(".exe")):
            repak_ver = run_version_cmd([repak_path, "--version"])
        lines.append("repak version: %s" % (repak_ver or "unreported (artifact sha256:%s)"
                     % sha256_of(artifact(repak_rel))))

    # Python（mac 用系统 python3）
    py_ver = run_version_cmd([sys.executable, "--version"]) or run_version_cmd(["python3", "--version"])
    lines.append("Python version: %s" % (py_ver or "unreported"))

    # UModel（Windows 二进制，mac 上无法运行 → sha256 兜底；Windows 本机可尝试）
    umodel_path = artifact(os.path.join("Tools", "umodel", "umodel_64.exe"))
    umodel_out = None
    if not skip_tools and os.path.exists(umodel_path) and os.name == "nt":
        umodel_out = run_version_cmd([umodel_path, "-version"])
    if umodel_out:
        m = re.search(r"^Compiled\s+(.+?)\s+\(build\s+(\d+)\)\s*$", umodel_out, re.M | re.I)
        if m:
            umodel_ver = "build %s (compiled %s)" % (m.group(2), re.sub(r"\s+", " ", m.group(1)).strip())
        else:
            umodel_ver = "unreported (artifact sha256:%s)" % sha256_of(umodel_path)
    elif not skip_tools and os.path.exists(umodel_path):
        umodel_ver = "unreported (artifact sha256:%s)" % sha256_of(umodel_path)
    else:
        umodel_ver = "unreported"
    lines.append("UModel version: %s" % umodel_ver)

    # Assimp（参数优先；mac 上可 ctypes 直读 dylib）
    assimp_ver = assimp_version
    if not assimp_ver and assimp_path and os.name == "posix":
        code = ("import ctypes, sys; lib = ctypes.CDLL(sys.argv[1]); "
                "print(lib.aiGetVersionMajor(), lib.aiGetVersionMinor(), "
                "lib.aiGetVersionPatch(), sep=chr(46))")
        assimp_ver = run_version_cmd([sys.executable, "-c", code, assimp_path])
    lines.append("Assimp version: %s" % (assimp_ver or "unreported"))
    lines.append("")

    # SHA-256 清单
    files = [
        os.path.join("Tools", "repak", "repak"),
        os.path.join("Tools", "repak", "liboo2coremac64.2.9.10.dylib"),
        os.path.join("Tools", "umodel", "umodel_64.exe"),
        os.path.join("Tools", "umodel", "SDL2_64.dll"),
        os.path.join("Tools", "assimp", "libassimp.dylib"),
        os.path.join("Tools", "convert_gltf_to_fbx.py"),
        os.path.join("Tools", "merge_gltf.py"),
    ]
    lines.append("SHA-256:")
    for rel in files:
        p = artifact(rel)
        if skip_tools or not os.path.isfile(p):
            continue
        lines.append("%s  %s" % (sha256_of(p), rel.replace(os.sep, "/")))

    with open(os.path.join(macos_dir, "THIRD-PARTY-MANIFEST.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def verify_layout(app_dir, skip_tools, version):
    macos_dir = os.path.join(app_dir, "Contents", "MacOS")
    required = [
        os.path.join("Contents", "Info.plist"),
        os.path.join("Contents", "MacOS", EXECUTABLE),
        os.path.join("Contents", "MacOS", "Languages", "zh-CN.json"),
        os.path.join("Contents", "MacOS", "Languages", "en-US.json"),
        os.path.join("Contents", "MacOS", "LICENSE"),
        os.path.join("Contents", "MacOS", "README.md"),
        os.path.join("Contents", "MacOS", "THIRD-PARTY-NOTICES"),
        os.path.join("Contents", "MacOS", "THIRD-PARTY-MANIFEST.txt"),
    ]
    if not skip_tools:
        required += [
            os.path.join("Contents", "MacOS", "Tools", "repak", "repak"),
            os.path.join("Contents", "MacOS", "Tools", "repak", "liboo2coremac64.2.9.10.dylib"),
            os.path.join("Contents", "MacOS", "Tools", "repak", "LICENSE-MIT"),
            os.path.join("Contents", "MacOS", "Tools", "repak", "LICENSE-APACHE"),
            os.path.join("Contents", "MacOS", "Tools", "repak", "README.md"),
            os.path.join("Contents", "MacOS", "Tools", "umodel", "umodel_64.exe"),
            os.path.join("Contents", "MacOS", "Tools", "umodel", "SDL2_64.dll"),
            os.path.join("Contents", "MacOS", "Tools", "umodel", "LICENSE.txt"),
            os.path.join("Contents", "MacOS", "Tools", "umodel", "readme.txt"),
            os.path.join("Contents", "MacOS", "Tools", "assimp", "libassimp.dylib"),
            os.path.join("Contents", "MacOS", "Tools", "assimp", "LICENSE"),
            os.path.join("Contents", "MacOS", "Tools", "convert_gltf_to_fbx.py"),
            os.path.join("Contents", "MacOS", "Tools", "merge_gltf.py"),
            os.path.join("Contents", "MacOS", "Prerequisites", "vc_redist.x64.exe"),
        ]
    missing = [rel for rel in required if not os.path.isfile(os.path.join(app_dir, rel))]
    if missing:
        raise RuntimeError("Missing publish artifact(s): %s" % ", ".join(missing))

    # CFBundleShortVersionString 校验（预发布版本只比对 X.Y.Z 前缀）
    with open(os.path.join(app_dir, "Contents", "Info.plist"), "rb") as f:
        info = plistlib.load(f)
    if not re.match(r"^\d+\.\d+\.\d+$", info["CFBundleShortVersionString"]):
        raise RuntimeError("CFBundleShortVersionString is not X.Y.Z: %s"
                           % info["CFBundleShortVersionString"])
    expected_short = (re.match(r"^\d+\.\d+\.\d+", version).group(0)
                      if version and re.match(r"^\d+\.\d+\.\d+", version) else None)
    if expected_short and info["CFBundleShortVersionString"] != expected_short:
        raise RuntimeError("CFBundleShortVersionString mismatch: expected %s, found %s"
                           % (expected_short, info["CFBundleShortVersionString"]))


def make_zip(app_dir, zip_path):
    """打 zip 并保留 Unix 权限位（macOS 解压后 apphost/repak 保持可执行）。"""
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, dirs, files in os.walk(app_dir):
            for name in sorted(files):
                full = os.path.join(root, name)
                rel = os.path.relpath(full, os.path.dirname(app_dir)).replace("\\", "/")
                mode = 0o755 if rel in _zip_exec_paths(app_dir) else 0o644
                zi = zipfile.ZipInfo(rel)
                zi.external_attr = (mode & 0xFFFF) << 16
                zi.compress_type = zipfile.ZIP_DEFLATED
                with open(full, "rb") as f:
                    zf.writestr(zi, f.read())


def _zip_exec_paths(app_dir):
    app_name = os.path.basename(app_dir)
    return {
        "%s/Contents/MacOS/%s" % (app_name, EXECUTABLE),
        "%s/Contents/MacOS/Tools/repak/repak" % app_name,
    }


def main():
    parser = argparse.ArgumentParser(description="PAK Asset Studio macOS .app 组装器")
    parser.add_argument("--publish-dir", required=True, help="dotnet publish 输出目录")
    parser.add_argument("--repak", help="repak macOS 二进制路径（universal 或单架构）")
    parser.add_argument("--assimp", help="libassimp.dylib 路径")
    parser.add_argument("--assimp-version", help="Assimp 版本号（如 6.0.5），省略时尽力自动探测")
    parser.add_argument("--version", default="dev", help="发布版本（默认 dev）")
    parser.add_argument("--arch", default="arm64", help="架构标识，用于产物命名（arm64/universal）")
    parser.add_argument("--output", default=os.path.join(repo_root(), "artifacts", "release"))
    parser.add_argument("--icon", help="可选 icon.icns 路径")
    parser.add_argument("--skip-tools", action="store_true", help="跳过 Tools 组装与校验（本机 dev 验证用）")
    parser.add_argument("--no-zip", action="store_true", help="只组装 .app 不打 zip")
    args = parser.parse_args()

    app_dir = os.path.join(args.output, "%s.app" % APP_NAME)
    if os.path.exists(app_dir):
        shutil.rmtree(app_dir)

    root = repo_root()
    tools_sources = []
    if not args.skip_tools:
        repak_dir = os.path.join(root, "tools", "repak")
        umodel_dir = os.path.join(root, "tools", "umodel")
        assimp_dir = os.path.join(root, "tools", "assimp")
        if not args.repak or not os.path.isfile(args.repak):
            raise RuntimeError("--repak 必须指向 repak macOS 二进制（CI 产物）；本机 dev 验证请加 --skip-tools")
        if not args.assimp or not os.path.isfile(args.assimp):
            raise RuntimeError("--assimp 必须指向 libassimp.dylib；本机 dev 验证请加 --skip-tools")
        tools_sources = [
            (os.path.join("Tools", "repak", "repak"), args.repak),
            (os.path.join("Tools", "repak", "liboo2coremac64.2.9.10.dylib"),
             os.path.join(repak_dir, "liboo2coremac64.2.9.10.dylib")),
            (os.path.join("Tools", "repak", "LICENSE-MIT"), os.path.join(repak_dir, "LICENSE-MIT")),
            (os.path.join("Tools", "repak", "LICENSE-APACHE"), os.path.join(repak_dir, "LICENSE-APACHE")),
            (os.path.join("Tools", "repak", "README.md"), os.path.join(repak_dir, "README.md")),
            (os.path.join("Tools", "umodel", "umodel_64.exe"), os.path.join(umodel_dir, "umodel_64.exe")),
            (os.path.join("Tools", "umodel", "SDL2_64.dll"), os.path.join(umodel_dir, "SDL2_64.dll")),
            (os.path.join("Tools", "umodel", "LICENSE.txt"), os.path.join(umodel_dir, "LICENSE.txt")),
            (os.path.join("Tools", "umodel", "readme.txt"), os.path.join(umodel_dir, "readme.txt")),
            (os.path.join("Tools", "assimp", "libassimp.dylib"), args.assimp),
            (os.path.join("Tools", "assimp", "LICENSE"), os.path.join(assimp_dir, "LICENSE")),
            (os.path.join("Tools", "convert_gltf_to_fbx.py"),
             os.path.join(root, "tools", "convert_gltf_to_fbx.py")),
            (os.path.join("Tools", "merge_gltf.py"), os.path.join(root, "tools", "merge_gltf.py")),
            (os.path.join("Prerequisites", "vc_redist.x64.exe"),
             os.path.join(root, "tools", "vc_runtime", "vc_redist.x64.exe")),
        ]

    print("Assembling %s ..." % app_dir)
    assemble_app(args.publish_dir, tools_sources, app_dir, args.version, args.icon)
    write_manifest(app_dir, args.repak if not args.skip_tools else None,
                   args.assimp if not args.skip_tools else None, args.assimp_version,
                   args.skip_tools)
    verify_layout(app_dir, args.skip_tools, args.version)
    print("Layout verified.")

    if args.no_zip:
        print("Done: %s" % app_dir)
        return

    os.makedirs(args.output, exist_ok=True)
    zip_path = os.path.join(args.output, "PakAssetStudio-v%s-macos-%s.zip" % (args.version, args.arch))
    make_zip(app_dir, zip_path)
    digest = sha256_of(zip_path)
    with open(zip_path + ".sha256", "w") as f:
        f.write("%s  %s\n" % (digest, os.path.basename(zip_path)))
    print("Published: %s" % zip_path)
    print("SHA-256:  %s" % digest)


if __name__ == "__main__":
    main()
