"""Compare complete reference/intervention captures with matched measurement conditions."""
import argparse
import csv
import json
from pathlib import Path
from analyze import read_json, summarize, write_csv


def load_capture(path):
    status = (path / "status.txt").read_text(encoding="utf-8-sig").strip()
    if status != "complete":
        raise ValueError(f"Capture is not complete: {path}: {status}")
    manifest = read_json(path / "manifest.json")
    with (path / "frames.csv").open(encoding="utf-8-sig", newline="") as stream:
        return manifest, summarize(list(csv.DictReader(stream)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("intervention", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    a_manifest, a = load_capture(args.reference)
    b_manifest, b = load_capture(args.intervention)
    matching = ("unity", "platform", "gpu", "cpu", "quality", "width", "height", "msaa", "renderScale", "fov", "aspect", "editor", "vanilla", "scenes")
    differences = [key for key in matching if a_manifest[key] != b_manifest[key]]
    if differences:
        raise ValueError("Unmatched capture conditions: " + ", ".join(differences))
    if a_manifest["editor"] or not a_manifest["vanilla"]:
        raise ValueError("Use Vanilla Development Players for intervention validation.")
    a_by_label = {r["label"]: r for r in a if r["configuration"] == "original"}
    rows = []
    for after in b:
        before = a_by_label.get(after["label"])
        if before is None or after["configuration"] != "original":
            continue
        row = {k: after[k] for k in ("label", "path", "direction", "t")}
        for metric in ("batches", "setpass", "draws", "shadowCasters", "frameMs", "mainThreadNs", "renderThreadNs", "gpuNs"):
            key = metric + "_median"
            row[metric + "_before"], row[metric + "_after"] = before[key], after[key]
            row[metric + "_delta"] = after[key] - before[key] if before[key] is not None and after[key] is not None else None
        rows.append(row)
    args.output.mkdir(parents=True, exist_ok=True)
    write_csv(args.output / "comparison.csv", rows)
    (args.output / "conditions.json").write_text(json.dumps({"reference": a_manifest, "intervention": b_manifest}, indent=2), encoding="utf-8")
    print(args.output / "comparison.csv")


if __name__ == "__main__":
    main()
