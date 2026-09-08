using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Ultrasound.PhasedArray
{
    /// <summary>
    /// Loads baked <c>.uscan</c> datasets from StreamingAssets.
    /// </summary>
    /// <remarks>
    /// On Android and WebGL, StreamingAssets is not a readable directory, so the bytes are
    /// always fetched through <see cref="UnityWebRequest"/>. The dataset index is a plain
    /// text manifest because directory listing is unavailable on those platforms too.
    /// </remarks>
    public static class ScanAssetLoader
    {
        public const string Folder = "ultrasound";
        public const string ManifestName = "datasets.txt";

        public static string FolderPath
        {
            get { return Path.Combine(Application.streamingAssetsPath, Folder); }
        }

        public static string PathFor(string fileName)
        {
            return Path.Combine(FolderPath, fileName);
        }

        static string UriFor(string path)
        {
            if (path.Contains("://")) return path;
            return "file://" + path;
        }

        /// <summary>Reads the manifest, falling back to a directory scan in the editor.</summary>
        public static IEnumerator LoadManifest(Action<List<string>> onComplete, Action<string> onError = null)
        {
            var names = new List<string>();
            string manifestPath = PathFor(ManifestName);

            yield return LoadBytes(manifestPath, bytes =>
            {
                string text = System.Text.Encoding.UTF8.GetString(bytes);
                foreach (string line in text.Split('\n'))
                {
                    string entry = line.Trim();
                    if (entry.Length == 0 || entry[0] == '#') continue;
                    names.Add(entry);
                }
            }, null);

            if (names.Count == 0 && Directory.Exists(FolderPath))
            {
                foreach (string path in Directory.GetFiles(FolderPath, "*.uscan"))
                    names.Add(Path.GetFileName(path));
                names.Sort(StringComparer.Ordinal);
            }

            if (names.Count == 0 && onError != null)
                onError("no datasets found in " + FolderPath);

            if (onComplete != null) onComplete(names);
        }

        /// <summary>Loads and parses one baked dataset.</summary>
        public static IEnumerator LoadLibrary(string fileName, Action<ScanLibrary> onComplete,
                                              Action<string> onError = null)
        {
            string path = PathFor(fileName);
            ScanLibrary library = null;
            string failure = null;

            yield return LoadBytes(path, bytes =>
            {
                try
                {
                    library = UscanFile.Read(bytes, fileName);
                }
                catch (Exception exception)
                {
                    failure = fileName + ": " + exception.Message;
                }
            }, error => failure = error);

            if (library != null)
            {
                if (onComplete != null) onComplete(library);
            }
            else if (onError != null)
            {
                onError(failure ?? (fileName + ": not found"));
            }
        }

        static IEnumerator LoadBytes(string path, Action<byte[]> onComplete, Action<string> onError)
        {
            using (var request = UnityWebRequest.Get(UriFor(path)))
            {
                yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool failed = request.result != UnityWebRequest.Result.Success;
#else
                bool failed = request.isNetworkError || request.isHttpError;
#endif
                if (failed)
                {
                    if (onError != null) onError(path + ": " + request.error);
                    yield break;
                }

                byte[] data = request.downloadHandler != null ? request.downloadHandler.data : null;
                if (data == null || data.Length == 0)
                {
                    if (onError != null) onError(path + ": empty response");
                    yield break;
                }

                if (onComplete != null) onComplete(data);
            }
        }
    }
}
