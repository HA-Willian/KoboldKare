"""Report confirmed Frame Debugger events without inventing SRP batch membership."""
import argparse
import json
from collections import Counter
from pathlib import Path
from analyze import read_json, write_csv


def stage(event):
    info = event["info"]
    for name in ("MainLightShadow", "AdditionalLightsShadow", "DrawOpaqueObjects", "DrawTransparentObjects", "RenderPostProcessingEffects"):
        if "/" + name in info:
            return name
    return info


def confirmed(event):
    return event["dataAvailable"] and event["dataIndexMatches"]


def draw_event(event):
    # Clear/compute events can retain irrelevant draw/shader fields in this internal API.
    return event["type"] in {"SRPBatch", "Mesh", "DynamicGeometry", "DrawProcedural", "DrawProceduralIndirect", "DrawMeshInstanced", "DrawMeshInstancedIndirect"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("a", type=Path)
    parser.add_argument("b", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    a, b = read_json(args.a), read_json(args.b)
    output = args.output
    output.mkdir(parents=True, exist_ok=True)
    summaries = {}
    for label, capture in (("A", a), ("B", b)):
        passes = {}
        for event in capture["events"]:
            group = passes.setdefault(stage(event), {"events": 0, "events_with_valid_data": 0, "known_draws": 0, "unresolved_multi_draw_events": 0})
            group["events"] += 1
            if confirmed(event):
                group["events_with_valid_data"] += 1
                group["known_draws"] += max(0, event["draws"]) if draw_event(event) else 0
                if draw_event(event) and event["draws"] > 1:
                    group["unresolved_multi_draw_events"] += 1
        summaries[label] = passes
        write_csv(output / f"frame-{label}-events.csv", [{
            "index": e["index"], "stage": stage(e), "type": e["type"], "validData": confirmed(e),
            "draws": e["draws"] if confirmed(e) and draw_event(e) else None,
            "shader": e["shader"], "pass": e["pass"], "lightMode": e["lightMode"], "keywords": e["keywords"],
            "rendererKey": e["rendererKey"], "associatedObject": e["associatedObject"], "evidence": e["objectEvidence"],
            "batchBreakCause": capture["batchBreakCauses"][e["batchBreakCause"]] if confirmed(e) and 0 <= e["batchBreakCause"] < len(capture["batchBreakCauses"]) else ""
        } for e in capture["events"]])
    # Only explicit Renderer associations on valid single-draw events become object membership evidence.
    def members(capture):
        return Counter((e["rendererKey"], stage(e), e["shader"], e["pass"], e["keywords"]) for e in capture["events"]
                       if confirmed(e) and draw_event(e) and e["draws"] == 1 and e["rendererKey"])
    left, right = members(a), members(b)
    diffs = []
    for direction, additional in (("A-B", left-right), ("B-A", right-left)):
        for (key, group, shader, shader_pass, keywords), count in additional.items():
            diffs.append({"direction": direction, "rendererKey": key, "stage": group, "shader": shader, "pass": shader_pass,
                          "keywords": keywords, "additionalEvents": count, "evidence": "Explicit renderer association in valid single-draw Frame Debugger events; incomplete for multi-draw SRP batches."})
    write_csv(output / "confirmed-single-draw-diff.csv", diffs,
              ["direction", "rendererKey", "stage", "shader", "pass", "keywords", "additionalEvents", "evidence"])
    (output / "pass-summary.json").write_text(json.dumps(summaries, indent=2), encoding="utf-8")
    lines = ["# Comparação de passes — Frame Debugger", "", "Capturas diagnósticas no Editor, separadas dos tempos de player. Eventos, draw calls e Renderers são grandezas diferentes.", "",
             "| Passe | Eventos A | Eventos B | Draws conhecidos A | Draws conhecidos B |", "|---|---:|---:|---:|---:|"]
    for group in sorted(set(summaries["A"]) | set(summaries["B"])):
        before = summaries["A"].get(group, {})
        after = summaries["B"].get(group, {})
        lines.append(f"| {group} | {before.get('events', 0)} | {after.get('events', 0)} | {before.get('known_draws', 0)} | {after.get('known_draws', 0)} |")
    lines += ["", "Draws conhecidos somam somente eventos com dados válidos. Campos ausentes não contam como zero draws reais.", "",
              "[Diff verificável de associações individuais](confirmed-single-draw-diff.csv). A lista é **parcial**: eventos SRP com múltiplos draws não expõem necessariamente todos os Renderers. O único objeto associado não representa todo o batch.", "",
              "Os CSVs de eventos preservam índices, passe, shader, keywords e motivo de quebra. Não foi inferida a participação de um objeto por semelhança de mesh/material ou por `isVisible`.", ""]
    (output / "FRAME_REPORT.md").write_text("\n".join(lines), encoding="utf-8")
    print(output / "FRAME_REPORT.md")


if __name__ == "__main__":
    main()
