// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Gsplat
{
    public static class GsplatRuntimeLoader
    {
        public static GsplatAsset LoadSog(string sogPath, CompressionMode compression = CompressionMode.Spark,
            ProgressCallback progressCallback = null)
        {
            var asset = CreateAsset(compression);
            asset.LoadFromSog(sogPath, progressCallback);
            return asset;
        }

        public static GsplatAsset LoadSog(byte[] sogArchiveBytes,
            CompressionMode compression = CompressionMode.Spark, ProgressCallback progressCallback = null)
        {
            var asset = CreateAsset(compression);
            asset.LoadFromSogBytes(sogArchiveBytes, progressCallback);
            return asset;
        }

        public static async Task<GsplatAsset> LoadSogAsync(string sogPath,
            CompressionMode compression = CompressionMode.Spark, ProgressCallback progressCallback = null)
        {
            await Task.Yield();
            return LoadSog(sogPath, compression, progressCallback);
        }

        public static async Task<GsplatAsset> LoadSogAsync(byte[] sogArchiveBytes,
            CompressionMode compression = CompressionMode.Spark, ProgressCallback progressCallback = null)
        {
            await Task.Yield();
            return LoadSog(sogArchiveBytes, compression, progressCallback);
        }

        public static async Task<GsplatAsset> LoadSogFromUrlAsync(string url,
            CompressionMode compression = CompressionMode.Spark, ProgressCallback progressCallback = null)
        {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("SOG URL is empty", nameof(url));

            using var request = UnityWebRequest.Get(url);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                progressCallback?.Invoke("Downloading SOG", operation.progress);
                await Task.Yield();
            }

#if UNITY_2020_2_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
                throw new InvalidOperationException($"Unable to download SOG from '{url}': {request.error}");

            progressCallback?.Invoke("Downloading SOG", 1.0f);
            return LoadSog(request.downloadHandler.data, compression, progressCallback);
        }

        public static void AssignToRenderer(GsplatRenderer renderer, GsplatAsset asset)
        {
            if (!renderer)
                throw new ArgumentNullException(nameof(renderer));

            renderer.GsplatAsset = asset;
            renderer.ReloadAsset();
        }

        static GsplatAsset CreateAsset(CompressionMode compression)
        {
            return compression switch
            {
                CompressionMode.Uncompressed => ScriptableObject.CreateInstance<GsplatAssetUncompressed>(),
                CompressionMode.Spark => ScriptableObject.CreateInstance<GsplatAssetSpark>(),
                _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null)
            };
        }
    }
}
