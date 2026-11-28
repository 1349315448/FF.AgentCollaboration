"""Create the signed update feed from published archives and their HTTPS URLs."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import subprocess
from urllib.parse import urlsplit


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release", type=Path, help="JSON with version, notes and assets (product, rid, url, file)")
    parser.add_argument("private_key", type=Path, help="ECDSA signing key in PEM form")
    parser.add_argument("output", type=Path, help="new ff-acp-update.json file")
    args = parser.parse_args()
    spec = json.loads(args.release.read_text(encoding="utf-8"))
    assets = []
    seen = set()
    for asset in spec["assets"]:
        product, rid = asset["product"], asset["rid"]
        if product not in {"workbench", "signaling"} or not rid or (product, rid) in seen:
            raise ValueError("Invalid or duplicate asset kind")
        seen.add((product, rid))
        url = urlsplit(asset["url"])
        if url.scheme != "https" or not url.hostname or url.username or url.fragment:
            raise ValueError("Every asset needs an HTTPS download URL")
        sha256 = hashlib.sha256()
        with (args.release.parent / asset["file"]).open("rb") as archive:
            for chunk in iter(lambda: archive.read(1024 * 1024), b""):
                sha256.update(chunk)
        assets.append(dict(product=product, rid=rid, url=asset["url"], sha256=sha256.hexdigest()))
    payload = json.dumps(dict(version=spec["version"], notes=spec.get("notes", ""), assets=assets),
                         ensure_ascii=False, separators=(",", ":")).encode()
    signature = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(args.private_key)],
                               input=payload, stdout=subprocess.PIPE, check=True).stdout
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(dict(payload=base64.b64encode(payload).decode(), signature=base64.b64encode(signature).decode()), output)
        output.write("\n")


if __name__ == "__main__":
    main()
