# Plano de performance — branch optmization

## Estado em 2026-11-06

- Investigação estática inicial concluída; não há medição própria de antes/depois.
- Pintura localizada desativada nos pontos de entrada do jogo com `#if KOBOLDKARE_RUNTIME_PAINTING`. O símbolo não está definido nas configurações versionadas. Código, referências serializadas e pacote preservados.
- Otimizações do metabolismo implementadas e verificadas por testes headless de regressão contra o commit `8a07abe8c07df7cc06150f1872e741aa4949965d`. Compilação completa, Play Mode e captura de referência no Unity continuam pendentes.
- Culling investigado parcialmente; causa das imagens ainda não confirmada em execução.

## Metabolismo: evidências

1. [DayNightCycle](Assets/KoboldKare/Scripts/DayNightCycle.cs) reutiliza um `WaitForSeconds(2f)` e invoca todos os assinantes de um evento no mesmo frame. É uma corrotina com laço, sem chamada recursiva. A duração inclusiva no Profiler agrega os assinantes e os eventos disparados por eles.
2. [Kobold.OnMetabolizationEvent e ProcessReagents](Assets/KoboldKare/Scripts/Kobold.cs) processam somente o personagem cuja PhotonView pertence ao cliente. O caminho metaboliza, aplica consumo, devolve sobras e produtos e chama `SetGenes`, mesmo quando os atributos não mudaram. O método também regenera energia passivamente; pular personagens com estômago vazio indiscriminadamente alteraria o jogo.
3. [ReagentContents.Metabolize](Assets/KoboldKare/Scripts/Reagents/ReagentContents.cs) cria uma mistura/dicionário temporário por execução. Cada entrada transferida pode criar outro objeto `Reagent`. A montagem via `AddMix` calcula o volume total repetidamente. Entradas de volume zero permanecem no dicionário e são incluídas na mistura metabolizada quando há algum volume total.
4. Ao devolver a mistura, `AddMix` chama [ReactionsDatabase.DoReactions](Assets/KoboldKare/Scripts/Reagents/ReactionsDatabase.cs) para cada entrada, inclusive zero. O parâmetro `introducedReactant` não filtra as receitas: todas são percorridas.
5. Existe recursão indireta nas reações: [ScriptableReagentReaction.DoReaction](Assets/KoboldKare/Scripts/Reagents/ScriptableReagentReaction.cs) adiciona produtos por `AddMix`, que volta a chamar `DoReactions`. Receitas encadeadas podem multiplicar verificações; não foi comprovado um ciclo infinito nas receitas atuais.
6. `SetGenes` reaplica infladores, tamanhos, materiais e eventos. Acesso a `Renderer.materials` também devolve arrays e pode instanciar materiais quando ainda compartilhados. São fontes potenciais de CPU e alocação que exigem atribuição no Profiler.

### Como os líquidos são armazenados

Cada [GenericReagentContainer](Assets/KoboldKare/Scripts/GenericReagentContainer.cs) possui `ReagentContents`, que usa `Dictionary<byte, Reagent>` em memória: ID do tipo e volume. [Database<T>](Assets/KoboldKare/Scripts/Database.cs) mantém um catálogo em memória de assets, incluindo substituições por mods. [ScriptableReagent](Assets/KoboldKare/Scripts/Reagents/ScriptableReagent.cs) define cor, calorias, meia-vida, perda constante e efeito de consumo. A consulta por ID indexa o catálogo; a consulta por nome e a busca de ID por asset percorrem listas.

O save serializa nomes e volumes para JSON. A rede transmite IDs e volumes quantizados por BitBuffer/Photon. O metabolismo examinado não faz consulta a banco de dados em disco.

## Plano de implementação do metabolismo

### Implementação aplicada

