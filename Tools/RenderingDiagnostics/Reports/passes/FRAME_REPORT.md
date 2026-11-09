# Comparação de passes — Frame Debugger

Capturas diagnósticas no Editor, separadas dos tempos de player. Eventos, draw calls e Renderers são grandezas diferentes.

| Passe | Eventos A | Eventos B | Draws conhecidos A | Draws conhecidos B |
|---|---:|---:|---:|---:|
| DrawOpaqueObjects | 18 | 87 | 53 | 289 |
| DrawTransparentObjects | 1 | 2 | 1 | 2 |
| MainLightShadow | 50 | 97 | 342 | 397 |
| MeshSkinning.GPUSkinning/MeshSkinning.SkinOnGPU/ComputeSkinningDispatch | 0 | 5 | 0 | 0 |
| UGUI.Rendering.RenderOverlays | 1 | 1 | 0 | 0 |
| UGUI.Rendering.RenderOverlays/Canvas.RenderOverlays | 3 | 3 | 3 | 3 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer | 3 | 3 | 0 | 0 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/Camera.RenderSkybox | 1 | 1 | 1 | 1 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/ColorGradingLUT | 1 | 1 | 1 | 1 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/CopyColor | 1 | 1 | 1 | 1 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/CopyDepth | 1 | 1 | 1 | 1 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/Render PostProcessing Effects/SMAA | 5 | 5 | 3 | 3 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/Render PostProcessing Effects/UberPostProcess | 1 | 1 | 1 | 1 |
| UniversalRenderPipeline.RenderSingleCamera: OrbitCamera/ScriptableRenderer.Execute: URPForwardRenderer/Render PostProcessing Effects/UberPostProcess/Bloom | 16 | 16 | 16 | 16 |

Draws conhecidos somam somente eventos com dados válidos. Campos ausentes não contam como zero draws reais.

[Diff verificável de associações individuais](confirmed-single-draw-diff.csv). A lista é **parcial**: eventos SRP com múltiplos draws não expõem necessariamente todos os Renderers. O único objeto associado não representa todo o batch.

Os CSVs de eventos preservam índices, passe, shader, keywords e motivo de quebra. Não foi inferida a participação de um objeto por semelhança de mesh/material ou por `isVisible`.
