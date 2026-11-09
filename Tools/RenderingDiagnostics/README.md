# Captura A/B de renderização

Instrumentação opt-in para Unity 2021.3.45f2 / URP 12.1.15. Não modifica a cena, o bake ou materiais compartilhados. O código runtime só existe no Editor e em Development Build.

## Executar

1. Deixar o Unity terminar a compilação. Executar **Tools > Rendering diagnostics > Audit saved MainMap** fora do Play Mode. O auditor recusa uma MainMap com alterações não salvas; não salva nem descarta edições. A cena já salva pode conter quaisquer alterações locais.
2. Executar **Tools > Rendering diagnostics > Build Development capture player**. A ferramenta exige Windows x86_64 ativo e cenas abertas sem alterações não salvas. Reconstrói Addressables para evitar medir bundles de outra revisão da cena/bake, cria Development Build com Frame Timing Stats e restaura as configurações e os bytes anteriores dos arquivos preservados.
3. Copiar [capture.example.json](capture.example.json), escolher uma pasta de saída nova e executar na raiz do projeto:

```powershell
& ./Builds/RenderingDiagnostics/KoboldKare.exe --kk-render-capture ./Tools/RenderingDiagnostics/capture.example.json -logFile ./Builds/RenderingDiagnostics/player.log
```

4. Ao terminar, gerar o relatório:

```powershell
python Tools/RenderingDiagnostics/analyze.py Builds/RenderingDiagnostics/Captures/reference --saved-scene Tools/RenderingDiagnostics/Reports/saved-scene.json
```

O modo `suite` mede a matriz de quatro configurações, repete seus extremos em ordem inversa, percorre 17 amostras (ou mais) nos dois sentidos para posição+rotação, somente posição e somente rotação, refina o maior salto de cada percurso/sentido e registra quatro poses adicionais. `matrix` executa somente a matriz. Não confundir capturas diagnósticas com tentativas corretivas.

O início Vanilla aguarda o ModManager, desativa os mods em memória e carrega MainMap pelo fluxo single player normal. Uma trava recusa capturas com assets de mods carregados. Não salva preferências de mods. Mantém resolução, nível de qualidade, FOV, VSync=0 e targetFrameRate=-1. OrbitCamera permanece pausada durante a captura, incluindo suas corrotinas de tween. A posição/rotação anterior, pausa, VSync, cap, resolução, URP e occlusion são restaurados no término, falha ou desativação do componente. O asset URP utilizado no experimento é uma cópia runtime.

**Limitadores externos:** desativar também RTSS/driver durante a referência e intervenções. VSync=0 e targetFrameRate=-1 não anulam um cap externo. Foram encontrados 90 FPS no perfil global do RTSS e 60 FPS no perfil KoboldKare nesta máquina. As execuções iniciais de player com esse limite foram interrompidas e não qualificam frame time sem cap.

`verticalFov` e `cameraAspect` permitem reproduzir o mesmo frustum em inspeções separadas no Editor; valores negativos preservam os valores do jogo. `shadowDistanceOverride` (negativo = preservar) e `shadowCascadesOverride` (0 = preservar) permitem intervenções explícitas na cópia temporária do URP. Uma intervenção só conta como tentativa quando for seguida de validação; mudar esses campos não constitui aprovação automática da qualidade visual.

## Artefatos e interpretação

- `manifest.json`: ambiente, poses, resolução, qualidade, câmeras, URP, counters disponíveis e seus limites.
- `frames.csv`: valores por frame concluído; contadores indisponíveis usam `-1` e FrameTiming indisponível fica vazio. CPU/render thread vêm de marcadores disponíveis, não são tempo exclusivo de submissão. Não comparar tempos medidos no Editor com tempos de player.
- `*-inventory.json`: inventário atualizado a cada frame de observação, incluindo instâncias criadas em runtime, materiais compartilhados/IDs/keywords, mesh, lightmap, MPB, bounds e batching estático.
- `region-runtime.json`: eventos de carga/descarga e mudanças observadas de FarmRegion/ancestrais nos pontos de amostragem. Não captura uma transformação transitória que aconteça e seja revertida entre observações.
- `transitions.csv`: limites refinados; campos sem cabeçalho: percurso, sentido, t mínimo, t máximo, batches mínimo/máximo e SetPass mínimo/máximo. Um salto **não prova** troca de célula. Comparar repetições para detectar variação temporal.
- `analysis/summary.csv`: mediana, p95, mínimo/máximo por ponto. A matriz usa a média das medianas dos extremos nas duas ordens.
- `analysis/isolation.json`: contribuições condicionais e interação `original − sem oclusão − sem sombras + sem ambos`. Frame time é uma observação empírica, não uma soma linear garantida de custos.
- `analysis/*-diff.csv` e `candidate-groups.json`: A−B/B−A agrupados por nome, material e shader, separados por tipo de observação.
- `analysis/*-rocks.csv`: inventário de rocks inclusive invisíveis em ambas as poses; IDs de material/mesh só valem na mesma execução.

`Renderer.isVisible` é uma observação agregada de quaisquer câmeras e pode incluir sombras. O teste de frustum só classifica bounds candidatos. **Nenhum dos dois confirma um draw, um passe ou contribuição aos contadores globais.** Inventário, escrita de arquivos, capturas de tela e enumeração de Renderers acontecem fora dos frames cronometrados.

