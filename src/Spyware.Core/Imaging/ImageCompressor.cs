using System.Drawing;
using System.Drawing.Imaging;

namespace Spyware.Core.Imaging;

public static class ImageCompressor
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");

    /// <summary>Devuelve un bitmap nuevo escalado dentro del máximo, o un clon si ya entra.</summary>
    public static Bitmap ResizeToMax(Bitmap source, int maxWidth, int maxHeight)
    {
        maxWidth = Math.Max(16, maxWidth);
        maxHeight = Math.Max(16, maxHeight);

        if (source.Width <= maxWidth && source.Height <= maxHeight)
            return (Bitmap)source.Clone();

        double ratio = Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        int height = Math.Max(1, (int)Math.Round(source.Height * ratio));

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
    }

    public static byte[] ToJpeg(Image image, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        using var ms = new MemoryStream();
        image.Save(ms, JpegCodec, parameters);
        return ms.ToArray();
    }

    public static byte[] ToPng(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
