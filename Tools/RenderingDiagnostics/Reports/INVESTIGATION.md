# Resultado da investigação A/B

**Aceite atualizado atingido nas poses testadas.** Foi mantido o novo bake de oclusão com **Smallest Occluder = 3**, preservando Smallest Hole = 0,25 e Backface Threshold = 100. As sombras permanecem em distância 100 e quatro cascatas. Não foram aplicadas mudanças de geometria, materiais, instancing, zonas ou DOTS/ECS.

## Antes e depois

Development Player Vanilla, Unity 2021.3.45f2 / URP 12.1.15, RTX 3080 Ti, Ryzen 7 5700X, 1280×720, qualidade 0, MSAA 2, FOV vertical 90,071945°, aspecto 16:9. Poses A/B conforme o pedido, com OrbitCamera travada. Os contadores são globais de todos os passes/câmeras.

| Pose | Referência: Batches / SetPass | Validação final: Batches / SetPass | Draw calls antes → depois | Shadow casters antes → depois |
|---|---:|---:|---:|---:|
| A | 302 / 90 | 177 / 71 | 456 → 217 | 355 → 134 |
| B | 581 / 168 | 167 / 66 | 747 → 212 | 416 → 133 |

B teve aproximadamente **71% menos batches** e **61% menos SetPass**. A diferença A/B caiu de **279 para 10 batches** e de **78 para 5 SetPass**. Isso atende à flexibilização solicitada: aproximar os cenários, sem exigir 319/99 exatos.

Na primeira validação completa do bake, os valores foram 177/73 e 167/67; a checagem final repetiu os batches e apresentou pequena variação de SetPass. Os relatórios preservam os números de cada execução.

- [Referência completa](baseline/REPORT.md)
- [Validação completa da tentativa 2](attempt-2/REPORT.md)
- [Curva comparativa nos três percursos](attempt-2/comparison-curve.png)
- [Comparação por pose/amostra](attempt-2/comparison.csv)
- [Matriz da versão final](final-validation/REPORT.md)

## Tempo de frame

As primeiras medições estavam limitadas a aproximadamente 16,7 ms; elas **não** foram usadas para alegar ganho de tempo. O RTSS tinha 90 FPS no perfil global e 60 FPS no perfil específico. Seus arquivos/configurações não foram alterados.

Foi feito um comparativo adicional com os executáveis original e otimizado em pastas de teste, 120 frames medidos por extremo e repetição, VSync=0 e limite interno=-1. Os tempos ficaram abaixo de 11,1 ms, demonstrando que aqueles limites de 60/90 FPS não estavam atuando nessas medições.

| Pose | Frame ms antes | Frame ms depois |
|---|---:|---:|
| A | 2,289 | 2,302 |
| B | 3,312 | 2,214 |

Os valores são a média das medianas das duas ordens. B apresentou aproximadamente **33% menos tempo de frame**, enquanto A permaneceu praticamente igual. GPU Frame Time não estava disponível; o marcador Render Thread retornou zero e não forneceu duração útil separada. Não foram inventados tempos de GPU/render thread.

[Dados do comparativo de tempos](timing-comparison/comparison.csv), [referência de tempo](baseline-timing/REPORT.md), [resultado de tempo](attempt-2-timing/REPORT.md).

## Diagnóstico e causa sustentada

A curva original dependia principalmente da **posição**. A rotação isolada variou pouco, aproximadamente 296–306 batches. O intervalo combinado refinado coincidiu nos dois sentidos em t=0,8828125…0,890625; houve outros aumentos pelo percurso. Isso localiza transições sem provar troca de célula.

Após o novo bake, os percursos ficaram muito mais planos e próximos. Desligar a oclusão continua elevando ambas as poses a aproximadamente três mil batches, com valores próximos ao controle original. Isso sustenta que o benefício veio da seleção por oclusão, preservando o conteúdo do cenário. O experimento combina reconstrução dos dados atuais e parâmetro menor; não separa completamente resolução de eventual desatualização anterior e não demonstra que o bake original fosse inválido.

FarmRegion manteve posição, rotação e escala em relação à cena salva nas observações realizadas. A mudança de índice de irmão da raiz após GameManager ir para DontDestroyOnLoad foi tratada como identificação hierárquica, sem confundi-la com movimento.

## Passes e objetos

[Comparativo do Frame Debugger original](passes/FRAME_REPORT.md), [eventos A](passes/frame-A-events.csv), [eventos B](passes/frame-B-events.csv), [diff individual verificável](passes/confirmed-single-draw-diff.csv).

