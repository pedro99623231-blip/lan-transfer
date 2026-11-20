using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WiFiTransfer;

/// <summary>Miniaturas de fotos e vídeos (a mesma que o Explorer mostra), em PNG, com cache.</summary>
public static class Thumbs
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<byte[]?> LocalAsync(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        byte[]? bytes = null;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 160);
            if (thumb != null && thumb.Size > 0)
            {
                using var src = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(thumb);
                var decoder = BitmapDecoder.Create(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(decoder.Frames[0]);
                using var ms = new MemoryStream();
                enc.Save(ms);
                bytes = ms.ToArray();
            }
        }
        catch { bytes = null; }
        Cache[path] = bytes;
        return bytes;
    }

    public static ImageSource? ToImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 80;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
