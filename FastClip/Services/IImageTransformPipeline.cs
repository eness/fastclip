using System.Drawing;
using FastClip.Models;

namespace FastClip.Services;

internal interface IImageTransformPipeline
{
    Bitmap Apply(Image sourceImage, PasteOptions options);
}
