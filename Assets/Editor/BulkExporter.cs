using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static class BulkExporter
{
    // Adapte ces marqueurs à TON schéma de nommage :
    // ex. "…_LogKEV" (player) vs "…_ColliderPosition" (ROI)
    static readonly string[] PlayerMarkers = { "_Log", "_LogKEV" };
    static readonly string[] DataMarkers = { "_ColliderPosition", "_PositionGameObject" };

    [MenuItem("Tools/EyeTracking/Export All Pairs (Resources)")]
    public static void ExportAllPairs()
    {
        // Trouve un GameManager ou en crée un temporaire
        var gm = Object.FindObjectOfType<GameManager>();
        if (gm == null)
        {
            var go = new GameObject("GameManager_Auto");
            gm = go.AddComponent<GameManager>();
        }

        // Récupère tous les TextAssets dans Resources/
        var all = Resources.LoadAll<TextAsset>("");

        // Sépare "players" et "datas" par marqueurs
        var players = all.Where(a => PlayerMarkers.Any(m => a.name.Contains(m))).ToList();
        var datas = all.Where(a => DataMarkers.Any(m => a.name.Contains(m))).ToList();

        if (players.Count == 0 || datas.Count == 0)
        {
            Debug.LogWarning($"[BulkExporter] No matching TextAssets found in Resources/. Players: {players.Count}, Data: {datas.Count}");
            return;
        }

        // Fait des paires par “clé” (préfixe avant le marqueur)
        var pairs = PairByKey(players, datas);

        int ok = 0, miss = 0;
        foreach (var pair in pairs)
        {
            if (pair.data == null)
            {
                Debug.LogWarning($"[BulkExporter] No ROI file found for player '{pair.player.name}'.");
                miss++;
                continue;
            }

            // Export “as-displayed” pour cette paire
            gm.GenerateOutputFileFor(pair.player.name, pair.data.name);
            ok++;
        }

        Debug.Log($"[BulkExporter] Export done. OK: {ok}, Missing pairs: {miss}. Output folder: {Application.persistentDataPath}");
    }

    // Option batch (ligne de commande):
    // Unity.exe -batchmode -projectPath "X:\MyProject" -executeMethod BulkExporter.ExportAllPairsBatch -quit
    public static void ExportAllPairsBatch()
    {
        ExportAllPairs();
        EditorApplication.Exit(0);
    }

    // --- Pairing helpers ---

    private static List<(TextAsset player, TextAsset data)> PairByKey(
        List<TextAsset> players, List<TextAsset> datas)
    {
        var result = new List<(TextAsset, TextAsset)>();

        foreach (var pl in players)
        {
            var k = ExtractKey(pl.name, PlayerMarkers);

            // match exact de clé
            var match = datas.FirstOrDefault(d => ExtractKey(d.name, DataMarkers) == k);

            // fallback permissif
            if (match == null)
                match = datas.FirstOrDefault(d => d.name.Contains(k));

            result.Add((pl, match));
        }
        return result;
    }

    private static string ExtractKey(string fileName, string[] markers)
    {
        foreach (var m in markers)
        {
            var idx = fileName.IndexOf(m);
            if (idx > 0) return fileName.Substring(0, idx);
        }
        // si aucun marqueur reconnu → utiliser tout le nom
        return fileName;
    }
}
