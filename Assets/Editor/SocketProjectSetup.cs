using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Utilidades de editor para dejar el proyecto listo para compilar los dos perfiles
/// (servidor y cliente) del control remoto 3D por sockets.
///
/// Las escenas validas de la guia son las 3D. Las escenas del chat original
/// (SocketWorld_Server / SocketController_Client) quedan en el repo solo como referencia.
///
/// Menu: Tools > Sockets
/// - "Configurar Build Settings": registra SocketWorld3D_Server y SocketController_Client3D
///   en File > Build Settings (Scenes In Build) y deja SampleScene desactivada.
/// - "Solo servidor" / "Solo cliente": deja habilitada unicamente la escena del perfil
///   que vas a compilar, para que el build arranque en ella.
/// - "Crear carpetas del proyecto": asegura Scenes, Scripts, Prefabs, Materials y Builds.
/// </summary>
public static class SocketProjectSetup
{
    private const string ServerScene = "Assets/Scenes/SocketWorld3D_Server.unity";
    private const string ClientScene = "Assets/Scenes/SocketController_Client3D.unity";
    private const string SampleScene = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/Sockets/Configurar Build Settings", priority = 0)]
    public static void ConfigureBuildSettings()
    {
        ApplyScenes(serverEnabled: true, clientEnabled: true);
    }

    [MenuItem("Tools/Sockets/Perfil: solo servidor", priority = 20)]
    public static void ProfileServerOnly()
    {
        ApplyScenes(serverEnabled: true, clientEnabled: false);
    }

    [MenuItem("Tools/Sockets/Perfil: solo cliente", priority = 21)]
    public static void ProfileClientOnly()
    {
        ApplyScenes(serverEnabled: false, clientEnabled: true);
    }

    [MenuItem("Tools/Sockets/Crear carpetas del proyecto", priority = 40)]
    public static void CreateFolders()
    {
        foreach (var folder in new[] { "Scenes", "Scripts", "Prefabs", "Materials" })
        {
            if (!AssetDatabase.IsValidFolder("Assets/" + folder))
                AssetDatabase.CreateFolder("Assets", folder);
        }

        // Builds vive fuera de Assets: son artefactos de compilacion, no assets importables.
        var builds = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Builds");
        if (!Directory.Exists(builds)) Directory.CreateDirectory(builds);

        AssetDatabase.Refresh();
        Debug.Log("[Sockets] Carpetas verificadas: Scenes, Scripts, Prefabs, Materials y Builds.");
    }

    private static void ApplyScenes(bool serverEnabled, bool clientEnabled)
    {
        var scenes = new List<EditorBuildSettingsScene>();
        AddScene(scenes, ServerScene, serverEnabled, required: true);
        AddScene(scenes, ClientScene, clientEnabled, required: true);
        // La escena original queda registrada pero desactivada, como referencia.
        AddScene(scenes, SampleScene, enabled: false, required: false);

        EditorBuildSettings.scenes = scenes.ToArray();

        foreach (var s in scenes)
            Debug.Log($"[Sockets] Build Settings: {s.path} -> {(s.enabled ? "habilitada" : "deshabilitada")}");
    }

    private static void AddScene(List<EditorBuildSettingsScene> list, string path, bool enabled, bool required)
    {
        var guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid))
        {
            if (required) Debug.LogError($"[Sockets] No se encontro la escena {path}.");
            return;
        }
        list.Add(new EditorBuildSettingsScene(path, enabled));
    }
}
