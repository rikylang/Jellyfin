#!/usr/bin/env python3
"""Build and package the Live Hub plugin (JPRM-compatible zip + Jellyfin repository manifest).

Usage:
  python build/package.py --version 0.1.0.0 [--output artifacts] [--base-url URL] [--no-build]

Produces:
  artifacts/livehub_<version>.zip   plugin DLLs + meta.json (same layout as `jprm plugin build`)
  artifacts/manifest.json           repository manifest for Dashboard -> Plugins -> Repositories
Only the Python standard library is used.
"""
import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "src", "Jellyfin.Plugin.LiveHub", "Jellyfin.Plugin.LiveHub.csproj")


def parse_build_yaml(path):
    """Minimal parser for the flat build.yaml used by JPRM (scalars, '>' folded blocks, lists)."""
    data, key, mode, buf = {}, None, None, []
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()

    def flush():
        if key is not None and mode == "fold":
            data[key] = " ".join(s.strip() for s in buf).strip()
        elif key is not None and mode == "list":
            data[key] = buf[:]

    for raw in lines:
        if raw.strip() in ("---", "") and mode != "fold":
            continue
        top = re.match(r"^([A-Za-z_][\w-]*):\s*(.*)$", raw)
        if top and not raw.startswith(" "):
            flush()
            key, value = top.group(1), top.group(2).strip()
            buf = []
            if value == ">":
                mode = "fold"
            elif value == "":
                mode = "list"
            else:
                mode = None
                data[key] = value.strip('"').strip("'")
            continue
        if mode == "fold":
            buf.append(raw)
        elif mode == "list":
            item = re.match(r"^\s*-\s*(.+)$", raw)
            if item:
                buf.append(item.group(1).strip().strip('"').strip("'"))
    flush()
    return data


def md5(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", required=True)
    ap.add_argument("--output", default=os.path.join(ROOT, "artifacts"))
    ap.add_argument("--base-url", default="", help="URL where the zip will be downloadable (manifest sourceUrl prefix)")
    ap.add_argument("--configuration", default="Release")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--changelog", default="")
    args = ap.parse_args()

    meta_src = parse_build_yaml(os.path.join(ROOT, "build.yaml"))
    publish_dir = os.path.join(args.output, "publish")
    os.makedirs(args.output, exist_ok=True)

    if not args.no_build:
        if os.path.isdir(publish_dir):
            shutil.rmtree(publish_dir)
        cmd = ["dotnet", "publish", PROJECT, "-c", args.configuration, "-o", publish_dir,
               "-p:Version=" + args.version, "-p:AssemblyVersion=" + args.version, "-p:FileVersion=" + args.version,
               "--nologo"]
        print("+", " ".join(cmd), flush=True)
        subprocess.run(cmd, check=True)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    changelog = args.changelog or meta_src.get("changelog", "")
    meta = {
        "category": meta_src["category"],
        "changelog": changelog,
        "description": meta_src["description"],
        "guid": meta_src["guid"],
        "name": meta_src["name"],
        "overview": meta_src["overview"],
        "owner": meta_src["owner"],
        "targetAbi": meta_src["targetAbi"],
        "framework": meta_src.get("framework", "net10.0"),
        "timestamp": timestamp,
        "version": args.version,
        "artifacts": meta_src["artifacts"],
    }

    zip_name = "livehub_{}.zip".format(args.version)
    zip_path = os.path.join(args.output, zip_name)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for artifact in meta_src["artifacts"]:
            src = os.path.join(publish_dir, artifact)
            if not os.path.isfile(src):
                sys.exit("missing artifact: " + src)
            z.write(src, artifact)
        z.writestr("meta.json", json.dumps(meta, indent=2))

    checksum = md5(zip_path)
    source_url = (args.base_url.rstrip("/") + "/" + zip_name) if args.base_url else zip_name
    manifest_path = os.path.join(args.output, "manifest.json")
    manifest = []
    if os.path.isfile(manifest_path):
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    entry = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if entry is None:
        entry = {"guid": meta["guid"], "versions": []}
        manifest.append(entry)
    entry.update({
        "name": meta["name"], "description": meta["description"], "overview": meta["overview"],
        "owner": meta["owner"], "category": meta["category"], "imageUrl": "",
    })
    entry["versions"] = [v for v in entry["versions"] if v.get("version") != args.version]
    entry["versions"].insert(0, {
        "version": args.version, "changelog": changelog, "targetAbi": meta["targetAbi"],
        "sourceUrl": source_url, "checksum": checksum, "timestamp": timestamp,
    })
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)

    print("zip      :", zip_path)
    print("md5      :", checksum)
    print("manifest :", manifest_path)


if __name__ == "__main__":
    main()
