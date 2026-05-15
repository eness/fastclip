using System.Drawing;
using FastClip.Models;

namespace FastClip.Services;

internal interface IImageFileWriter
{
    ImageSaveResult ReplaceImageFile(Image image, string targetPath, PasteOptions options);
    ImageSaveResult CreateNewImage(Image image, string folderPath, string extension, PasteOptions options);
}
