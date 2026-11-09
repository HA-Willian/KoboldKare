# Relatório de captura de renderização

Estado: `complete`. Ambiente: Development Player.
Unity 2021.3.45f2; NVIDIA GeForce RTX 3080 Ti; resolução 1280 × 720; qualidade 0; Vanilla=True.

Contadores globais de todos os passes/câmeras. Tempos ausentes permanecem vazios. Observações de `isVisible` e candidatos de frustum **não confirmam draws**.

| Configuração | Pose | Batches | SetPass | Draw calls | Casters | Frame ms |
|---|---|---:|---:|---:|---:|---:|
| original | A | 177.000 | 71.000 | 217.000 | 134.000 | cap externo |
| no-occlusion | A | 3033.250 | 381.750 | 3322.250 | 431.000 | cap externo |
| no-shadows | A | 83.000 | 53.000 | 83.000 | 0.000 | cap externo |
| neither | A | 2780.000 | 336.000 | 2900.000 | 0.000 | cap externo |
| original | B | 167.000 | 66.000 | 212.000 | 133.000 | cap externo |
| no-occlusion | B | 2958.000 | 373.000 | 3239.000 | 430.000 | cap externo |
| no-shadows | B | 79.000 | 48.000 | 79.000 | 0.000 | cap externo |
| neither | B | 2707.000 | 325.000 | 2814.000 | 0.000 | cap externo |

Valores da matriz: média das medianas das duas repetições por pose; dispersão e p95 em [summary.csv](summary.csv). Efeitos condicionais e interação em [isolation.json](isolation.json).

B − A, observação original de visibilidade: 8 candidatos. Consulte os CSVs de diff para identificação verificável; nenhum candidato foi promovido a draw confirmado.

A curva deve ser comparada nos dois sentidos e nos três percursos. Um degrau não identifica sozinho célula de oclusão.

Atribuição de passes/objetos depende da exportação do Frame Debugger. Eventos SRP com múltiplos draws podem expor somente um objeto associado; essa associação não identifica todos os membros do batch.

**Tempos sem cap inválidos:** Final root executable check confirms counters after preservation cleanup. Use the separate baseline-timing and attempt-2-timing captures for unconstrained frame-time comparison.

Cena salva SHA-256: `43abfaebef1cde2dac6f4e95d891482c044cb83515c19636a11a6c4207e7d23c`; bake: `83c262192814f20fbecb914f4acf3d98aff364ecd5a9caa0f1fafeac4ede16b4`.

Transformações salvas e runtime devem ser comparadas por caminho; IDs são locais à execução. Carregamento aditivo/prefab não comprova invalidação do bake.

Validação da tentativa corretiva 2; consultar comparação antes/depois e inspeção visual.
Aceite atualizado: aproximar A/B e demonstrar benefício comparativo em condições semelhantes; 319/99 não é um limite rígido. Com cap externo não se alega ganho de frame time.
