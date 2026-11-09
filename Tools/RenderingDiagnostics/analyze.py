"""Analyze completed Unity captures. Python stdlib only; never infer draws from visibility."""
import argparse
import csv
import json
import math
import re
import statistics
from collections import Counter, defaultdict
from pathlib import Path

METRICS = ("batches", "setpass", "draws", "shadowCasters", "frameMs", "mainThreadNs", "renderThreadNs", "gpuNs", "cpuTimingMs", "gpuTimingMs")


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def percentile(values, fraction):
    values = sorted(values)
    at = (len(values) - 1) * fraction
    lo = math.floor(at)
    hi = math.ceil(at)
    return values[lo] + (values[hi] - values[lo]) * (at - lo)


def summarize(rows):
    groups = defaultdict(list)
    for row in rows:
        groups[row["label"]].append(row)
    output = []
    for label, frames in groups.items():
        result = {k: frames[0][k] for k in ("label", "configuration", "path", "direction", "t")}
        result["frames"] = len(frames)
        for metric in METRICS:
            values = [float(r[metric]) for r in frames if r.get(metric, "") != "" and float(r[metric]) >= 0]
            for stat, value in (("median", statistics.median(values) if values else None),
                                ("p95", percentile(values, .95) if values else None),
                                ("min", min(values) if values else None),
                                ("max", max(values) if values else None)):
                result[f"{metric}_{stat}"] = value
        output.append(result)
    return output


def write_csv(path, rows, fields=None):
    if fields is None:
        fields = list(dict.fromkeys(k for row in rows for k in row))
    with Path(path).open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(rows)


def visibility_diff(a, b, field="isVisibleObserved"):
    def visible(snapshot):
        # A/B belong to the same session. Instance IDs survive sibling/root reordering.
        return {r.get("id", r["key"]): r for r in snapshot["renderers"] if r["active"] and r["enabled"] and r[field]}
    left, right = visible(a), visible(b)
    return ({key: left[key] for key in left.keys() - right.keys()},
            {key: right[key] for key in right.keys() - left.keys()})


def flattened(renderer, direction, observation):
    return {
        "direction": direction, "evidence": observation + "; NOT confirmed draw", "key": renderer["key"],
        "instanceId": renderer["id"], "name": renderer["name"], "path": renderer["path"], "scene": renderer["scene"],
        "mesh": renderer["mesh"], "meshId": renderer["meshId"],
        "materials": " | ".join(f'{m["name"]}#{m["id"]}' for m in renderer["materials"] if m),
        "shaders": " | ".join(m["shader"] for m in renderer["materials"] if m),
        "keywords": " | ".join(" ".join(m["keywords"]) for m in renderer["materials"] if m),
        "lightmapIndex": renderer["lightmapIndex"], "staticBatch": renderer["staticBatch"], "propertyBlock": renderer["propertyBlock"],
        "shadowCasting": renderer["shadowCasting"],
    }


def group_candidates(renderers):
    result = {}
    result["name"] = Counter(r["name"] for r in renderers)
    result["material"] = Counter(f'{m["name"]}#{m["id"]}' for r in renderers for m in r["materials"] if m)
    result["shader"] = Counter(m["shader"] for r in renderers for m in r["materials"] if m)
    return {kind: dict(counts.most_common()) for kind, counts in result.items()}


