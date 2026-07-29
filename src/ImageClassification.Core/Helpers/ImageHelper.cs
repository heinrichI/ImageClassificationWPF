using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageClassification.Core.Helpers;

public static class ImageHelper
{
    public static Image<Rgb24> LoadAndResize(string path, int targetSize)
    {
        var image = Image.Load<Rgb24>(path);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(targetSize, targetSize),
            Mode = ResizeMode.Crop
        }));
        return image;
    }

    public static float[] ImageToTensor(Image<Rgb24> image, int targetSize)
    {
        var tensor = new float[3 * targetSize * targetSize];
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < targetSize; y++)
            {
                var pixelRow = accessor.GetRowSpan(y);
                for (int x = 0; x < targetSize; x++)
                {
                    var pixel = pixelRow[x];
                    tensor[0 * targetSize * targetSize + y * targetSize + x] = (pixel.R / 255f - mean[0]) / std[0];
                    tensor[1 * targetSize * targetSize + y * targetSize + x] = (pixel.G / 255f - mean[1]) / std[1];
                    tensor[2 * targetSize * targetSize + y * targetSize + x] = (pixel.B / 255f - mean[2]) / std[2];
                }
            }
        });

        return tensor;
    }

    public static bool IsSupportedImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" or ".webp";
    }
}
