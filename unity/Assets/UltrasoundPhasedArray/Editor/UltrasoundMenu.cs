using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Ultrasound.PhasedArray.EditorTools
{
    /// <summary>Editor entry points for setting up and feeding the console.</summary>
    public static class UltrasoundMenu
    {
        const string ConfigFolder = "Assets/UltrasoundPhasedArray";

        [MenuItem("Ultrasound/Create Phased Array Console", false, 0)]
        public static void CreateConsole()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = Object.FindFirstObjectByType<PhasedArrayConsole>();
#else
            var existing = Object.FindObjectOfType<PhasedArrayConsole>();
#endif
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                Debug.Log("[Ultrasound] The open scene already has a console.");
                return;
            }

            var host = new GameObject("Phased Array Console");
            host.AddComponent<PhasedArrayConsole>();

            Undo.RegisterCreatedObjectUndo(host, "Create Phased Array Console");
            Selection.activeGameObject = host;

            Debug.Log("[Ultrasound] Console added. Press play; no other scene objects are needed.");
        }

        [MenuItem("Ultrasound/Create Array Config Asset", false, 1)]
        public static void CreateConfigAsset()
        {
            var config = ArrayConfig.CreateDefault();

            if (!AssetDatabase.IsValidFolder(ConfigFolder))
            {
                Directory.CreateDirectory(ConfigFolder);
                AssetDatabase.Refresh();
            }

            string path = AssetDatabase.GenerateUniqueAssetPath(ConfigFolder + "/ArrayConfig.asset");
            AssetDatabase.CreateAsset(config, path);
            AssetDatabase.SaveAssets();

            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
            Debug.Log("[Ultrasound] Created " + path);
        }

        [MenuItem("Ultrasound/Bake angle-range CSV to .uscan...", false, 20)]
        public static void BakeCsv()
        {
            string csvPath = EditorUtility.OpenFilePanel(
                "Select an angle/range export from results/distance", "", "csv");
            if (string.IsNullOrEmpty(csvPath)) return;

            string destinationFolder = ScanAssetLoader.FolderPath;
            Directory.CreateDirectory(destinationFolder);

            string suggested = Path.GetFileNameWithoutExtension(csvPath) + ".uscan";
            string outputPath = EditorUtility.SaveFilePanel(
                "Save baked dataset", destinationFolder, suggested, "uscan");
            if (string.IsNullOrEmpty(outputPath)) return;

            var importer = new CsvScanImporter { maxRangeCm = 160f };
            ScanLibrary library;

            try
            {
                EditorUtility.DisplayProgressBar("Baking scan", "Parsing " +
                                                 Path.GetFileName(csvPath), 0.3f);
                library = importer.Import(csvPath);
            }
            catch (System.Exception exception)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Bake failed", exception.Message, "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (library == null || library.frames.Count == 0)
            {
                EditorUtility.DisplayDialog("Bake failed", "No usable rows in " + csvPath, "OK");
                return;
            }

            using (var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                UscanFile.Write(stream, library);

            if (outputPath.StartsWith(destinationFolder)) RefreshManifest();
            AssetDatabase.Refresh();

            var summary = new StringBuilder();
            summary.AppendLine(Path.GetFileName(outputPath));
            foreach (ScanFrame frame in library.frames)
                summary.AppendFormat(CultureInfo.InvariantCulture, "{0} {1}: {2} angles x {3} bins, peak {4:0.0}\n",
                                     frame.scene, frame.txMode, frame.AngleCount, frame.BinCount,
                                     frame.MaxAmplitude());

            Debug.Log("[Ultrasound] " + summary);
            EditorUtility.DisplayDialog("Bake complete", summary.ToString(), "OK");
        }

        [MenuItem("Ultrasound/Refresh dataset manifest", false, 21)]
        public static void RefreshManifest()
        {
            string folder = ScanAssetLoader.FolderPath;
            if (!Directory.Exists(folder))
            {
                Debug.LogWarning("[Ultrasound] " + folder + " does not exist yet.");
                return;
            }

            var names = new List<string>();
            foreach (string path in Directory.GetFiles(folder, "*.uscan"))
                names.Add(Path.GetFileName(path));
            names.Sort(System.StringComparer.Ordinal);

            var text = new StringBuilder();
            text.AppendLine("# Baked datasets loaded by the Unity console, one file name per line.");
            text.AppendLine("# Regenerate with unity/Tools/bake_scan.py or Ultrasound > Refresh dataset manifest.");
            foreach (string name in names) text.AppendLine(name);

            File.WriteAllText(Path.Combine(folder, ScanAssetLoader.ManifestName), text.ToString());
            AssetDatabase.Refresh();
            Debug.Log("[Ultrasound] Manifest lists " + names.Count + " dataset(s).");
        }
    }
}