def isolation(summary):
    # Both endpoint repetitions participate; do not pool A/B or timing columns with missing data.
    result = {}
    for pose in "AB":
        configurations = {}
        for config in ("original", "no-occlusion", "no-shadows", "neither"):
            records = [r for r in summary if r["path"] == "matrix" and r["configuration"] == config
                       and r["label"] in (f"{config}-{pose}", f"{config}-{pose}-repeat")]
            configurations[config] = {m: statistics.mean(r[f"{m}_median"] for r in records if r[f"{m}_median"] is not None)
                                    if any(r[f"{m}_median"] is not None for r in records) else None for m in METRICS}
        effects = {}
        for metric in METRICS:
            o, c, s, n = [configurations[k][metric] for k in ("original", "no-occlusion", "no-shadows", "neither")]
            if any(v is None for v in (o, c, s, n)):
                continue
            effects[metric] = {"occlusion_saving_with_shadows": c - o,
                               "occlusion_saving_without_shadows": n - s,
                               "shadow_cost_with_occlusion": o - s,
                               "shadow_cost_without_occlusion": c - n,
                               "interaction": o - c - s + n}
        result[pose] = {"configurations": configurations, "effects": effects}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--saved-scene", type=Path)
    parser.add_argument("--attempt", type=int, default=0)
    args = parser.parse_args()
    capture = args.capture
    output = args.output or capture / "analysis"
    output.mkdir(parents=True, exist_ok=True)
    manifest = read_json(capture / "manifest.json")
    validity_path = capture / "measurement-validity.json"
    validity = read_json(validity_path) if validity_path.exists() else {}
    invalid_uncapped_time = validity.get("frameTimeValidForUncappedComparison") is False
    status = (capture / "status.txt").read_text(encoding="utf-8-sig").strip()
    with (capture / "frames.csv").open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream))
    summary = summarize(rows)
    write_csv(output / "summary.csv", summary)
    effects = isolation(summary)
    (output / "isolation.json").write_text(json.dumps(effects, indent=2), encoding="utf-8")
    groups = {}
    candidate_count = None
    for config in ("original", "no-occlusion", "no-shadows", "neither"):
        a_path, b_path = (capture / f"{config}-{p}-inventory.json" for p in "AB")
        if not a_path.exists() or not b_path.exists():
            continue
        a, b = read_json(a_path), read_json(b_path)
        for observation in ("isVisibleObserved", "frustumCandidate"):
            a_minus_b, b_minus_a = visibility_diff(a, b, observation)
            flattened_rows = [flattened(r, direction, observation) for direction, group in (("A-B", a_minus_b), ("B-A", b_minus_a)) for r in sorted(group.values(), key=lambda r: r["key"])]
            write_csv(output / f"{config}-{observation}-diff.csv", flattened_rows,
                      list(flattened(next(iter(a["renderers"])), "", "")) if a["renderers"] else ["direction", "evidence", "key"])
            groups[f"{config}-{observation}"] = {"A-B": group_candidates(a_minus_b.values()), "B-A": group_candidates(b_minus_a.values())}
            if config == "original" and observation == "isVisibleObserved":
                candidate_count = len(b_minus_a)
        # Full inventory of rocks, including objects invisible in both poses.
        rocks = {r["key"]: r for r in a["renderers"] + b["renderers"] if "rock" in r["name"].lower()}
        write_csv(output / f"{config}-rocks.csv", [flattened(r, "inventory", "inventory only") for r in rocks.values()])
    (output / "candidate-groups.json").write_text(json.dumps(groups, indent=2), encoding="utf-8")
    lines = ["# Relatório de captura de renderização", "", f"Estado: `{status}`. Ambiente: {'Editor (diagnóstico; não é referência de player)' if manifest['editor'] else 'Development Player'}.",
             f"Unity {manifest['unity']}; {manifest['gpu']}; resolução {manifest['width']} × {manifest['height']}; qualidade {manifest['quality']}; Vanilla={manifest['vanilla']}.", "",
             "Contadores globais de todos os passes/câmeras. Tempos ausentes permanecem vazios. Observações de `isVisible` e candidatos de frustum **não confirmam draws**.", "",
             "| Configuração | Pose | Batches | SetPass | Draw calls | Casters | Frame ms |", "|---|---|---:|---:|---:|---:|---:|"]
    for pose in "AB":
        for config, metrics in effects[pose]["configurations"].items():
            def fmt(metric):
                if metric == "frameMs" and invalid_uncapped_time:
                    return "cap externo" if validity.get("externalCapFps") else "não comparado"
                value = metrics[metric]
                return "—" if value is None else f"{value:.3f}"
            lines.append(f"| {config} | {pose} | " + " | ".join(fmt(m) for m in ("batches", "setpass", "draws", "shadowCasters", "frameMs")) + " |")
    lines += ["", "Valores da matriz: média das medianas das duas repetições por pose; dispersão e p95 em [summary.csv](summary.csv). Efeitos condicionais e interação em [isolation.json](isolation.json).",
              "", f"B − A, observação original de visibilidade: {candidate_count if candidate_count is not None else 'pendente'} candidatos. Consulte os CSVs de diff para identificação verificável; nenhum candidato foi promovido a draw confirmado.",
              "", "A curva deve ser comparada nos dois sentidos e nos três percursos. Um degrau não identifica sozinho célula de oclusão.",
              "", "Atribuição de passes/objetos depende da exportação do Frame Debugger. Eventos SRP com múltiplos draws podem expor somente um objeto associado; essa associação não identifica todos os membros do batch."]
    if status != "complete":
        lines += ["", "**Captura incompleta: não constitui a referência completa solicitada.**"]
    if invalid_uncapped_time:
        lines += ["", "**Tempos sem cap inválidos:** " + validity.get("note", "Limitador externo identificado.")]
    if args.saved_scene:
        saved = read_json(args.saved_scene)
        lines += ["", f"Cena salva SHA-256: `{saved['sceneSha256']}`; bake: `{saved['occlusionSha256']}`.",
                  "", "Transformações salvas e runtime devem ser comparadas por caminho; IDs são locais à execução. Carregamento aditivo/prefab não comprova invalidação do bake."]
        runtime = read_json(capture / "region-runtime.json")
        def region_key(key):
            # Moving GameManager to DontDestroyOnLoad shifts root sibling indices.
            # Match a unique scene+root name, retaining all child indices.
            return re.sub(r"(::[^/]+)\[\d+\](?=/|$)", r"\1", key)
        saved_transforms = defaultdict(list)
        for t in saved["transforms"]:
            saved_transforms[region_key(t["key"])].append(t)
        comparisons = []
        for change in runtime["changes"]:
            t = change["transform"]
            candidates = saved_transforms.get(region_key(t["key"]), [])
            old = candidates[0] if len(candidates) == 1 else None
            comparisons.append({"frame": change["frame"], "key": t["key"], "saved_found": old is not None,
                                "differences": [field for field in ("position", "rotation", "lossyScale", "localPosition", "localRotation", "localScale") if old and old[field] != t[field]]})
        (output / "region-comparison.json").write_text(json.dumps(comparisons, indent=2), encoding="utf-8")
    lines += ["", (f"Validação da tentativa corretiva {args.attempt}; consultar comparação antes/depois e inspeção visual." if args.attempt else
                    "Captura diagnóstica de referência; não conta como tentativa corretiva."),
              "Aceite atualizado: aproximar A/B e demonstrar benefício comparativo em condições semelhantes; 319/99 não é um limite rígido. Com cap externo não se alega ganho de frame time.", ""]
    (output / "REPORT.md").write_text("\n".join(lines), encoding="utf-8")
    print(output / "REPORT.md")


if __name__ == "__main__":
    main()