- [DayNightCycle](Assets/KoboldKare/Scripts/DayNightCycle.cs): agendador circular com limite configurável `metabolizationsPerFrame` (padrão: quatro assinantes por frame). Cada assinante recebe `2f` e passa a ter seu próprio prazo de dois segundos, contado a partir do cadastro ou último tick. O primeiro grupo simultâneo é distribuído entre frames. Em sobrecarga, o tick atrasa; não há rajada de compensação nem aceleração do metabolismo. Pausas usam tempo escalado; reativar o componente reinicia os prazos. Remoção de delegates, remoção durante callbacks, alvos destruídos e exceções são tratados. Não há `GetInvocationList()` no despacho; ele só é usado no cadastro/remoção. Uma chamada lenta individual ainda pode exceder o orçamento desejado de frame: o limite é por quantidade, não por milissegundos.
- [ReagentContents](Assets/KoboldKare/Scripts/Reagents/ReagentContents.cs): novo caminho interno `MetabolizeInto`, com buffer pertencente ao personagem e cache dos objetos de saída por ID. O cálculo preenche o resultado diretamente, evitando a soma repetida de volumes na montagem via `AddMix`. As chaves ativas são reconstruídas sem manter reagentes antigos no resultado. A API pública que retorna uma mistura independente continua disponível. O buffer é emprestado e não pode ser retido; callbacks reentrantes do personagem usam a API independente como fallback.
- [Kobold](Assets/KoboldKare/Scripts/Kobold.cs): usa o buffer reutilizável e enumeração dos valores do dicionário sem iterator temporário no consumo. Um [snapshot dos valores dos genes](Assets/KoboldKare/Scripts/KoboldGenes.cs) detecta inclusive alterações em objetos mutáveis. Quando os valores permanecem iguais, a reaplicação visual é evitada; limites, identidade do objeto e notificações de genes/energia são preservados. Os eventos de mistura, entradas zero e regeneração passiva permanecem ativos.
- [ReactionsDatabase](Assets/KoboldKare/Scripts/Reagents/ReactionsDatabase.cs): índice por primeiro reagente obrigatório. A seleção preserva a ordem do catálogo e reavalia a mistura após cada receita, inclusive quando callbacks habilitam receitas posteriores. Continua a executar receitas prontas não relacionadas ao ID introduzido; filtrar apenas por esse ID mudaria o comportamento anterior. Casos com volumes negativos ou coeficientes incomuns mantêm a avaliação conservadora. As revisões de [Database](Assets/KoboldKare/Scripts/Database.cs) invalidam o índice ao carregar, substituir ou remover reagentes/receitas. Edições pelo Inspector invalidam via `OnValidate`; mods que alteram arrays de receitas diretamente em runtime devem chamar `ReactionsDatabase.InvalidateReactionCache()`.
- A recursão de produtos foi mantida nesta etapa, conforme a separação prevista no plano. Trocar por uma fila exige preservar a execução em profundidade e os callbacks intermediários; o índice reduz verificações desnecessárias sem fazer essa troca de semântica.
- Marcadores disponíveis no Profiler: `DayNightCycle.DispatchMetabolism`, `Kobold.MetabolismTick`, `Kobold.ProcessReagents`, `Kobold.ReturnReagents`, `Kobold.ApplyGenes`, `Reagents.Metabolize`, `Reagents.TotalVolume` e `Reagents.Reactions`.

### Validação executada

O [runner de regressão](Tools/PerformanceTests/README.md) compila em memória os fontes reais de reagentes, receitas e catálogos, além dos métodos de metabolismo extraídos do personagem. Serviços Unity/Photon e aplicação visual usam adaptadores; o JSON usa a implementação real do projeto. Não se trata de compilação completa do jogo.

- Paridade com a versão anterior em 180 cenários de metabolismo/save e 100 grafos de receitas, incluindo callbacks, adições zero, cadeias, substituição/remoção de mods, alteração dos IDs por mudanças no catálogo e casos de volumes/coeficientes negativos.
- Paridade de regeneração passiva em repouso/ragdoll, consumo parcial, atributos alterados por mutação e por substituição, mudanças de capacidade, proteção por `IsMine` e callbacks reentrantes.
- Testes adicionais para identidade dos objetos reutilizados, limpeza de resultados antigos, proteção contra origem igual ao destino, campos do snapshot, limite de ticks, justiça do agendamento, frames longos, reativação, remoção de delegates, alvos destruídos e exceções.
- Após aquecimento, o teste isolado do buffer registrou **0 bytes em 1.000 chamadas**, contra **184.000 bytes** na API que aloca uma mistura a cada chamada, no runtime Mono dos adaptadores. Esse valor não mede todo o metabolismo, receitas, consumo, Unity ou ganho de FPS.
- Verificação sintática dos arquivos C# alterados nos modos com pintura ligada/desligada e verificação de whitespace considerando CRLF.

Pendentes: compilação completa com pacotes externos, efeitos visuais em Play Mode, transferência real de autoridade multiplayer e captura comparativa de CPU/GC/p95/p99 na mesma Development Build. Não há ganho percentual de performance do jogo demonstrado até essa medição. O bake de culling não foi alterado sem confirmar a hipótese na câmera real.

