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
