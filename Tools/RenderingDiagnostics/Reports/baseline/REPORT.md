# Relatório de captura de renderização

Estado: `complete`. Ambiente: Development Player.
Unity 2021.3.45f2; NVIDIA GeForce RTX 3080 Ti; resolução 1280 × 720; qualidade 0; Vanilla=True.

Contadores globais de todos os passes/câmeras. Tempos ausentes permanecem vazios. Observações de `isVisible` e candidatos de frustum **não confirmam draws**.

| Configuração | Pose | Batches | SetPass | Draw calls | Casters | Frame ms |
|---|---|---:|---:|---:|---:|---:|
| original | A | 302.000 | 90.000 | 456.000 | 355.000 | cap externo |
| no-occlusion | A | 3026.500 | 379.000 | 3316.000 | 431.000 | cap externo |
| no-shadows | A | 111.000 | 61.000 | 114.000 | 0.000 | cap externo |
| neither | A | 2777.000 | 338.000 | 2897.000 | 0.000 | cap externo |
| original | B | 581.000 | 168.000 | 747.000 | 416.000 | cap externo |
| no-occlusion | B | 2949.000 | 369.000 | 3230.000 | 430.000 | cap externo |
| no-shadows | B | 340.000 | 106.000 | 352.000 | 0.000 | cap externo |
| neither | B | 2705.000 | 327.000 | 2814.000 | 0.000 | cap externo |

Valores da matriz: média das medianas das duas repetições por pose; dispersão e p95 em [summary.csv](summary.csv). Efeitos condicionais e interação em [isolation.json](isolation.json).

B − A, observação original de visibilidade: 2158 candidatos. Consulte os CSVs de diff para identificação verificável; nenhum candidato foi promovido a draw confirmado.

A curva deve ser comparada nos dois sentidos e nos três percursos. Um degrau não identifica sozinho célula de oclusão.

Atribuição de passes/objetos depende da exportação do Frame Debugger. Eventos SRP com múltiplos draws podem expor somente um objeto associado; essa associação não identifica todos os membros do batch.

**Tempos sem cap inválidos:** Completed under the user-relaxed criterion. Render counters can be compared under matched conditions; frame time gains cannot be established with RTSS cap.

Cena salva SHA-256: `23678f3f58c349d812a6475463b2056952093ea0d43ddf556582953fb0a874f0`; bake: `0821878f3997246037e127ab2a961fc09b9a81701eb9d1c65f411f41eaa60319`.

Transformações salvas e runtime devem ser comparadas por caminho; IDs são locais à execução. Carregamento aditivo/prefab não comprova invalidação do bake.

Captura diagnóstica de referência; não conta como tentativa corretiva.
Aceite atualizado: aproximar A/B e demonstrar benefício comparativo em condições semelhantes; 319/99 não é um limite rígido. Com cap externo não se alega ganho de frame time.