| Passe na captura diagnóstica do Editor | Eventos A → B | Draws conhecidos A → B |
|---|---:|---:|
| MainLightShadow | 50 → 97 | 342 → 397 |
| DrawOpaqueObjects | 18 → 87 | 53 → 289 |

Todos os eventos exportados tiveram leitura com índice válido. Esses totais são de capturas separadas no Editor, não os mesmos contadores globais do player. O diff identifica associações adicionais a Bush e GroundBush_LOD3, com hierarquia, shader, keywords e passe. Nos eventos de sombra com shader Rock, a contagem original passou de 4 para 22.

**Limitação preservada:** a API expõe eventos SRP com vários draws sem enumerar necessariamente todos os Renderers. A lista individual é parcial e explicitamente identificada assim. Renderer.isVisible e frustum permanecem observações/candidatos; não foram promovidos a draws confirmados. Quantidades adicionais de eventos também podem representar outra cascata do mesmo objeto.

## Auditoria da casa e dos rocks

- [Cena original resumida](saved-scene-summary.json) e [geometria fonte de Stone](stone-source-geometry.json).
- Stone tem flags Occluder/Occludee, bounds de aproximadamente 22,64×8,06×26,65 m, 4.360 vértices importados, dois submeshes e 2.154 triângulos. A análise soldada encontrou 534 bordas de contorno e oito arestas com mais de duas faces; isso não comprova erro porque há aberturas e partes desconectadas.
- A amostragem fonte por raios encontrou muitas superfícies finas; a mediana dos pares de entrada/saída foi 0,25 unidade Blender. Smallest Hole descreve aberturas, não uma espessura mínima obrigatória da parede. As coordenadas Blender e Unity também diferem; o arquivo registra as limitações.
- Os Renderers HexaDoor da casa têm flags estáticas zero. Não foram convertidos em occluders estáticos. Nas seis imagens verificadas, não houve desaparecimento visível de paredes, objetos da sala vizinha ou sombras locais.
- Inventário diagnóstico: 991 Renderers com rock no nome, quatro materiais compartilhados, zero MPBs e diferentes índices de lightmap. Não foi comprovada instanciação desnecessária de materiais nem justificada uma mudança de instancing.

## Tentativas e decisões

**Duas tentativas corretivas validadas, dentro do limite de cinco.** [Registro detalhado](attempts.json).

1. Sombras: distância 60, duas cascatas. B caiu para 449/139, mas a diferença entre poses continuou grande. Não mantida.
2. Bake: Smallest Occluder 3, demais parâmetros originais. A/B ficaram próximos e as imagens locais preservaram a aparência. Mantida.

Um experimento prévio em 1,5/0,2 falhou no TomeGenerator por falta de memória, antes de validação. Também houve rejeição indevida de uma geração moderada pela guarda de logs, corrigida e repetida usando o cache. Esses episódios estão registrados, sem serem apresentados como medições válidas ou prova de defeito do bake original.

## Preservação, custo e verificação

A cena final difere do snapshot inicial **somente** em Smallest Occluder 5→3. As demais alterações anteriores do usuário foram preservadas por bytes/hashes; o GUID/meta do bake foi mantido. O bake original e a cena estão disponíveis no [backup original](../../../Builds/RenderingDiagnostics/BakeBackups/attempt-2-moderate-retry-original), que pode ser restaurado pelo comando documentado na ferramenta.

O novo arquivo de bake cresceu de **75.450.222 para 198.156.622 bytes** em formato texto. Esse é um custo real de dados; não foi alegada redução de memória de runtime. Uma operação de conversão de formato teve efeitos amplos de reserialização: foi retirada da ferramenta, e todos os arquivos alheios foram restaurados, incluindo os snapshots das alterações locais. O bake permaneceu no formato texto original do projeto.

Development Build final concluída com Addressables reconstruídos; matriz final executada após a restauração das dependências. Sete testes do analisador/atribuição passaram. A validação completa incluiu 17 amostras em cada sentido para três percursos, três níveis de bisseção e quatro poses extras. A inspeção visual cobre essas seis poses locais; não constitui varredura de todo o mapa nem de todas as câmeras secundárias.

Ferramenta e reprodução: [README](../README.md). Sem mudança de geometria, materiais, distância/cascatas de sombras ou migração para DOTS/ECS.
