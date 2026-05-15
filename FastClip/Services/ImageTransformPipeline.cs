using System.Drawing;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class ImageTransformPipeline : IImageTransformPipeline
{
    public Bitmap Apply(Image sourceImage, PasteOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceImage);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Resize.IsResizeActive)
        {
            return new Bitmap(sourceImage);
        }

        var resizedImage = new Bitmap(options.Resize.Width, options.Resize.Height);
        resizedImage.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);

        using var graphics = Graphics.FromImage(resizedImage);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        graphics.DrawImage(sourceImage, new Rectangle(0, 0, resizedImage.Width, resizedImage.Height));
        return resizedImage;
    }
}
