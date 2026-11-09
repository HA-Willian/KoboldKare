# Relatório de captura de renderização

Estado: `complete`. Ambiente: Development Player.
Unity 2021.3.45f2; NVIDIA GeForce RTX 3080 Ti; resolução 1280 × 720; qualidade 0; Vanilla=True.

Contadores globais de todos os passes/câmeras. Tempos ausentes permanecem vazios. Observações de `isVisible` e candidatos de frustum **não confirmam draws**.

| Configuração | Pose | Batches | SetPass | Draw calls | Casters | Frame ms |
|---|---|---:|---:|---:|---:|---:|
| original | A | 220.000 | 78.000 | 304.000 | 197.000 | cap externo |
| no-occlusion | A | 2896.000 | 359.500 | 3102.000 | 202.000 | cap externo |
| no-shadows | A | 110.000 | 63.000 | 113.000 | 0.000 | cap externo |
| neither | A | 2777.500 | 336.500 | 2898.500 | 0.000 | cap externo |
| original | B | 449.000 | 139.000 | 535.000 | 188.000 | cap externo |
| no-occlusion | B | 2822.500 | 348.500 | 3013.500 | 195.000 | cap externo |
| no-shadows | B | 342.000 | 107.000 | 354.000 | 0.000 | cap externo |
| neither | B | 2708.000 | 324.000 | 2813.000 | 0.000 | cap externo |

Valores da matriz: média das medianas das duas repetições por pose; dispersão e p95 em [summary.csv](summary.csv). Efeitos condicionais e interação em [isolation.json](isolation.json).

B − A, observação original de visibilidade: 2156 candidatos. Consulte os CSVs de diff para identificação verificável; nenhum candidato foi promovido a draw confirmado.

A curva deve ser comparada nos dois sentidos e nos três percursos. Um degrau não identifica sozinho célula de oclusão.

Atribuição de passes/objetos depende da exportação do Frame Debugger. Eventos SRP com múltiplos draws podem expor somente um objeto associado; essa associação não identifica todos os membros do batch.

**Tempos sem cap inválidos:** Completed under the user-relaxed criterion. Render counters can be compared under matched conditions; frame time gains cannot be established with RTSS cap.

Cena salva SHA-256: `23678f3f58c349d812a6475463b2056952093ea0d43ddf556582953fb0a874f0`; bake: `0821878f3997246037e127ab2a961fc09b9a81701eb9d1c65f411f41eaa60319`.

Transformações salvas e runtime devem ser comparadas por caminho; IDs são locais à execução. Carregamento aditivo/prefab não comprova invalidação do bake.

Validação da tentativa corretiva 1; consultar comparação antes/depois e inspeção visual.
Aceite atualizado: aproximar A/B e demonstrar benefício comparativo em condições semelhantes; 319/99 não é um limite rígido. Com cap externo não se alega ganho de frame time.