## Frame Debugger

Em Play Mode, os modos `holdA` e `holdB` travam a pose para inspeção separada. Não medir desempenho com o Frame Debugger habilitado.

O Editor oferece comandos locais fixos, recebidos por `Temp/RenderCapture-request.json`. O resultado aparece em `Temp/RenderCapture-response.json`; verificar `error` antes de prosseguir. Esses comandos não avaliam código e não expõem porta de rede.

```json
{"command":"play","config":"Temp/holdA.json"}
```

Esperar o status `holdA` da captura, então enviar separadamente:

```json
{"command":"fd-enable"}
```

Esperar pelo menos um frame renderizado e exportar:

```json
{"command":"fd-export","output":"Builds/RenderingDiagnostics/frame-A.json"}
```

A exportação seleciona cada evento e aguarda a leitura nativa assíncrona. Esperar o arquivo `.json.status.txt` indicar `exported N/N`; verificar a quantidade de eventos com dados válidos e qualquer `.error.txt`. Apenas chamar `GetFrameEventData` em um laço sem selecionar/reproduzir cada evento retorna dados incompletos nesta versão do Unity.

Depois `{"command":"fd-disable"}` e `{"command":"stop"}`. Repetir na pose B. `capture` inicia uma captura dentro do Play Mode; `status`, `audit`, `build` e `frame-api` completam os comandos disponíveis.

Para alternar sem reiniciar a sessão, desativar o Frame Debugger e enviar `{"command":"hold-pose","pose":"B"}`. Esperar estabilizar e habilitar/exportar novamente.

O adaptador usa reflection sobre a API interna da versão instalada: registra a disponibilidade e correspondência do índice dos dados de cada evento. Inclui draws por evento, shader/pass/light mode, keywords, meshes, motivo de quebra de batch e Renderer associado quando exposto. Um evento SRP pode conter vários draws e revelar somente um objeto associado. A exportação marca esses casos como incompletos: **não atribuir todos os draws do evento ao único objeto exposto nem promover Renderers com mesh semelhante a membros confirmados**. Para completar a lista individual, inspecionar os membros do evento na ferramenta de depuração disponível; conservar as limitações se a API não os expuser.

Gerar o comparativo de passes e a lista parcial de associações individuais:

```powershell
python Tools/RenderingDiagnostics/frame_report.py Tools/RenderingDiagnostics/Reports/frame-A-detailed.json Tools/RenderingDiagnostics/Reports/frame-B-detailed.json --output Tools/RenderingDiagnostics/Reports/passes
```

Campos de draw/shader em eventos de clear/compute podem ser irrelevantes na API interna. O relatório só soma draw calls para tipos de evento de desenho conhecidos. A contagem de eventos e a contagem de draws permanecem separadas.

Para comparar uma intervenção validada nas mesmas condições:

```powershell
python Tools/RenderingDiagnostics/compare.py Builds/RenderingDiagnostics/Captures/reference Builds/RenderingDiagnostics/Captures/attempt-1 --output Tools/RenderingDiagnostics/Reports/attempt-1
./Tools/RenderingDiagnostics/Plot.ps1 -Summary Builds/RenderingDiagnostics/Captures/reference/analysis/summary.csv -Output Tools/RenderingDiagnostics/Reports/curve-reference.png
```

O comparador exige capturas completas e rejeita diferenças de ambiente, resolução, FOV/aspecto, qualidade ou cenas. Isso não detecta limitadores externos nem substitui a inspeção visual de popping, aberturas e sombras.

## Experimentos reversíveis de bake

Exigem uma única MainMap salva aberta fora do Play Mode. A pasta de backup precisa estar vazia. A ferramenta copia os bytes da cena, do bake e de seu meta antes de alterar parâmetros.

```json
{"command":"bake-experiment","output":"Builds/RenderingDiagnostics/BakeBackups/test-3","smallestOccluder":3,"smallestHole":0.25}
```

O bake usa `GenerateInBackground`; consultar `status.txt` na pasta de backup. `{"command":"cancel-bake"}` cancela e restaura os arquivos originais. Falhas nativas de geração também restauram os arquivos. Não executar outro comando de alteração nem recompilar scripts enquanto esse bake está em andamento. Após sucesso, auditar e reconstruir Addressables antes de comparar o player.

Para rejeitar um bake concluído:

```json
{"command":"restore-bake","output":"Builds/RenderingDiagnostics/BakeBackups/test-3"}
```

Um teste global com Smallest Occluder=1,5 e Smallest Hole=0,2 gerou 916.134 tiles nesta cena e falhou em `TomeGenerator` por falta de memória. Isso é uma limitação desse experimento, sem demonstrar defeito do bake original. `Compute()` síncrono também bloqueia a UI durante esse trabalho; a ferramenta passou a usar geração em segundo plano.

## Validação do analisador

```powershell
python -m unittest discover -s Tools/RenderingDiagnostics -p 'test_*.py' -v
```

Os testes cobrem novas instâncias no diff, candidatos de frustum sem observação de visibilidade, exclusão de objetos inativos, GPU ausente, p95 e sinal/condicionamento da interação da matriz. Compilação e execução no Unity são necessárias para validar a integração real.
