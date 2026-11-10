"""Summarize ProfilerCaptureExport output using only the Python standard library."""
import argparse
import csv
import hashlib
import json
import math
import statistics
from pathlib import Path


def read_csv(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def quantile(values, fraction):
    values = sorted(values)
    position = (len(values) - 1) * fraction
    lower = int(position)
    return values[lower] + (values[min(lower + 1, len(values) - 1)] - values[lower]) * (position - lower)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export_directory", type=Path)
    args = parser.parse_args()
    root = args.export_directory
    datasets = {}
    provenance = {}
    for population in (0, 10, 20, 40):
        stem = f"{population}k"
        frames = read_csv(root / f"{stem}.frames.csv")
        summary = {row["marker"]: row for row in read_csv(root / f"{stem}.summary.csv")}
        edges = read_csv(root / f"{stem}.edges.csv")
        validation = (root / f"{stem}.validation.txt").read_text()
        assert validation.startswith("PASS"), stem
        assert all(float(row["frame_ms"]) > 0 for row in frames), stem
        assert all(int(row["frames"]) == len(frames) for row in summary.values()), stem
        for name, row in summary.items():
            for measure in ("total", "self"):
                key = f"{name}:{measure}_ms"
                if key not in frames[0]:
                    continue
                actual = statistics.mean(float(frame[key]) for frame in frames)
                assert math.isclose(actual, float(row[f"mean_{measure}_ms"]), abs_tol=1e-8), (stem, name)
        # The total inclusive GC bytes of the root must equal allocation metadata,
        # rather than summing inclusive bytes over ancestors and double counting.
        assert math.isclose(float(summary["Main Thread"]["mean_inclusive_gc_bytes"]), float(summary["GC.Alloc"]["mean_inclusive_gc_bytes"]), abs_tol=1e-8)
        datasets[population] = (frames, summary, edges)
        source = root.parent / f"{stem}.data"
        with source.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        provenance[stem] = {"source": str(source), "bytes": source.stat().st_size, "sha256": digest, "complete_frames": len(frames), "first_exported_frame": frames[0]["frame"], "last_exported_frame": frames[-1]["frame"], "native_hierarchy_validation": validation.strip()}

    def value(n, name, field="mean_total_ms"):
        return float(datasets[n][1].get(name, {}).get(field, 0))

    def number(value, places=2):
        return f"{value:.{places}f}".replace(".", ",")

    def frame_stat(n, fraction=None):
        values = [float(row["frame_ms"]) for row in datasets[n][0]]
        return statistics.mean(values) if fraction is None else quantile(values, fraction)

    comparison = []
    for n in datasets:
        comparison.append({
            "additional_kobolds": n,
            "complete_frames": len(datasets[n][0]),
            "frame_mean_ms": frame_stat(n),
            "frame_p95_ms": frame_stat(n, .95),
            "frame_p99_ms": frame_stat(n, .99),
            "fps_from_mean_frame": 1000 / frame_stat(n),
            "target_fps_wait_mean_ms": value(n, "WaitForTargetFPS"),
            "late_update_mean_ms": value(n, "PreLateUpdate.ScriptRunBehaviourLateUpdate"),
            "penetrator_mean_ms": value(n, "Penetration.Penetrator.LateUpdate"),
            "autoseek_mean_ms": value(n, "Penetration.Penetrator.AutoSeek"),
            "getpath_mean_ms": value(n, "Penetration.Penetrable.GetPath"),
            "getpath_calls_mean": value(n, "Penetration.Penetrable.GetPath", "mean_calls"),
            "spline_rebuild_mean_ms": value(n, "Penetration.Spline.Rebuild"),
            "spline_rebuild_calls_mean": value(n, "Penetration.Spline.Rebuild", "mean_calls"),
        })
    with (root / "comparison.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(comparison[0]))
        writer.writeheader()
        writer.writerows(comparison)
    (root / "provenance.json").write_text(json.dumps(provenance, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    lines = [
        "# Investigação de FPS por população — 2026-11-10",
        "",
        "**Maior consumidor entre os scripts medidos: `Penetrator.LateUpdate`, com " + number(value(40, "Penetration.Penetrator.LateUpdate")) + " ms por frame com 40 Kobolds adicionais. O principal trabalho interno é reconstruir curvas, especialmente durante consultas repetidas a `Penetrable.GetPath()`.**",
        "",
        "Foram analisadas as quatro capturas nativas e consultadas as quatro imagens fornecidas. Todos os Kobolds estavam na linha de visão, com limite de 60 FPS definido no jogo, conforme informado pelo usuário. Não existe comparação fora de visão nesta coleta.",
        "",
        "## Método e validação",
        "",
        "Leitura offline com o próprio Unity 2021.3.45f2, em projeto temporário separado do jogo. Cada arquivo forneceu 300 frames ao leitor; o frame terminal tinha duração zero e estava incompleto. Foram usados **299 frames completos por cenário**, sem remover os picos não nulos. A exportação inicial atingiu o limite de memória no cenário de 40; foi repetida com limites locais de 2.000 frames e 1,5 GiB para recuperar todos os 300 frames disponíveis. Nenhuma preferência do Editor do usuário foi alterada.",
        "",
        "Tempos próprios, totais e contagens dos marcadores instrumentados do primeiro frame completo de cada cenário foram confrontados com a Hierarchy nativa do Unity (tolerância de 0,001 ms). Todas as quatro verificações passaram. Médias dos CSVs por frame também foram confrontadas com os resumos. Os hashes dos arquivos de entrada constam em [provenance.json](./provenance.json).",
        "",
        "Os percentis usam interpolação linear. Marcadores ausentes contam como zero naquele frame. Os tempos abaixo são da thread principal e incluem esperas internas quando existentes; não medem todo o trabalho paralelo. O tempo de GPU não estava disponível. GC é contabilizado pelos metadados de `GC.Alloc`; não houve amostra `GC.Collect` nos frames analisados.",
        "",
        "## Escala com a população",
        "",
        "Tempos médios em milissegundos; as colunas de marcadores são inclusivas e podem se sobrepor.",
        "",
        "| Kobolds adicionais | Frame | Espera pelo limite | Scripts LateUpdate | Penetrator | Reconstrução de curvas | Reconstruções/frame |",
        "| ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for row in comparison:
        lines.append("| " + str(row["additional_kobolds"]) + " | " + " | ".join(number(row[key]) for key in ("frame_mean_ms", "target_fps_wait_mean_ms", "late_update_mean_ms", "penetrator_mean_ms", "spline_rebuild_mean_ms", "spline_rebuild_calls_mean")) + " |")
    lines += [
        "",
        "As capturas de 0, 10 e 20 mantêm aproximadamente 16,7 ms porque o limitador preenche a folga com `WaitForTargetFPS`. Portanto, o FPS estável nesses cenários esconde o aumento do trabalho. Com 40, a espera cai para " + number(value(40, "WaitForTargetFPS"), 4) + " ms e o frame médio sobe para " + number(frame_stat(40)) + " ms (" + number(1000 / frame_stat(40), 1) + " FPS pelo inverso do tempo médio). Isso descreve a janela capturada; os 43 FPS observados posteriormente pelo usuário pertencem a outra janela de execução.",
        "",
        "| Kobolds adicionais | Mediana do frame | p95 | p99 |",
        "| ---: | ---: | ---: | ---: |",
    ]
    for n in datasets:
        lines.append(f"| {n} | " + " | ".join(number(frame_stat(n, q)) for q in (.5, .95, .99)) + " |")
    lines += [
        "",
        "## Onde o tempo é gasto com 40",
        "",
        "| Marcador | Média ms | p95 ms | Chamadas/frame |",
        "| --- | ---: | ---: | ---: |",
    ]
    markers = ["PreLateUpdate.ScriptRunBehaviourLateUpdate", "Penetration.Penetrator.LateUpdate", "Penetration.Penetrator.AutoSeek", "Penetration.Penetrable.GetPath", "Penetration.Spline.Rebuild", "Penetration.Spline.BinormalLUT", "Penetration.Spline.DistanceLUT", "Physics.Interpolation", "FixedUpdate.PhysicsFixedUpdate", "Jiggle.Rig.Advance", "Jiggle.Skin.Advance", "PostLateUpdate.FinishFrameRendering", "Penetration.Deformer.Upload", "Penetration.ProceduralDeformation.Upload", "Kobold.Ragdoller.LateUpdate", "Kobold.BonerInflatable.OnSizeChanged", "Kobold.MetabolismTick"]
    for name in markers:
        lines.append(f"| `{name}` | {number(value(40, name))} | {number(value(40, name, 'p95_total_ms'))} | {number(value(40, name, 'mean_calls'))} |")
    edges = datasets[40][2]
    seek_path = next(row for row in edges if row["parent"] == "Penetration.Penetrator.AutoSeek" and row["child"] == "Penetration.Penetrable.GetPath")
    lines += [
        "",
        "**Não somar as linhas de curvas:** `Spline.Rebuild` inclui as duas LUTs; `GetPath` inclui reconstrução; `AutoSeek` inclui consultas; `Penetrator.LateUpdate` inclui esses caminhos. Os " + number(value(40, "Penetration.Penetrator.LateUpdate")) + " ms do Penetrator representam aproximadamente " + number(100 * value(40, "Penetration.Penetrator.LateUpdate") / frame_stat(40), 1) + "% do frame médio capturado.",
        "",
        "Dentro de `AutoSeek`, somente as consultas a `GetPath` consomem " + number(float(seek_path["mean_total_ms"])) + " ms em " + number(float(seek_path["mean_calls"])) + " chamadas por frame (" + number(100 * float(seek_path["mean_total_ms"]) / value(40, "Penetration.Penetrator.AutoSeek"), 1) + "% desse bloco). A chamada física `Physics.OverlapSphereNonAlloc`, somada em todos os seus consumidores da thread principal, custa " + number(value(40, "Physics.OverlapSphereNonAlloc")) + " ms. Logo, o custo principal da busca está na avaliação/reconstrução das curvas dos candidatos, e não apenas na consulta espacial.",
        "",
        "A chamada a `Penetrable.GetPath` reconstrói as LUTs em cada acesso, sem verificar se os pontos mudaram. A busca de cada penetrador consulta os alvos próximos; os caminhos de construção da interação também consultam esses alvos. No cenário de 40 foram medidos " + number(value(40, "Penetration.Penetrable.GetPath", "mean_calls")) + " acessos a `GetPath` por frame e " + number(value(40, "Penetration.Penetrator.LateUpdate", "mean_calls")) + " callbacks de penetradores. As contagens são de componentes/chamadas, não uma correspondência de um penetrador por Kobold.",
        "",
        "A população e o número de componentes ativos variam: `Kobold.Throb.Update` registra 1, 11, 21 e 41 chamadas por frame, enquanto `Penetrator.LateUpdate` registra 0, 7, 12 e 24. Não concluir complexidade quadrática a partir dessas quatro amostras; densidade, composição e estado dos personagens também influenciam.",
        "",
        "## Revisão dos suspeitos",
        "",
        "- **Curvas/busca: prioridade confirmada entre os scripts instrumentados.** As LUTs dominam o tempo próprio e não apresentam alocações gerenciadas nessas amostras. Aumentar paralelismo sem remover reconstruções redundantes manteria trabalho desnecessário.",
        "- **Uploads visuais: prioridade reduzida nesta coleta.** Os dois marcadores de upload somam " + number(value(40, "Penetration.Deformer.Upload") + value(40, "Penetration.ProceduralDeformation.Upload")) + " ms na thread principal; não são o consumidor dominante medido. Não há medição de duração GPU.",
        "- **Física: custo secundário relevante.** Interpolação custa " + number(value(40, "Physics.Interpolation")) + " ms e o bloco de física fixa " + number(value(40, "FixedUpdate.PhysicsFixedUpdate")) + " ms. Há " + number(value(40, "Physics.Simulate", "mean_calls")) + " passos físicos por frame em média, contra " + number(value(0, "Physics.Simulate", "mean_calls")) + " no cenário vazio. Esses marcadores não devem ser somados indiscriminadamente aos seus descendentes. O aumento de passos por frame acompanha o frame mais longo e pode amplificar a carga.",
        "- **Ragdoll: distinguir callback de custo nativo.** O callback C# mede " + number(value(40, "Kobold.Ragdoller.LateUpdate")) + " ms. Isso reduz sua prioridade direta, mas não exclui efeitos posteriores de configurações/transforms na física nativa.",
        "- **Jiggle: custo secundário.** Rig e skin somam " + number(value(40, "Jiggle.Rig.Advance") + value(40, "Jiggle.Skin.Advance")) + " ms nesta thread.",
        "- **Infladores e metabolismo:** os marcadores medidos têm médias muito menores que as curvas. A alocação total é de aproximadamente " + number(value(40, "GC.Alloc", "mean_inclusive_gc_bytes") / 1024) + " KiB por frame, sem coleta de GC registrada nessa janela. Não são a explicação principal demonstrada para o custo contínuo.",
        "",
        "## Primeira correção indicada e validação pendente",
        "",
        "1. Evitar reconstruir `Penetrable.GetPath()` quando a sequência de posições dos pontos permanece exatamente igual. A invalidação precisa observar a pose real; um cache somente por `Time.frameCount` pode devolver dados antigos se os transforms mudarem dentro do mesmo frame. Não alterar a ordem da simulação ou os callbacks.",
        "2. Avaliar um caminho específico para obter a tangente de entrada durante a seleção dos candidatos, sem gerar LUTs completas. Manter os mesmos cálculos/resultado do caminho atual e cobrir casos degenerados. Isso deve vir depois ou em mudança separada para atribuir o ganho.",
        "3. Capturar A/B no mesmo save, população, posição, estados e build. Medir principalmente `Spline.Rebuild:Calls`, `GetPath`, `AutoSeek`, frame/p95/p99 e equivalência das interações. Não prometer um FPS final a partir de subtrair esses marcadores do frame.",
        "",
        "Nenhum comportamento do jogo foi alterado nesta análise. A comparação fora de visão continua pendente; os resultados acima são do cenário visível. Nenhuma nova intervenção do usuário é necessária para concluir o diagnóstico desta coleta.",
        "",
        "## Como localizar os marcadores manualmente",
        "",
        "No Profiler, escolha CPU Usage, clique em um frame do gráfico e troque Timeline por Hierarchy. Use a busca da tabela para `Penetration.`; selecione `Penetration.Penetrator.LateUpdate` ou `Penetration.Spline.Rebuild`. Leia `Time ms`, `Self ms`, `Calls` e `GC Alloc`. Na Timeline, os mesmos marcadores são blocos sob a Main Thread; clique no bloco para consultar o tempo. Um marcador não é um módulo que precise ser ativado separadamente. A busca filtra a visualização, não determina o que foi gravado. Mantenha Deep Profile desligado para esta comparação.",
        "",
        "Dados reutilizáveis: [comparison.csv](./comparison.csv), [40k.summary.csv](./40k.summary.csv), [40k.frames.csv](./40k.frames.csv), [40k.edges.csv](./40k.edges.csv). As capturas originais foram preservadas.",
    ]
    (root / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("PASS CSV means, frame validity, native-hierarchy confirmations and GC accounting for four captures.")
    print("Report:", root / "report.md")
    for row in comparison:
        print(row)


if __name__ == "__main__":
    main()
