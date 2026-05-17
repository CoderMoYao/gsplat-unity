// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEngine;

namespace Gsplat
{
    public sealed class SogData
    {
        public uint SplatCount;
        public byte SHBands;
        public Bounds Bounds;
        public Vector3[] Positions;
        public Vector4[] Colors; // SH DC coefficients in RGB, decoded alpha in A.
        public Vector3[] SHs;
        public Vector3[] Scales;
        public Vector4[] Rotations; // Quaternion xyzw.
    }

    public readonly struct SogImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] RgbaTopLeft;

        public SogImage(int width, int height, byte[] rgbaTopLeft)
        {
            Width = width;
            Height = height;
            RgbaTopLeft = rgbaTopLeft;
        }

        public int PixelCount => Width * Height;

        public byte R(int index) => RgbaTopLeft[index * 4];
        public byte G(int index) => RgbaTopLeft[index * 4 + 1];
        public byte B(int index) => RgbaTopLeft[index * 4 + 2];
        public byte A(int index) => RgbaTopLeft[index * 4 + 3];
    }

    public static class SogImageDecoder
    {
        public delegate SogImage WebPDecoder(byte[] bytes);

        /// <summary>
        /// Optional WebP decoder hook. The returned pixels must be RGBA, row-major from the top-left corner.
        /// </summary>
        public static WebPDecoder CustomWebPDecoder;

        public static SogImage Decode(string filename, byte[] bytes)
        {
            var extension = Path.GetExtension(filename).ToLowerInvariant();
            if (extension == ".webp")
            {
                if (CustomWebPDecoder != null)
                    return CustomWebPDecoder(bytes);

                try
                {
                    return SogNativeWebPDecoder.Decode(bytes);
                }
                catch (DllNotFoundException e)
                {
                    throw new NotSupportedException(
                        $"Unable to decode '{filename}'. SOG files store data in lossless WebP images. " +
                        "Place a native libwebp plugin in the Unity project, or assign SogImageDecoder.CustomWebPDecoder.",
                        e);
                }
                catch (EntryPointNotFoundException e)
                {
                    throw new NotSupportedException(
                        $"Unable to decode '{filename}'. The loaded libwebp library does not expose the required WebP decoder functions.",
                        e);
                }
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new NotSupportedException(
                        $"Unable to decode '{filename}' with Unity ImageConversion.LoadImage.");

                if (texture.width <= 0 || texture.height <= 0)
                    throw new InvalidDataException($"Decoded image '{filename}' has invalid size {texture.width}x{texture.height}");

                var width = texture.width;
                var height = texture.height;
                var pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length != width * height)
                    throw new InvalidDataException($"Decoded image '{filename}' did not return readable RGBA pixels");
                var rgba = new byte[width * height * 4];

                // Unity returns pixels row-major from bottom-left; SOG stores images from top-left.
                for (var y = 0; y < height; y++)
                {
                    var srcY = height - 1 - y;
                    for (var x = 0; x < width; x++)
                    {
                        var src = pixels[srcY * width + x];
                        var dstIndex = (y * width + x) * 4;
                        rgba[dstIndex] = src.r;
                        rgba[dstIndex + 1] = src.g;
                        rgba[dstIndex + 2] = src.b;
                        rgba[dstIndex + 3] = src.a;
                    }
                }

                return new SogImage(width, height, rgba);
            }
            finally
            {
                if (Application.isEditor)
                    UnityEngine.Object.DestroyImmediate(texture);
                else
                    UnityEngine.Object.Destroy(texture);
            }
        }
    }

    public static class SogDecoder
    {
        [Serializable]
        class Meta
        {
            public int version;
            public int count;
            public MetaMeans means;
            public MetaScales scales;
            public MetaQuats quats;
            public MetaSh0 sh0;
            public MetaShN shN;
        }

        [Serializable]
        class MetaMeans
        {
            public float[] mins;
            public float[] maxs;
            public string[] files;
        }

        [Serializable]
        class MetaScales
        {
            public float[] codebook;
            public string[] files;
        }

        [Serializable]
        class MetaQuats
        {
            public string[] files;
        }

        [Serializable]
        class MetaSh0
        {
            public float[] codebook;
            public string[] files;
        }

        [Serializable]
        class MetaShN
        {
            public int count;
            public int bands;
            public float[] codebook;
            public string[] files;
        }

        public static SogData Load(string sogPath, ProgressCallback progressCallback = null)
        {
            var files = ReadFiles(sogPath);
            return Load(files, progressCallback);
        }

        public static SogData Load(byte[] sogArchiveBytes, ProgressCallback progressCallback = null)
        {
            if (sogArchiveBytes == null || sogArchiveBytes.Length == 0)
                throw new ArgumentException("SOG archive bytes are empty", nameof(sogArchiveBytes));

            var files = ReadArchive(sogArchiveBytes);
            return Load(files, progressCallback);
        }

        public static SogData Load(IReadOnlyDictionary<string, byte[]> sogFiles,
            ProgressCallback progressCallback = null)
        {
            if (sogFiles == null)
                throw new ArgumentNullException(nameof(sogFiles));

            var files = sogFiles.ToDictionary(pair => NormalizePath(pair.Key), pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var metaText = ReadText(files, "meta.json");
            var meta = JsonUtility.FromJson<Meta>(metaText);
            ValidateMeta(meta);

            var count = meta.count;
            var data = new SogData
            {
                SplatCount = (uint)count,
                SHBands = meta.shN?.files != null && meta.shN.files.Length > 0 ? (byte)meta.shN.bands : (byte)0,
                Positions = new Vector3[count],
                Colors = new Vector4[count],
                Scales = new Vector3[count],
                Rotations = new Vector4[count]
            };
            if (data.SHBands > 0)
                data.SHs = new Vector3[count * GsplatUtils.SHBandsToCoefficientCount(data.SHBands)];

            var meansLower = DecodeImage(files, meta.means.files[0]);
            var meansUpper = DecodeImage(files, meta.means.files[1]);
            var scalesFile = FirstFile(meta.scales.files, "scales.files");
            var quatsFile = FirstFile(meta.quats.files, "quats.files");
            var sh0File = FirstFile(meta.sh0.files, "sh0.files");
            var scales = DecodeImage(files, scalesFile);
            var quats = DecodeImage(files, quatsFile);
            var sh0 = DecodeImage(files, sh0File);

            EnsurePixelCapacity(meansLower, count, meta.means.files[0]);
            EnsurePixelCapacity(meansUpper, count, meta.means.files[1]);
            EnsurePixelCapacity(scales, count, scalesFile);
            EnsurePixelCapacity(quats, count, quatsFile);
            EnsurePixelCapacity(sh0, count, sh0File);

            SogImage shNCentroids = default;
            SogImage shNLabels = default;
            if (data.SHBands > 0)
            {
                shNCentroids = DecodeImage(files, meta.shN.files[0]);
                shNLabels = DecodeImage(files, meta.shN.files[1]);
                EnsurePixelCapacity(shNLabels, count, meta.shN.files[1]);
            }

            for (var i = 0; i < count; i++)
            {
                data.Positions[i] = DecodePosition(meta.means, meansLower, meansUpper, i);
                data.Scales[i] = DecodeScale(meta.scales, scales, i);
                data.Rotations[i] = DecodeQuaternion(quats, i);
                data.Colors[i] = DecodeSh0(meta.sh0, sh0, i);

                if (data.SHs != null)
                    DecodeShN(meta.shN, shNCentroids, shNLabels, i, data.SHs);

                if (i == 0)
                    data.Bounds = new Bounds(data.Positions[i], Vector3.zero);
                else
                    data.Bounds.Encapsulate(data.Positions[i]);

                if ((i & 4095) == 0)
                    progressCallback?.Invoke("Decoding SOG splats", i / (float)count);
            }

            progressCallback?.Invoke("Decoding SOG splats", 1.0f);
            return data;
        }

        static Dictionary<string, byte[]> ReadFiles(string sogPath)
        {
            if (Directory.Exists(sogPath))
            {
                return Directory.GetFiles(sogPath, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => NormalizePath(MakeRelativePath(sogPath, path)),
                        File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            }

            if (!File.Exists(sogPath))
                throw new FileNotFoundException("SOG file does not exist", sogPath);

            using var stream = File.OpenRead(sogPath);
            return ReadArchive(stream);
        }

        static Dictionary<string, byte[]> ReadArchive(byte[] archiveBytes)
        {
            using var stream = new MemoryStream(archiveBytes, false);
            return ReadArchive(stream);
        }

        static Dictionary<string, byte[]> ReadArchive(Stream stream)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                using var entryStream = entry.Open();
                using var memory = new MemoryStream();
                entryStream.CopyTo(memory);
                files[NormalizePath(entry.FullName)] = memory.ToArray();
            }

            return files;
        }

        static string ReadText(Dictionary<string, byte[]> files, string filename)
        {
            var bytes = ReadFile(files, filename);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        static byte[] ReadFile(Dictionary<string, byte[]> files, string filename)
        {
            var normalized = NormalizePath(filename);
            if (files.TryGetValue(normalized, out var bytes))
                return bytes;

            var match = files.FirstOrDefault(pair =>
                pair.Key.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(pair.Key), normalized, StringComparison.OrdinalIgnoreCase));
            if (match.Value != null)
                return match.Value;

            throw new FileNotFoundException($"SOG archive is missing '{filename}'");
        }

        static SogImage DecodeImage(Dictionary<string, byte[]> files, string filename)
        {
            return SogImageDecoder.Decode(filename, ReadFile(files, filename));
        }

        static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString());
        }

        static string AppendDirectorySeparatorChar(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
                !path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                return path + Path.DirectorySeparatorChar;
            return path;
        }

        static void ValidateMeta(Meta meta)
        {
            if (meta == null)
                throw new InvalidDataException("Invalid SOG meta.json");
            if (meta.version != 2)
                throw new NotSupportedException($"Unsupported SOG version {meta.version}; only version 2 is supported");
            if (meta.count <= 0)
                throw new NotSupportedException("SOG contains no splats");
            if (meta.means?.mins == null || meta.means.maxs == null || meta.means.files == null ||
                meta.means.mins.Length < 3 || meta.means.maxs.Length < 3 || meta.means.files.Length < 2)
                throw new InvalidDataException("SOG meta.json is missing means data");
            if (meta.scales?.codebook == null || meta.scales.files == null || meta.scales.files.Length == 0 ||
                meta.scales.codebook.Length < 256)
                throw new InvalidDataException("SOG meta.json is missing scales data");
            if (meta.quats?.files == null || meta.quats.files.Length == 0)
                throw new InvalidDataException("SOG meta.json is missing quats data");
            if (meta.sh0?.codebook == null || meta.sh0.files == null || meta.sh0.files.Length == 0 ||
                meta.sh0.codebook.Length < 256)
                throw new InvalidDataException("SOG meta.json is missing sh0 data");
            if (meta.shN != null && meta.shN.files != null && meta.shN.files.Length > 0)
            {
                if (meta.shN.bands < 1 || meta.shN.bands > 3 || meta.shN.count < 1 ||
                    meta.shN.files.Length < 2 || meta.shN.codebook == null || meta.shN.codebook.Length < 256)
                    throw new InvalidDataException("SOG meta.json is missing shN data");
            }
        }

        static void EnsurePixelCapacity(SogImage image, int count, string filename)
        {
            if (image.PixelCount < count)
                throw new InvalidDataException($"SOG image '{filename}' has {image.PixelCount} pixels, expected at least {count}");
        }

        static string FirstFile(string[] files, string fieldName)
        {
            if (files == null || files.Length == 0 || string.IsNullOrEmpty(files[0]))
                throw new InvalidDataException($"SOG meta.json is missing {fieldName}");
            return files[0];
        }

        static Vector3 DecodePosition(MetaMeans means, SogImage lower, SogImage upper, int index)
        {
            var x = lower.R(index) | (upper.R(index) << 8);
            var y = lower.G(index) | (upper.G(index) << 8);
            var z = lower.B(index) | (upper.B(index) << 8);
            return new Vector3(
                Unlog(Mathf.Lerp(means.mins[0], means.maxs[0], x / 65535.0f)),
                Unlog(Mathf.Lerp(means.mins[1], means.maxs[1], y / 65535.0f)),
                Unlog(Mathf.Lerp(means.mins[2], means.maxs[2], z / 65535.0f)));
        }

        static float Unlog(float value)
        {
            return Mathf.Sign(value) * (Mathf.Exp(Mathf.Abs(value)) - 1.0f);
        }

        static Vector3 DecodeScale(MetaScales scalesMeta, SogImage image, int index)
        {
            var r = image.R(index);
            var g = image.G(index);
            var b = image.B(index);
            return new Vector3(
                Mathf.Exp(scalesMeta.codebook[r]),
                Mathf.Exp(scalesMeta.codebook[g]),
                Mathf.Exp(scalesMeta.codebook[b]));
        }

        static Vector4 DecodeQuaternion(SogImage image, int index)
        {
            var a = ToSmallestThreeComponent(image.R(index));
            var b = ToSmallestThreeComponent(image.G(index));
            var c = ToSmallestThreeComponent(image.B(index));
            var mode = image.A(index) - 252;
            if (mode < 0 || mode > 3)
                throw new InvalidDataException($"Invalid SOG quaternion mode {image.A(index)} at splat {index}");

            var d = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - (a * a + b * b + c * c)));
            return mode switch
            {
                0 => new Vector4(d, a, b, c),
                1 => new Vector4(a, d, b, c),
                2 => new Vector4(a, b, d, c),
                _ => new Vector4(a, b, c, d)
            };
        }

        static float ToSmallestThreeComponent(byte value)
        {
            return (value / 255.0f - 0.5f) * 2.0f / Mathf.Sqrt(2.0f);
        }

        static Vector4 DecodeSh0(MetaSh0 sh0Meta, SogImage image, int index)
        {
            var r = image.R(index);
            var g = image.G(index);
            var b = image.B(index);
            var a = image.A(index);
            return new Vector4(
                sh0Meta.codebook[r],
                sh0Meta.codebook[g],
                sh0Meta.codebook[b],
                a / 255.0f);
        }

        static void DecodeShN(MetaShN shNMeta, SogImage centroids, SogImage labels, int index, Vector3[] shs)
        {
            var coeffsPerSplat = GsplatUtils.SHBandsToCoefficientCount((byte)shNMeta.bands);
            var label = labels.R(index) | (labels.G(index) << 8);
            if (label >= shNMeta.count)
                throw new InvalidDataException($"Invalid SOG SH label {label} at splat {index}");
            var baseSplat = index * coeffsPerSplat;

            for (var coeff = 0; coeff < coeffsPerSplat; coeff++)
            {
                var centroidIndex = (label % 64) * coeffsPerSplat + coeff + (label / 64) * centroids.Width;
                if (centroidIndex >= centroids.PixelCount)
                    throw new InvalidDataException($"Invalid SOG SH centroid index {centroidIndex} at splat {index}");
                shs[baseSplat + coeff] = new Vector3(
                    shNMeta.codebook[centroids.R(centroidIndex)],
                    shNMeta.codebook[centroids.G(centroidIndex)],
                    shNMeta.codebook[centroids.B(centroidIndex)]);
            }
        }

        public static float AlphaToOpacityLogit(float alpha)
        {
            alpha = Mathf.Clamp(alpha, 1e-6f, 1.0f - 1e-6f);
            return Mathf.Log(alpha / (1.0f - alpha));
        }
    }
}
