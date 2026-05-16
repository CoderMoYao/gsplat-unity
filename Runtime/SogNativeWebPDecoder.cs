// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;

namespace Gsplat
{
    public static class SogNativeWebPDecoder
    {
        const string k_LibWebP = "libwebp";

        [DllImport(k_LibWebP, CallingConvention = CallingConvention.Cdecl)]
        static extern int WebPGetInfo(byte[] data, UIntPtr dataSize, out int width, out int height);

        [DllImport(k_LibWebP, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr WebPDecodeRGBAInto(byte[] data, UIntPtr dataSize, byte[] outputBuffer,
            UIntPtr outputBufferSize, int outputStride);

        public static SogImage Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                throw new ArgumentException("WebP input is empty", nameof(bytes));

            if (WebPGetInfo(bytes, (UIntPtr)bytes.Length, out var width, out var height) == 0 ||
                width <= 0 || height <= 0)
                throw new InvalidOperationException("libwebp failed to read WebP image dimensions");

            var output = new byte[width * height * 4];
            var decoded = WebPDecodeRGBAInto(bytes, (UIntPtr)bytes.Length, output, (UIntPtr)output.Length, width * 4);
            if (decoded == IntPtr.Zero)
                throw new InvalidOperationException("libwebp failed to decode WebP image");

            return new SogImage(width, height, output);
        }
    }
}