### 1. Instrumentar e estabelecer uma referência

- Adicionar marcadores estáveis para a emissão do evento, metabolismo por personagem, cálculo de volumes, consumo, devolução de misturas, reações e aplicação de genes.
- Capturar uma Development Build com Deep Profile desligado e allocation call stacks habilitadas quando necessário. Usar a mesma cena, save, quantidade de personagens e receitas antes/depois.
- Separar duração inclusiva, tempo próprio, bytes de `GC.Alloc` e pausas de coleta. O screenshot indica aproximadamente 8,66 ms inclusivos; sozinho não estabelece quanto disso é coleta ou qual descendente domina.

### 2. Reduzir alocações no metabolismo

- Introduzir uma variante que escreva em um buffer pertencente ao personagem, preservando a API que retorna uma mistura para outros consumidores.
- Reutilizar armazenamento e entradas, observando retenção de referências por callbacks, reentrância e diferenças entre esvaziar um dicionário e reaproveitar os objetos `Reagent`.
- Preencher os resultados em uma passagem, evitando usar `AddMix` para montar uma mistura temporária sem reações nem observadores.
- Verificar se ignorar entradas zero mantém os contratos de callbacks e mods antes de mudar o comportamento global de `AddMix`.

### 3. Eliminar processamento redundante

- Evitar reaplicar genes quando nenhum atributo mudou. Verificar todos os consumidores, pois equivalência de valores, identidade do objeto e efeitos de eventos são questões distintas.
- Reduzir notificações intermediárias somente após mapear seus efeitos em HUD, infladores, energia e receitas.
- Criar um índice de receitas por reagente para não percorrer o catálogo inteiro a cada adição. Atualizar o índice no carregamento/descarregamento de mods.
- Tratar a recursão das receitas em uma mudança separada, preservando ordem, estequiometria, cadeias e callbacks. Uma fila de trabalho não pode simplesmente alterar a ordem de execução das receitas.

### 4. Distribuir o trabalho no tempo

- Depois das reduções de custo, substituir a rajada de todos os assinantes por agendamento por personagem ou lotes entre frames.
- Preservar o intervalo e a quantidade metabolizada por personagem, sem usar `Time.deltaTime` do frame como substituto de dois segundos. Definir comportamento em pausas, frames longos e entrada/saída de personagens.
- Manter autoridade Photon e não invocar listeners removidos/destruídos durante o processamento. Evitar `GetInvocationList()` alocando um array a cada rodada.
- Medir custo total além do maior pico: distribuir trabalho suaviza o pico, mas não reduz por si só o trabalho total.

### Critérios de aceitação

- Mesmos volumes finais, calorias, energia passiva, atributos e efeitos para uma sequência controlada de ticks.
- Cobertura de estômago vazio, volumes pequenos, meia-vida zero, capacidade cheia, sobras, limpeza, receitas simples/encadeadas e mod com receita adicional.
- Sem processamento duplicado em multiplayer; validar transferência de autoridade e destruição de personagens.
- Comparar bytes alocados por personagem/tick, custo total por intervalo e picos/p95/p99 de frame nas mesmas condições. Não estabelecer ganho percentual antes de medir.

## Pintura localizada

Os blocos excluídos cobrem colisões, explosões, jatos, projéteis, imersão, callbacks dos emissores e configurações do `PaintDecal`. As transferências de líquidos, RPCs, partículas, sons e dano permanecem fora dos blocos.

Para reativar, adicionar `KOBOLDKARE_RUNTIME_PAINTING` em **Player Settings > Scripting Define Symbols** nos alvos desejados e recompilar. A implementação permanece no repositório. O pacote é mantido como dependência; esta mudança impede as chamadas de pintura do código versionado do jogo, sem alegar remover seu código dos bundles ou bloquear mods externos que chamem o pacote diretamente.

Validação realizada: análise sintática Roslyn dos nove arquivos alterados nos dois modos (18 verificações), ausência de chamadas ativas a `PaintDecal` nesses arquivos com o símbolo desligado, igualdade dos tokens originais ao religá-lo e preservação das chamadas RPC nos dois modos. A revisão de whitespace passou considerando os finais de linha CRLF existentes. Não foi executada compilação completa, Play Mode ou medição de performance no Unity; os pacotes externos e seus possíveis comportamentos de inicialização precisam dessa validação em execução.

## Occlusion culling: achados e próximos passos

