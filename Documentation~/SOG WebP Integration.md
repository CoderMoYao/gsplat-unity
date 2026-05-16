# SOG WebP Integration

SOG assets are zip archives containing `meta.json` plus lossless WebP images. The runtime SOG loader can use native `libwebp` directly. If `libwebp` is not available, install a WebP decoder and assign `Gsplat.SogImageDecoder.CustomWebPDecoder`.

## Native libwebp

For Windows:

1. Download or build the official WebP library from Google.
2. Put `libwebp.dll` in a Unity plugin folder, for example:
   `Assets/Plugins/x86_64/libwebp.dll`
3. In Unity's plugin inspector, enable it for Editor and the target standalone platform.
4. Reimport the `.sog` asset.

The SOG importer calls these native functions:

- `WebPGetInfo`
- `WebPDecodeRGBAInto`

If your DLL has a different name, either rename it to `libwebp.dll` or provide your own decoder through `SogImageDecoder.CustomWebPDecoder`.

The decoder must return RGBA pixels in row-major order starting at the top-left corner:

```csharp
using Gsplat;
using UnityEditor;

[InitializeOnLoad]
public static class SogWebPDecoderBootstrap
{
    static SogWebPDecoderBootstrap()
    {
        SogImageDecoder.CustomWebPDecoder = DecodeWebP;
    }

    static SogImage DecodeWebP(byte[] bytes)
    {
        // Replace this with your WebP library call.
        // The result must be RGBA8, top-left row-major.
        int width = 0;
        int height = 0;
        byte[] rgbaTopLeft = null;

        return new SogImage(width, height, rgbaTopLeft);
    }
}
```

Good integration options:

- Use a native `libwebp` Unity plugin and wrap `WebPDecodeRGBA`.
- Use a managed WebP decoder package if it supports lossless WebP and returns raw RGBA.
- For editor-only conversion workflows, decode WebP during import and let the imported `GsplatAsset` store the decoded/packed splat data.

Do not return Unity's default `GetPixels32()` order directly unless your wrapper flips rows. Unity pixel arrays are usually bottom-left row-major; SOG expects top-left row-major.
