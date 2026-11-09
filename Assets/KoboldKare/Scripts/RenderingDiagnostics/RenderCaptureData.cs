#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KoboldKare.RenderingDiagnostics {
    [Serializable]
    public sealed class RenderCaptureConfig {
        public string outputDirectory = "";
        public int width = 1280, height = 720;
        public int settleFrames = 60, measureFrames = 90, observationFrames = 4;
        public int samples = 17, bisections = 5;
        public bool startVanilla = true, quitWhenDone = true, screenshots = true;
        public float verticalFov = -1, cameraAspect = -1, shadowDistanceOverride = -1;
        public int shadowCascadesOverride = 0;
        public string mode = "suite"; // suite, matrix, holdA, holdB
        public Vector3 positionA = new Vector3(-166.19f, 19.63f, 321.10f);
        public Vector3 rotationA = new Vector3(6.80f, 78.60f, 0);
        public Vector3 positionB = new Vector3(-167.63f, 19.62f, 325.20f);
        public Vector3 rotationB = new Vector3(10.40f, 71.60f, 0);
    }

    [Serializable]
    public sealed class CaptureMaterial {
        public int id;
        public string name, asset, shader;
        public string[] keywords;
        public bool instancing;
        public int renderQueue, passCount;
    }

    [Serializable]
    public sealed class CaptureRenderer {
        public int id, lightmapIndex, realtimeLightmapIndex, staticBatchIndex, layer;
        public string key, path, scene, name, type, mesh, meshAsset;
        public int meshId, subMeshes, vertices;
        public bool enabled, active, isVisibleObserved, frustumCandidate, staticBatch, propertyBlock;
        public bool occluderStatic, occludeeStatic, staticFlagsAvailable;
        public string shadowCasting, receiveShadows, staticFlags, asset;
        public Bounds bounds, meshBounds;
        public Vector4 lightmapScaleOffset;
        public CaptureMaterial[] materials;
    }

    [Serializable]
    public sealed class CaptureTransform {
        public string key;
        public int id;
        public Vector3 localPosition, localScale, position, lossyScale;
        public Quaternion localRotation, rotation;
    }

    [Serializable]
    public sealed class CaptureInventory {
        public string label, evidence = "Visibility observations and frustum candidates; NOT confirmed camera or shadow draws.";
        public int frame;
        public List<CaptureRenderer> renderers = new List<CaptureRenderer>();
        public List<CaptureTransform> region = new List<CaptureTransform>();
    }

    public static class RenderCaptureData {
        public static string PathOf(Transform t) {
            var parts = new List<string>();
            while (t != null) {
                parts.Add(t.name + "[" + t.GetSiblingIndex() + "]");
                t = t.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static string Key(Component c) => c.gameObject.scene.path + "::" + PathOf(c.transform);

        public static CaptureTransform TransformOf(Transform t) => new CaptureTransform {
            key = Key(t), id = t.GetInstanceID(), localPosition = t.localPosition,
            localRotation = t.localRotation, localScale = t.localScale, position = t.position,
            rotation = t.rotation, lossyScale = t.lossyScale
        };

        public static CaptureRenderer RendererOf(Renderer r, Plane[] planes = null) {
            var filter = r.GetComponent<MeshFilter>();
            var mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh : filter ? filter.sharedMesh : null;
            var materials = r.sharedMaterials; // Never use .material/.materials: those instantiate assets.
            var record = new CaptureRenderer {
                id = r.GetInstanceID(), key = Key(r) + "::" + r.GetType().Name + "[" + Array.IndexOf(r.GetComponents<Renderer>(), r) + "]",
                path = PathOf(r.transform), scene = r.gameObject.scene.path, name = r.name,
                type = r.GetType().Name, enabled = r.enabled, active = r.gameObject.activeInHierarchy,
                isVisibleObserved = r.isVisible, frustumCandidate = planes != null && GeometryUtility.TestPlanesAABB(planes, r.bounds),
                layer = r.gameObject.layer, bounds = r.bounds, staticBatch = r.isPartOfStaticBatch,
                staticBatchIndex = -1, propertyBlock = r.HasPropertyBlock(),
                lightmapIndex = r.lightmapIndex, realtimeLightmapIndex = r.realtimeLightmapIndex,
                lightmapScaleOffset = r.lightmapScaleOffset, shadowCasting = r.shadowCastingMode.ToString(),
                receiveShadows = r.receiveShadows.ToString(), mesh = mesh ? mesh.name : "",
                meshId = mesh ? mesh.GetInstanceID() : 0, meshBounds = mesh ? mesh.bounds : default,
                subMeshes = mesh ? mesh.subMeshCount : 0, vertices = mesh ? mesh.vertexCount : 0,
                materials = new CaptureMaterial[materials.Length], staticFlags = "unavailable-in-player"
            };
            for (int i = 0; i < materials.Length; i++) {
                var m = materials[i];
                if (!m) continue;
                var keywords = m.shaderKeywords;
                Array.Sort(keywords, StringComparer.Ordinal);
                record.materials[i] = new CaptureMaterial {
                    id = m.GetInstanceID(), name = m.name, shader = m.shader ? m.shader.name : "",
                    keywords = keywords, instancing = m.enableInstancing, renderQueue = m.renderQueue, passCount = m.passCount
                };
#if UNITY_EDITOR
                record.materials[i].asset = UnityEditor.AssetDatabase.GetAssetPath(m);
#endif
            }
#if UNITY_EDITOR
            var flags = UnityEditor.GameObjectUtility.GetStaticEditorFlags(r.gameObject);
            record.staticFlags = flags.ToString();
            record.staticFlagsAvailable = true;
            record.occluderStatic = (flags & UnityEditor.StaticEditorFlags.OccluderStatic) != 0;
            record.occludeeStatic = (flags & UnityEditor.StaticEditorFlags.OccludeeStatic) != 0;
            record.asset = UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(r).ToString();
            record.meshAsset = UnityEditor.AssetDatabase.GetAssetPath(mesh);
#endif
            return record;
        }

        public static Renderer[] LoadedRenderers() {
            // Include inactive and runtime-created objects; exclude assets and preview resources.
            var result = new List<Renderer>();
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>()) {
                if (r && r.gameObject.scene.IsValid() && r.gameObject.scene.isLoaded)
                    result.Add(r);
            }
            return result.ToArray();
        }

        public static List<CaptureTransform> RegionTransforms() {
            var result = new List<CaptureTransform>();
            var seen = new HashSet<int>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>()) {
                if (!t || !t.gameObject.scene.IsValid() || !t.gameObject.scene.isLoaded || t.name != "FarmRegion") continue;
                for (var parent = t; parent; parent = parent.parent) {
                    if (seen.Add(parent.GetInstanceID())) result.Add(TransformOf(parent));
                }
            }
            return result;
        }

        public static void WriteJson(string path, object value) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            File.WriteAllText(path, JsonUtility.ToJson(value, true));
        }
    }
}
#endif