- [MainMap](Assets/KoboldKare/Scenes/MainMap.unity) referencia um [OcclusionCullingData](Assets/KoboldKare/Scenes/MainMap/OcclusionCullingData.asset) existente, com dados PVS, 3.029 renderers estáticos e zero portais. Zero portais, isoladamente, não comprova defeito.
- Configurações serializadas: Smallest Occluder = 5, Smallest Hole = 0,25 e Backface Threshold = 100. O modelo da casa tem overrides de flags estáticas; há entradas do prefab da casa nos dados do bake. Portanto, não cabe concluir que o culling simplesmente não foi habilitado ou que a casa inteira foi omitida.
- Não foi encontrada atribuição a `Camera.useOcclusionCulling` nos scripts versionados pesquisados. Isso não comprova o valor da câmera efetiva em execução, especialmente com assets/pacotes externos e criação dinâmica.
- Hipóteses a testar: resolução conservadora das células perto da parede, vãos/geometria que deixam visibilidade passar, bounds extensos de renderers, câmera fora do volume válido e bake desatualizado. Nenhuma delas está confirmada pelo screenshot.
- Na câmera efetiva do jogador, reproduzir os dois pontos das imagens usando **Occlusion > Visualization** e o Frame Debugger. Inspecionar células, bounds e draw calls da câmera principal, distinguindo passes de sombra e câmeras adicionais.
- Comparar um bake de teste com Smallest Occluder menor somente após inspecionar paredes e escala. Verificar custo de bake/memória e resultado nos dois pontos; não editar apenas o número serializado e esperar que o PVS já gravado mude.

