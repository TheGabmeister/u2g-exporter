using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace U2GExporter
{
    public static class ExportMenu
    {
        [MenuItem("Assets/Export to Godot")]
        static void Export()
        {
            // Get selected folder
            string selectedPath = GetSelectedFolderPath();
            if (string.IsNullOrEmpty(selectedPath))
            {
                Debug.LogError("[U2G][FATAL] No folder selected.");
                return;
            }

            // Pick output directory
            string outputDir = EditorUtility.SaveFolderPanel("Export to Godot — Choose Output Folder", "", "");
            if (string.IsNullOrEmpty(outputDir))
                return; // User cancelled

            // Check if output directory already has files
            if (Directory.Exists(outputDir) && (Directory.GetFiles(outputDir).Length > 0 || Directory.GetDirectories(outputDir).Length > 0))
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Export to Godot",
                    $"The output directory already contains files:\n{outputDir}\n\nExisting files may be overwritten. Continue?",
                    "Continue", "Cancel");
                if (!overwrite)
                    return;
            }

            // Prompt to save unsaved scenes
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return; // User cancelled save prompt

            // Capture scene setup for restoration
            var sceneSetup = EditorSceneManager.GetSceneManagerSetup();

            bool cancelled = false;
            int processedCount = 0;

            try
            {
                // Phase 1: Dependency resolution
                EditorUtility.DisplayCancelableProgressBar("Export to Godot", "Resolving dependencies...", 0f);

                var skipReport = new SkipReport();
                var allAssets = DependencyResolver.Resolve(selectedPath, skipReport);

                // Separate by type
                var textures = allAssets.Where(a => a.Type == AssetType.Texture).ToList();
                var fbxFiles = allAssets.Where(a => a.Type == AssetType.Fbx).ToList();
                var materials = allAssets.Where(a => a.Type == AssetType.Material).ToList();
                var prefabs = allAssets.Where(a => a.Type == AssetType.Prefab).ToList();
                var scenes = DependencyResolver.GetScenesInFolder(allAssets, selectedPath);

                int totalAssets = textures.Count + fbxFiles.Count + materials.Count + prefabs.Count + scenes.Count;
                Debug.Log($"[U2G][INFO] Found {allAssets.Count} total assets, {totalAssets} to convert " +
                    $"({textures.Count} textures, {fbxFiles.Count} FBX, {materials.Count} materials, " +
                    $"{prefabs.Count} prefabs, {scenes.Count} scenes).");

                // Ensure output directory exists
                Directory.CreateDirectory(outputDir);

                // Phase 2: Convert textures
                foreach (var asset in textures)
                {
                    if (ShowProgress("Copying textures", asset.Path, processedCount, totalAssets))
                    { cancelled = true; break; }

                    try { TextureExporter.Export(asset.Path, outputDir); }
                    catch (System.Exception ex) { Debug.LogError($"[U2G][ERROR] Failed to copy texture '{asset.Path}': {ex.Message}"); }
                    processedCount++;
                }

                // Phase 3: Copy FBX files
                if (!cancelled)
                {
                    foreach (var asset in fbxFiles)
                    {
                        if (ShowProgress("Copying FBX files", asset.Path, processedCount, totalAssets))
                        { cancelled = true; break; }

                        try { FbxExporter.Export(asset.Path, outputDir); }
                        catch (System.Exception ex) { Debug.LogError($"[U2G][ERROR] Failed to copy FBX '{asset.Path}': {ex.Message}"); }
                        processedCount++;
                    }
                }

                // Phase 4: Convert materials
                if (!cancelled)
                {
                    foreach (var asset in materials)
                    {
                        if (ShowProgress("Converting materials", asset.Path, processedCount, totalAssets))
                        { cancelled = true; break; }

                        try { MaterialExporter.Export(asset.Path, outputDir, skipReport); }
                        catch (System.Exception ex) { Debug.LogError($"[U2G][ERROR] Failed to convert material '{asset.Path}': {ex.Message}"); }
                        processedCount++;
                    }
                }

                // Phase 5: Convert prefabs
                if (!cancelled)
                {
                    foreach (var asset in prefabs)
                    {
                        if (ShowProgress("Converting prefabs", asset.Path, processedCount, totalAssets))
                        { cancelled = true; break; }

                        try { PrefabExporter.Export(asset.Path, outputDir, skipReport); }
                        catch (System.Exception ex) { Debug.LogError($"[U2G][ERROR] Failed to convert prefab '{asset.Path}': {ex.Message}"); }
                        processedCount++;
                    }
                }

                // Phase 6: Convert scenes
                if (!cancelled)
                {
                    foreach (var asset in scenes)
                    {
                        if (ShowProgress("Converting scenes", asset.Path, processedCount, totalAssets))
                        { cancelled = true; break; }

                        try
                        {
                            var scene = EditorSceneManager.OpenScene(asset.Path, OpenSceneMode.Additive);
                            SceneExporter.Export(scene, asset.Path, outputDir, skipReport);
                            EditorSceneManager.CloseScene(scene, true);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogError($"[U2G][ERROR] Failed to convert scene '{asset.Path}': {ex.Message}");
                        }
                        processedCount++;
                    }
                }

                // Phase 7: Generate project.godot
                if (!cancelled)
                {
                    string projectName = Path.GetFileName(selectedPath);
                    ProjectWriter.WriteProjectFile(outputDir, projectName);
                    Debug.Log("[U2G][INFO] Generated project.godot");
                }

                // Phase 8: Skip report
                skipReport.Log();

                // Summary
                if (cancelled)
                    Debug.LogWarning($"[U2G][WARN] Export cancelled by user after {processedCount}/{totalAssets} assets. Partial output preserved at: {outputDir}");
                else
                    Debug.Log($"[U2G][INFO] Export complete! {processedCount} assets converted to: {outputDir}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();

                // Restore editor scene setup
                if (sceneSetup != null && sceneSetup.Length > 0)
                {
                    try { EditorSceneManager.RestoreSceneManagerSetup(sceneSetup); }
                    catch (System.Exception ex) { Debug.LogWarning($"[U2G][WARN] Failed to restore scene setup: {ex.Message}"); }
                }
            }

            // Open output folder
            if (!cancelled)
                EditorUtility.RevealInFinder(outputDir);
        }

        [MenuItem("Assets/Export to Godot", true)]
        static bool ExportValidation()
        {
            return IsSelectedFolder();
        }

        static bool IsSelectedFolder()
        {
            string path = GetSelectedFolderPath();
            return !string.IsNullOrEmpty(path);
        }

        static string GetSelectedFolderPath()
        {
            foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (AssetDatabase.IsValidFolder(path))
                    return path;
            }
            return null;
        }

        static bool ShowProgress(string phase, string currentAsset, int current, int total)
        {
            float progress = total > 0 ? (float)current / total : 0f;
            return EditorUtility.DisplayCancelableProgressBar(
                "Export to Godot",
                $"{phase}: {Path.GetFileName(currentAsset)}",
                progress);
        }
    }
}
