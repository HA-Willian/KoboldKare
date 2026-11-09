# Relatório de captura de renderização

Estado: `complete`. Ambiente: Development Player.
Unity 2021.3.45f2; NVIDIA GeForce RTX 3080 Ti; resolução 1280 × 720; qualidade 0; Vanilla=True.

Contadores globais de todos os passes/câmeras. Tempos ausentes permanecem vazios. Observações de `isVisible` e candidatos de frustum **não confirmam draws**.

| Configuração | Pose | Batches | SetPass | Draw calls | Casters | Frame ms |
|---|---|---:|---:|---:|---:|---:|
| original | A | 300.000 | 92.000 | 454.000 | 355.000 | 2.289 |
| no-occlusion | A | 3030.000 | 383.000 | 3320.500 | 431.000 | 5.379 |
| no-shadows | A | 109.000 | 63.000 | 112.000 | 0.000 | 1.870 |
| neither | A | 2781.500 | 344.000 | 2902.000 | 0.000 | 4.714 |
| original | B | 581.000 | 170.000 | 748.000 | 416.000 | 3.312 |
| no-occlusion | B | 2950.000 | 367.000 | 3229.000 | 430.000 | 4.848 |
| no-shadows | B | 340.000 | 110.000 | 352.000 | 0.000 | 2.179 |
| neither | B | 2713.500 | 332.000 | 2818.500 | 0.000 | 4.563 |

Valores da matriz: média das medianas das duas repetições por pose; dispersão e p95 em [summary.csv](summary.csv). Efeitos condicionais e interação em [isolation.json](isolation.json).

B − A, observação original de visibilidade: 2158 candidatos. Consulte os CSVs de diff para identificação verificável; nenhum candidato foi promovido a draw confirmado.

A curva deve ser comparada nos dois sentidos e nos três percursos. Um degrau não identifica sozinho célula de oclusão.

Atribuição de passes/objetos depende da exportação do Frame Debugger. Eventos SRP com múltiplos draws podem expor somente um objeto associado; essa associação não identifica todos os membros do batch.

Cena salva SHA-256: `23678f3f58c349d812a6475463b2056952093ea0d43ddf556582953fb0a874f0`; bake: `0821878f3997246037e127ab2a961fc09b9a81701eb9d1c65f411f41eaa60319`.

Transformações salvas e runtime devem ser comparadas por caminho; IDs são locais à execução. Carregamento aditivo/prefab não comprova invalidação do bake.

Captura diagnóstica de referência; não conta como tentativa corretiva.
Aceite atualizado: aproximar A/B e demonstrar benefício comparativo em condições semelhantes; 319/99 não é um limite rígido. Com cap externo não se alega ganho de frame time.