Referências oficiais: [janela de Occlusion Culling, Unity 2021.3](https://docs.unity.cn/2021.3/Documentation/Manual/occlusion-culling-window.html), [fluxo de profiling](https://unity.com/how-to/best-practices-for-profiling-game-performance) e [alocações e ferramentas de memória](https://unity.com/how-to/use-memory-profiling-unity).

## Investigação de custo por Kobold — 2026-11-10

### Evidência das três capturas fornecidas

Valores aproximados de frames individuais; não são médias nem medições produzidas nesta investigação:

| Cenário | CPU do frame | Draw Calls (painel inferior) | Batches | Triângulos (painel inferior) |
| --- | ---: | ---: | ---: | ---: |
| Lobby vazio | 2,61 ms | 486 | 327 | 480,1 mil |
| Kobolds fora de visão | 7,14 ms | 481 | 331 | 527,7 mil |
| Kobolds visíveis | 6,90 ms | 859 | 706 | 1,3 milhão |

A captura fora de visão acrescenta aproximadamente 4,53 ms de CPU ao frame vazio, apesar de Draw Calls semelhantes. O bloco `PreLateUpdate.ScriptRunBehaviourLateUpdate` ocupa aproximadamente 2,85 ms fora de visão e 1,73 ms com os personagens visíveis. `Penetrator.LateUpdate` aparece explicitamente na primeira captura, com aproximadamente 0,87 ms em uma das amostras legíveis. Não atribuir o custo de todo o bloco a essa amostra: há outros callbacks e potencialmente outras instâncias.

Esses frames justificam priorizar scripts na thread principal. Não demonstram ganho de FPS, custo médio por personagem, escala assintótica ou que olhar para os Kobolds melhora a performance: fases da física e estados da simulação variam entre frames. A duração de GPU aparece indisponível. Culling e renderização não são o foco desta etapa.

### Caminhos síncronos encontrados, por prioridade de medição

1. **Curvas e busca automática de interação.** [Penetrator.LateUpdate](Packages/com.naelstrof.penetrationtech/Scripts/Penetrator.cs) executa na thread principal sem condição de visibilidade. Reconstrói a curva ociosa e a curva final em duas chamadas a `SetWeightsFromPoints` por execução, inclusive sem interação. Cada chamada em [CatmullSpline](Packages/com.naelstrof.penetrationtech/Scripts/CatmullSpline.cs) gera pesos, uma tabela de distância com 32 amostras e uma tabela de binormais com 16 amostras. Quando `AutoSeek` está ativo, não há intervalo de busca: satisfeitas as condições de estado/transição, executa `OverlapSphereNonAlloc` e avalia os candidatos a cada frame. `NonAlloc` evita o array de resultados, mas não elimina o custo da consulta física nem da avaliação.
2. **Reconstrução repetida em consultas.** [Penetrable.GetPath](Packages/com.naelstrof.penetrationtech/Scripts/Penetrable.cs) lê os transforms e reconstrói a curva a cada consulta; não possui cache por pose ou por frame. A avaliação de candidatos da busca chama esse método para obter a tangente. Construção de caminhos, listeners e notificações também podem consultá-lo. Aumentar a densidade de personagens pode aumentar tanto o número de buscadores quanto o de candidatos, mas isso precisa ser confirmado por `Calls` nas capturas. Não presumir custo quadrático para qualquer disposição de personagens.
3. **Envio visual contínuo fora de visão.** [CatmullDeformer.LateUpdate](Packages/com.naelstrof.penetrationtech/Scripts/CatmullDeformer.cs) empacota dados, chama `ComputeBuffer.SetData` e atualiza `MaterialPropertyBlock` todo frame. [ProceduralDeformation.LateUpdate](Packages/com.naelstrof.penetrationtech/Scripts/ProceduralDeformation.cs) envia dois buffers e reaplica seus vínculos aos renderers sem teste de visibilidade ou de alteração. Chamadas de upload são candidatas a custo de CPU/driver; o código e as imagens, sozinhos, não demonstram uma espera CPU/GPU. A chamada `BakeMesh` encontrada nesse arquivo pertence ao botão de bake do Editor, não ao loop de runtime.
4. **Rigidbodies do ragdoll.** [Ragdoller.LateUpdate e RigidbodyNetworkInfo.UpdateState](Assets/KoboldKare/Scripts/Ragdoller.cs) percorrem os corpos a cada frame, inclusive para personagens locais em pé. Reaplicam `isKinematic` e `interpolation` mesmo quando o estado não mudou. No caminho remoto de ragdoll também interpolam transforms. Esse custo multiplica por personagens e ossos; a captura também mostra aumento do bloco de interpolação física. Ainda falta estabelecer quanto cada um representa.
5. **Inflador atualizado sem mudança de valor.** [Kobold.Update](Assets/KoboldKare/Scripts/Kobold.cs) chama `bonerInflater.SetSize` por apêndice ativo a cada frame. [Inflatable.SetSize e TweenToNewSize](Packages/com.naelstrof.inflatable/Inflatable.cs) não descartam alvos iguais; quando a corrotina anterior termina, uma nova pode começar mesmo com excitação zero. Durante a corrotina, [BonerInflatable.OnSizeChanged](Assets/KoboldKare/Scripts/BonerInflatable.cs) reaplica blendshapes e o blend de jiggle. É trabalho redundante possível, ainda sem atribuição de milissegundos. Corrotinas continuam na thread principal: `yield` distribui execução no tempo e não cria uma thread de trabalho.
6. **Jiggle, animação e controlador.** [JiggleRigBuilder.Advance](Packages/com.naelstrof.jigglephysics/Scripts/JiggleRigBuilder.cs) e [JiggleSkin.Advance](Packages/com.naelstrof.jigglephysics/Scripts/JiggleSkin.cs) simulam e aplicam poses sequencialmente. Existe [JiggleRigRendererLOD](Packages/com.naelstrof.jigglephysics/Scripts/JiggleRigRendererLOD.cs), portanto não assumir que todos os rigs continuem ativos fora de visão: confirmar na captura. [CharacterControllerAnimator.Update](Assets/KoboldKare/Scripts/CharacterControllerAnimator.cs) continua atualizando parâmetros/VFX/look-at sem teste de visibilidade. [KoboldCharacterController.FixedUpdate](Assets/KoboldKare/Scripts/KoboldCharacterController.cs) calcula chão com até nove raycasts; `OverlapCapsule` que aloca aparece em `Stuck`, condicionado à tentativa de levantar de um agachamento, e não em todo frame ocioso. Física e avaliação nativa do Animator devem ser medidas separadamente dos callbacks C#.

A [IA de olhar](Assets/KoboldKare/Scripts/KoboldAIPossession.cs) possui pensamento com espera de dois segundos, além de um `LateUpdate` de orientação. Não apareceu evidência estática suficiente para colocá-la acima das curvas neste cenário. O metabolismo já possui instrumentação e agendamento documentados acima; medir seus picos separadamente do custo contínuo.

### Instrumentação adicionada nesta etapa

Somente campos estáticos `ProfilerMarker`, import da API e escopos `.Auto()`; nenhuma alteração de estado, frequência, autoridade Photon ou visibilidade. Os escopos fecham também em retornos antecipados/exceções. Nenhum escopo atravessa um `yield`.

| Marcador | O que mede |
| --- | --- |
| `Penetration.Penetrator.LateUpdate` | Callback completo, incluindo descendentes |
| `Penetration.Penetrator.AutoSeek` | Busca física e avaliação dos candidatos |
| `Penetration.Spline.Rebuild` | Reconstrução por `SetWeightsFromPoints` |
| `Penetration.Spline.DistanceLUT` | Geração da tabela de distância |
| `Penetration.Spline.BinormalLUT` | Geração da tabela de binormais |
| `Penetration.Penetrable.GetPath` | Consulta que refaz a curva do alvo |
| `Penetration.Deformer.Upload` | Empacotamento, upload e property blocks do deformador |
| `Penetration.ProceduralDeformation.Upload` | Dois uploads e property blocks da deformação receptora |
| `Kobold.Ragdoller.LateUpdate` | Atualização dos corpos e interpolação remota |
| `Jiggle.Rig.Advance` | Avanço de um builder de rigs |
| `Jiggle.Skin.Advance` | Avanço de um JiggleSkin, incluindo envio visual |
| `Kobold.Throb.Update` | Cálculo e chamada do inflador por frame |
| `Kobold.BonerInflatable.OnSizeChanged` | Aplicação de blendshapes/jiggle; também aparece sob execução de corrotinas |

Nomes constantes permitem agrupar instâncias sem alocar strings por frame. `Calls` de um marcador pode contar apêndices, curvas ou builders, não necessariamente Kobolds. `Time ms` inclusivo inclui os filhos: não somar um pai aos seus descendentes. Usar `Self ms` para separar trabalho próprio. Os marcadores de upload incluem empacotamento/property blocks, não apenas transferência de buffer.

### Captura necessária para confirmar o maior custo

1. Recompilar uma Development Build com conexão do Profiler; desligar Deep Profile. Manter a mesma configuração de VSync/limite de FPS, resolução e qualidade em todas as amostras. Aguardar carregamento/inicialização e estabilização das simulações.
2. Usar o mesmo save e três quantidades conhecidas de Kobolds, por exemplo N, 2N e 4N, além do cenário vazio. Registrar o número real de personagens e apêndices ativos; não inferi-lo pelos nomes das imagens. Fixar posição/pose, autoridade local/remota e estado em pé/ragdoll. Capturar 300–600 frames por condição, olhando para eles e depois mantendo-os fora de visão.
3. Em CPU Usage, usar Hierarchy para ordenar por `Time ms` e comparar `Self ms`, `Calls` e `GC Alloc` dos marcadores. Usar Timeline para verificar em qual thread o trabalho ocorre. Comparar média/mediana e p95/p99 do frame; manter picos de metabolismo destacados. Só ativar allocation call stacks em uma captura separada se houver alocações relevantes.
4. Para as curvas, comparar `Spline.Rebuild` e `Penetrable.GetPath` por frame, e os filhos de `AutoSeek`. Se os custos de LUT dominarem, priorizar cache/invalidação e reconstruções redundantes. Cache apenas por frame não basta se transforms mudarem dentro do mesmo frame: preservar ordem e correção das consultas.
5. Se uploads dominarem, testar separadamente atualização por alteração de dados e redução de trabalho visual invisível, preservando o caminho de simulação e a atualização ao voltar a aparecer. Se ragdoll dominar, testar aplicação de configuração apenas em transições. Se o inflador repetir chamadas com zero, testar descarte de alvos iguais preservando o contrato de eventos/listeners.
6. Qualquer teste A/B que desative um componente de interação deve usar uma sessão descartável e recuperar o mesmo estado inicial: desativá-lo pode disparar `OnDisable`, remover colliders/listeners e mudar a própria simulação. Não usar essa comparação como único argumento de causalidade. Não mover chamadas de UnityEngine para `Task.Run`; paralelização exige separar dados e respeitar as APIs que precisam da thread principal.

### Validação desta etapa e limites

- Os dez arquivos instrumentados passaram pelo parser Roslyn em C# 9 nos modos Editor e Player (20 verificações).
- Removendo apenas os imports/campos/escopos adicionados, os tokens C# coincidem com os fontes anteriores nos dois modos, confirmando o escopo de instrumentação.
- Um trecho com `ProfilerMarker` e `using var scope = marker.Auto()` compilou em memória contra `UnityEngine.CoreModule.dll` do Unity 2021.3.45f2.
- Verificação de whitespace passou considerando CRLF.
- Não foi executada compilação completa do jogo, Play Mode, nova captura nem medição de ganho. A investigação confirma caminhos contínuos síncronos; o maior consumidor agregado ainda depende da captura instrumentada.
