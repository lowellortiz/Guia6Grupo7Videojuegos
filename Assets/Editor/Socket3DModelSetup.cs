using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

/// <summary>
/// Sustituye la geometria primitiva de la escena servidor por los modelos 3D del proyecto:
/// Leafeon como visual del objeto controlado por red, y caja1 como visual de los objetos cogibles.
///
/// Criterio de diseno: los cubos (RemotePlayer, Box_A/B/C) NO se tocan como GameObjects.
/// Conservan su Collider, su Rigidbody y su GrabbableObject -- que es lo que RemoteObjectServer
/// busca con Physics.OverlapSphere -- y solo se les desactiva el MeshRenderer. El modelo entra
/// como hijo puramente visual. Asi no se rompe ninguna referencia del Inspector.
///
/// Menu: Tools > Sockets > Modelos 3D
/// - "1. Configurar pipeline 3D (URP)": el proyecto venia con el Renderer 2D, que dibuja los
///   materiales Lit sin iluminacion ni sombras. Crea un Universal Renderer 3D y lo deja activo.
/// - "2. Preparar materiales de los modelos": extrae los materiales embebidos de Leafeon y crea
///   el material de la caja con su textura.
/// - "3. Aplicar modelos a la escena servidor": limpia las instancias sueltas y mete los visuales.
///
/// Los pasos 1 y 2 tocan ASSETS y NO los cubre Ctrl+Z. Haz commit de git antes de ejecutarlos.
/// El paso 3 solo toca la escena y si es deshacible.
///
/// Ajuste fino: tras aplicar, puedes mover/rotar Visual_Leafeon y Visual_Caja libremente en el
/// Inspector; son hijos visuales y no afectan a fisica ni a red. Pero volver a ejecutar el paso 3
/// los borra y recrea, perdiendo esos ajustes.
/// </summary>
public static class Socket3DModelSetup
{
    private const string ServerScene = "Assets/Scenes/SocketWorld3D_Server.unity";

    // OJO: la extension de Leafeon es .FBX en MAYUSCULAS.
    private const string LeafeonModel = "Assets/Modelos3D/Leafeon/Leafeon.FBX";
    private const string CajaModel = "Assets/Modelos3D/caja1/source/caja1.fbx";
    private const string CajaTexture = "Assets/Modelos3D/caja1/textures/Piskel-blender.png";

    private const string MaterialsDir = "Assets/Materials";
    private const string PrefabsDir = "Assets/Prefabs";
    private const string UrpAssetPath = "Assets/Settings/UniversalRP.asset";
    private const string Renderer3DPath = "Assets/Settings/UniversalRenderer3D.asset";
    private const string CajaPrefabPath = "Assets/Prefabs/Caja_Grabbable.prefab";

    private const string VisualLeafeon = "Visual_Leafeon";
    private const string VisualCaja = "Visual_Caja";

    private static readonly string[] BoxNames = { "Box_A", "Box_B", "Box_C" };

    // --- Ajustes del usuario -------------------------------------------------
    // Si Leafeon se ve enano o gigante junto a las cajas, este es el unico numero a tocar.
    private const float PlayerTargetHeight = 1.0f;
    // El modelo mira a -Z de fabrica y el servidor avanza hacia +Z. Si camina de espaldas, pon 0.
    private const float LeafeonYaw = 180f;
    // Convencion Z-up de Blender. Si la textura de la caja sale de lado, toca esto.
    private static readonly Vector3 CajaEuler = new Vector3(-90f, 0f, 0f);
    // -------------------------------------------------------------------------

    private enum FitMode { Center, SnapToFloor }

    // =========================================================================
    // Paso 1: pipeline 3D
    // =========================================================================

    [MenuItem("Tools/Sockets/Modelos 3D/1. Configurar pipeline 3D (URP)", priority = 60)]
    public static void ConfigurePipeline3D()
    {
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
        if (urp == null)
        {
            Debug.LogError($"[Sockets] No se encontro {UrpAssetPath}.");
            return;
        }

        if (GetRendererDataAt(urp, 0) is UniversalRendererData existing)
        {
            Debug.Log($"[Sockets] El pipeline ya usa un Universal Renderer 3D ({AssetDatabase.GetAssetPath(existing)}).");
            return;
        }

        // Crea el UniversalRendererData resolviendo postProcessData y recursos internos,
        // y lo deja en m_RendererDataList[0]. Un CreateInstance a pelo se saltaria eso.
        var data = urp.LoadBuiltinRendererData(RendererType.UniversalRenderer);
        if (data == null)
        {
            Debug.LogError("[Sockets] No se pudo crear el Universal Renderer 3D. " +
                           "Hazlo a mano: Assets > Create > Rendering > URP Universal Renderer, " +
                           $"guardalo en {Renderer3DPath} y ponlo en el indice 0 de la Renderer List de {UrpAssetPath}.");
            return;
        }

        EditorUtility.SetDirty(urp);
        AssetDatabase.SaveAssets();

        // La referencia es por GUID, asi que mover el asset no la rompe.
        var created = AssetDatabase.GetAssetPath(data);
        if (!string.IsNullOrEmpty(created) && created != Renderer3DPath)
        {
            var error = AssetDatabase.MoveAsset(created, Renderer3DPath);
            if (!string.IsNullOrEmpty(error))
                Debug.LogWarning($"[Sockets] El renderer se creo en {created} pero no se pudo mover: {error}");
        }

        // Por si LoadBuiltinRendererData no lo dejase en el indice 0.
        EnsureRendererAtIndexZero(urp, data);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Sockets] Pipeline 3D listo: {AssetDatabase.GetAssetPath(data)}. " +
                  "El Plane deberia mostrar ahora sombreado e iluminacion direccional. " +
                  "Si sigue plano, fuerza un recompilado (toca cualquier .cs). " +
                  "Renderer2D.asset se conserva: lo usan los templates 2D.");
    }

    private static ScriptableRendererData GetRendererDataAt(UniversalRenderPipelineAsset urp, int index)
    {
        // Via SerializedObject: m_RendererDataList es internal y la propiedad publica cambia de
        // forma entre versiones de URP.
        var list = new SerializedObject(urp).FindProperty("m_RendererDataList");
        if (list == null || index >= list.arraySize) return null;
        return list.GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
    }

    private static void EnsureRendererAtIndexZero(UniversalRenderPipelineAsset urp, ScriptableRendererData data)
    {
        var so = new SerializedObject(urp);
        var list = so.FindProperty("m_RendererDataList");
        if (list == null) return;

        if (list.arraySize == 0) list.InsertArrayElementAtIndex(0);
        if (list.GetArrayElementAtIndex(0).objectReferenceValue != data)
            list.GetArrayElementAtIndex(0).objectReferenceValue = data;

        var defaultIndex = so.FindProperty("m_DefaultRendererIndex");
        if (defaultIndex != null) defaultIndex.intValue = 0;

        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // =========================================================================
    // Paso 2: materiales
    // =========================================================================

    [MenuItem("Tools/Sockets/Modelos 3D/2. Preparar materiales de los modelos", priority = 61)]
    public static void PrepareMaterials()
    {
        if (!ModelExists(LeafeonModel) || !ModelExists(CajaModel)) return;

        SocketProjectSetup.CreateFolders();

        // Leafeon: los materiales embebidos ya son URP/Lit con sus texturas (el importador de URP
        // los convierte). Extraerlos respeta el orden de submallas Body/Eye/Mouth; crear .mat
        // nuevos obligaria a acertar ese orden a mano en cada reimport.
        ExtractEmbeddedMaterials(LeafeonModel, MaterialsDir);
        RenameLeafeonMaterials();

        // Caja: su material embebido no tiene textura y la textura esta suelta, asi que aqui si
        // creamos el material y lo remapeamos sobre el FBX.
        ConfigurePixelArtTexture(CajaTexture);
        var cajaMat = CreateOrUpdateLitMaterial("Caja1_Lit", CajaTexture);
        if (cajaMat != null) RemapModelMaterial(CajaModel, cajaMat);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Sockets] Materiales preparados en {MaterialsDir}. " +
                  "Revisa que Leafeon_Body tenga su Base Map asignado.");
    }

    private static void ExtractEmbeddedMaterials(string modelPath, string destFolder)
    {
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Sockets] {modelPath} no es un modelo importable.");
            return;
        }

        var remapped = importer.GetExternalObjectMap();
        var extracted = 0;

        foreach (var mat in AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>())
        {
            var id = new AssetImporter.SourceAssetIdentifier(mat);
            if (remapped.ContainsKey(id)) continue; // ya extraido antes

            var dest = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(destFolder, mat.name + ".mat").Replace('\\', '/'));

            var error = AssetDatabase.ExtractAsset(mat, dest);
            if (string.IsNullOrEmpty(error)) extracted++;
            else Debug.LogWarning($"[Sockets] No se pudo extraer el material '{mat.name}': {error}");
        }

        if (extracted == 0) return;

        AssetDatabase.WriteImportSettingsIfDirty(modelPath);
        AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
        Debug.Log($"[Sockets] {extracted} material(es) extraidos de {Path.GetFileName(modelPath)}.");
    }

    /// <summary>
    /// Los materiales del FBX se llaman "Material #11/#12/#13". Renombrarlos es seguro: el remap
    /// del .meta guarda fileID+guid, no la ruta.
    /// </summary>
    private static void RenameLeafeonMaterials()
    {
        var importer = AssetImporter.GetAtPath(LeafeonModel) as ModelImporter;
        if (importer == null) return;

        var renamed = false;
        foreach (var entry in importer.GetExternalObjectMap())
        {
            var mat = entry.Value as Material;
            if (mat == null) continue;

            var target = LeafeonNameFor(mat);
            if (target == null || mat.name == target) continue;

            var path = AssetDatabase.GetAssetPath(mat);
            if (string.IsNullOrEmpty(path)) continue;
            if (AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsDir}/{target}.mat") != null) continue;

            var error = AssetDatabase.RenameAsset(path, target);
            if (string.IsNullOrEmpty(error)) renamed = true;
            else Debug.LogWarning($"[Sockets] No se pudo renombrar '{mat.name}': {error}");
        }

        if (renamed) AssetDatabase.SaveAssets();
    }

    private static string LeafeonNameFor(Material mat)
    {
        var texture = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : null;
        var hint = (texture != null ? texture.name : mat.name).ToLowerInvariant();

        if (hint.Contains("body")) return "Leafeon_Body";
        if (hint.Contains("eye")) return "Leafeon_Eye";
        if (hint.Contains("mouth")) return "Leafeon_Mouth";
        return null;
    }

    private static void ConfigurePixelArtTexture(string texturePath)
    {
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null)
        {
            Debug.LogWarning($"[Sockets] No se encontro la textura {texturePath}.");
            return;
        }

        var dirty = false;
        if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; dirty = true; }
        if (importer.textureCompression != TextureImporterCompression.Uncompressed)
        {
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            dirty = true;
        }
        if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }

        if (dirty)
        {
            importer.SaveAndReimport();
            Debug.Log($"[Sockets] {Path.GetFileName(texturePath)} reimportada como pixel art (Point, sin comprimir).");
        }
    }

    private static Material CreateOrUpdateLitMaterial(string name, string texturePath)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogError("[Sockets] No se encontro el shader 'Universal Render Pipeline/Lit'.");
            return null;
        }

        var path = $"{MaterialsDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        // URP usa _BaseMap/_BaseColor, no los _MainTex/_Color del pipeline built-in.
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", 0.1f);
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>
    /// Remapea el material sobre el propio FBX para que cualquier instancia salga bien,
    /// en vez de dejar un override de material por cada instancia en la escena.
    /// </summary>
    private static void RemapModelMaterial(string modelPath, Material mat)
    {
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null) return;

        var changed = false;
        foreach (var embedded in AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Material>())
        {
            if (embedded == mat) continue;
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(embedded), mat);
            changed = true;
        }

        if (!changed) return;
        AssetDatabase.WriteImportSettingsIfDirty(modelPath);
        importer.SaveAndReimport();
        Debug.Log($"[Sockets] {Path.GetFileName(modelPath)} remapeado a {mat.name}.");
    }

    // =========================================================================
    // Paso 3: escena
    // =========================================================================

    [MenuItem("Tools/Sockets/Modelos 3D/3. Aplicar modelos a la escena servidor", priority = 62)]
    public static void ApplyModelsToServerScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Sockets] Sal del modo Play antes de aplicar los modelos.");
            return;
        }

        if (!ModelExists(LeafeonModel) || !ModelExists(CajaModel)) return;
        if (!TryGetServerScene(out var scene)) return;

        var player = FindRoot(scene, "RemotePlayer");
        if (player == null)
        {
            Debug.LogError("[Sockets] No se encontro 'RemotePlayer' en la escena servidor. No se guardo nada.");
            return;
        }

        var boxes = new List<GameObject>();
        foreach (var name in BoxNames)
        {
            var box = FindRoot(scene, name);
            if (box == null)
            {
                Debug.LogError($"[Sockets] No se encontro '{name}' en la escena servidor. No se guardo nada.");
                return;
            }
            boxes.Add(box);
        }

        var group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Aplicar modelos 3D a la escena servidor");

        RemoveStrayModelInstances(scene);
        NormalizeRemotePlayerScale(player);

        SetupVisual(player, LeafeonModel, VisualLeafeon, FitMode.SnapToFloor,
                    PlayerTargetHeight, Quaternion.Euler(0f, LeafeonYaw, 0f));

        foreach (var box in boxes)
            SetupVisual(box, CajaModel, VisualCaja, FitMode.Center, 1f, Quaternion.Euler(CajaEuler));

        Undo.CollapseUndoOperations(group);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[Sockets] Modelos aplicados y escena guardada. " +
                  "Puedes ajustar Visual_Leafeon y Visual_Caja en el Inspector, pero volver a " +
                  "ejecutar este paso los recrea y pierde esos ajustes.");

        var hold = player.transform.Find("HoldPoint");
        if (hold != null)
        {
            Debug.LogWarning($"[Sockets] Aviso (comportamiento preexistente, no se toco): la caja cogida " +
                             $"acaba a ~{hold.localPosition.z + 1f:0.0} m del jugador, porque TryGrab() la " +
                             "coloca en localPosition = Vector3.forward respecto a HoldPoint " +
                             $"(z = {hold.localPosition.z:0.0}). Si la quieres pegada, pon " +
                             "HoldPoint.localPosition = (0, 0.5, 0.4).");
        }
    }

    private static void RemoveStrayModelInstances(Scene scene)
    {
        // Las instancias sueltas de Leafeon y caja1 que quedaron en la raiz. Al destruir la raiz
        // se van con ella el MeshRenderer huerfano y el BoxCollider fantasma que el Leafeon suelto
        // tenia en el origen -- ese collider lo estaba viendo Physics.OverlapSphere.
        var stray = scene.GetRootGameObjects()
            .Where(root => IsInstanceOfModel(root, LeafeonModel) || IsInstanceOfModel(root, CajaModel))
            .ToList();

        foreach (var root in stray)
        {
            Debug.Log($"[Sockets] Eliminando instancia suelta '{root.name}' de la raiz de la escena.");
            Undo.DestroyObjectImmediate(root);
        }
    }

    /// <summary>
    /// RemotePlayer venia con escala (1, 1, 1.5). Eso estira cualquier hijo en Z y, peor, la caja
    /// cogida se parenta a HoldPoint y sufre cizallamiento al girar el jugador (escala no uniforme
    /// + rotacion del padre). Se normaliza a (1,1,1) compensando el collider y el HoldPoint, de
    /// forma que la geometria de mundo y el comportamiento fisico quedan identicos.
    /// </summary>
    private static void NormalizeRemotePlayerScale(GameObject player)
    {
        var t = player.transform;
        var old = t.localScale;
        if (Mathf.Approximately(old.x, old.y) && Mathf.Approximately(old.y, old.z)) return;

        var box = player.GetComponent<BoxCollider>();
        if (box != null)
        {
            Undo.RecordObject(box, "Compensar collider del jugador");
            box.size = Vector3.Scale(box.size, old);
            box.center = Vector3.Scale(box.center, old);
        }

        var hold = t.Find("HoldPoint");
        if (hold != null)
        {
            Undo.RecordObject(hold, "Compensar HoldPoint");
            hold.localPosition = Vector3.Scale(hold.localPosition, old);
            hold.localScale = Vector3.one;
        }

        Undo.RecordObject(t, "Normalizar escala del jugador");
        t.localScale = Vector3.one;

        Debug.Log($"[Sockets] RemotePlayer normalizado de {old} a (1,1,1); collider y HoldPoint compensados " +
                  "para conservar la misma geometria de mundo.");
    }

    private static void SetupVisual(GameObject parent, string modelPath, string visualName,
                                    FitMode mode, float targetLocalHeight, Quaternion localRotation)
    {
        // Idempotencia: borrar cualquier visual previo antes de crear el nuevo.
        var previous = new List<GameObject>();
        foreach (Transform child in parent.transform)
        {
            if (child.name == visualName || IsInstanceOfModel(child.gameObject, modelPath))
                previous.Add(child.gameObject);
        }
        foreach (var go in previous) Undo.DestroyObjectImmediate(go);

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (asset == null)
        {
            Debug.LogError($"[Sockets] No se pudo cargar {modelPath}.");
            return;
        }

        // InstantiatePrefab y no Object.Instantiate: Leafeon tiene ~96 nodos de hueso y una copia
        // desconectada los volcaria literalmente dentro del .unity.
        var visual = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent.transform);
        Undo.RegisterCreatedObjectUndo(visual, "Anadir visual 3D");

        visual.name = visualName;
        var vt = visual.transform;
        vt.localPosition = Vector3.zero;
        vt.localRotation = localRotation;
        vt.localScale = Vector3.one;

        // Escala uniforme calculada por bounds: inmune al ScaleFactor del importador.
        if (TryWorldBounds(visual, out var bounds))
        {
            var targetWorldY = targetLocalHeight * parent.transform.lossyScale.y;
            vt.localScale = Vector3.one * (targetWorldY / bounds.size.y);
        }
        else
        {
            Debug.LogWarning($"[Sockets] No se pudieron medir los bounds de '{visualName}'. " +
                             "Se deja escala 1; ajustala a mano en el Inspector.");
        }

        if (TryWorldBounds(visual, out bounds))
            vt.position += AlignDelta(parent, bounds, mode);

        var renderer = parent.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.enabled)
        {
            Undo.RecordObject(renderer, "Ocultar cubo primitivo");
            renderer.enabled = false;
        }

        Debug.Log($"[Sockets] {parent.name}: visual '{visualName}' aplicado (escala {vt.localScale.x:0.000}).");
    }

    private static Vector3 AlignDelta(GameObject parent, Bounds bounds, FitMode mode)
    {
        var origin = parent.transform.position;
        if (mode != FitMode.SnapToFloor) return origin - bounds.center;

        // Apoyar el modelo sobre la base real del collider del jugador.
        var box = parent.GetComponent<BoxCollider>();
        var floorY = box != null
            ? box.bounds.min.y
            : origin.y - 0.5f * parent.transform.lossyScale.y;

        return new Vector3(origin.x - bounds.center.x, floorY - bounds.min.y, origin.z - bounds.center.z);
    }

    // =========================================================================
    // Atajo y extra opcional
    // =========================================================================

    [MenuItem("Tools/Sockets/Modelos 3D/Aplicar todo (1+2+3)", priority = 80)]
    public static void ApplyAll()
    {
        ConfigurePipeline3D();
        PrepareMaterials();
        ApplyModelsToServerScene();
    }

    [MenuItem("Tools/Sockets/Modelos 3D/Opcional: crear prefab de caja", priority = 100)]
    public static void CreateBoxPrefab()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[Sockets] Sal del modo Play.");
            return;
        }
        if (!TryGetServerScene(out var scene)) return;

        SocketProjectSetup.CreateFolders();

        var source = FindRoot(scene, BoxNames[0]);
        if (source == null || source.transform.Find(VisualCaja) == null)
        {
            Debug.LogError($"[Sockets] Ejecuta antes el paso 3: '{BoxNames[0]}' no tiene el visual aplicado.");
            return;
        }

        var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(source, CajaPrefabPath, InteractionMode.UserAction);
        if (prefab == null)
        {
            Debug.LogError($"[Sockets] No se pudo crear {CajaPrefabPath}.");
            return;
        }

        // O las tres o ninguna: si solo Box_A fuese instancia, Box_B/C no heredarian los cambios.
        foreach (var name in BoxNames.Skip(1))
        {
            var box = FindRoot(scene, name);
            if (box == null || PrefabUtility.IsPartOfPrefabInstance(box)) continue;

            var replacement = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            replacement.name = name;
            replacement.transform.SetPositionAndRotation(box.transform.position, box.transform.rotation);
            replacement.transform.localScale = box.transform.localScale;
            Undo.RegisterCreatedObjectUndo(replacement, "Reemplazar caja por prefab");
            Undo.DestroyObjectImmediate(box);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Sockets] {CajaPrefabPath} creado y las tres cajas convertidas en instancias.");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static bool ModelExists(string path)
    {
        // AssetPathToGUID distingue mayusculas: Leafeon.FBX no es Leafeon.fbx.
        if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) return true;
        Debug.LogError($"[Sockets] No se encontro el modelo {path} (revisa mayusculas en la extension).");
        return false;
    }

    private static bool TryGetServerScene(out Scene scene)
    {
        scene = SceneManager.GetSceneByPath(ServerScene);
        if (scene.IsValid() && scene.isLoaded) return true;

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("[Sockets] Operacion cancelada.");
            return false;
        }

        scene = EditorSceneManager.OpenScene(ServerScene, OpenSceneMode.Single);
        if (scene.IsValid()) return true;

        Debug.LogError($"[Sockets] No se pudo abrir {ServerScene}.");
        return false;
    }

    // GameObject.Find ignora los inactivos y busca en todas las escenas cargadas.
    private static GameObject FindRoot(Scene scene, string name) =>
        scene.GetRootGameObjects().FirstOrDefault(go => go.name == name);

    private static bool IsInstanceOfModel(GameObject go, string assetPath) =>
        PrefabUtility.IsAnyPrefabInstanceRoot(go) &&
        PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == assetPath;

    private static bool TryWorldBounds(GameObject go, out Bounds bounds)
    {
        bounds = default;
        var found = false;

        foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer) continue;

            var rendererBounds = renderer.bounds;
            if (renderer is SkinnedMeshRenderer skinned && rendererBounds.size.y < 1e-4f &&
                skinned.sharedMesh != null)
            {
                rendererBounds = TransformBounds(skinned.sharedMesh.bounds, skinned.transform.localToWorldMatrix);
            }

            if (!found) { bounds = rendererBounds; found = true; }
            else bounds.Encapsulate(rendererBounds);
        }

        return found && bounds.size.y > 1e-4f;
    }

    private static Bounds TransformBounds(Bounds local, Matrix4x4 matrix)
    {
        var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
        var e = local.extents;

        for (var i = 0; i < 8; i++)
        {
            var corner = local.center + new Vector3(
                (i & 1) == 0 ? -e.x : e.x,
                (i & 2) == 0 ? -e.y : e.y,
                (i & 4) == 0 ? -e.z : e.z);
            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }

        return result;
    }
}
