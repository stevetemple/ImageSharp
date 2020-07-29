using SixLabors.ImageSharp.Formats.WebP;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Tests.TestUtilities.ImageComparison;
using Xunit;

namespace SixLabors.ImageSharp.Tests.Formats.WebP
{
    public class WebPLosslessEncoderTests
    {

        private const PixelTypes TestPixelTypes = PixelTypes.Rgba32 | PixelTypes.RgbaVector | PixelTypes.Argb32;
        private static readonly ImageComparer ValidatorComparer = ImageComparer.TolerantPercentage(0.0015F);


        [Theory]
        [WithTestPatternImages(100, 100, TestPixelTypes, false)]
        [WithTestPatternImages(100, 100, TestPixelTypes, false)]
        public void EncodeGeneratedPatterns<TPixel>(TestImageProvider<TPixel> provider, bool limitAllocationBuffer)
            where TPixel : unmanaged, IPixel<TPixel>
        {
            if (limitAllocationBuffer)
            {
                provider.LimitAllocatorBufferCapacity().InPixelsSqrt(100);
            }

            using (Image<TPixel> image = provider.GetImage())
            {
                var encoder = new WebPEncoder { Lossless = true, Quality = 100 };

                // Always save as we need to compare the encoded output.
                provider.Utility.SaveTestOutputFile(image, "webp", encoder);
            }

            // TODO: Using this as a harness just to see the code running need to actually compare encoded result etc
            /*string path = provider.Utility.GetTestOutputFileName("webp", null, true);
            using (var encoded = Image.Load<TPixel>(path))
            {
                encoded.CompareToReferenceOutput(ValidatorComparer, provider, null, "webp");
            }*/
        }
    }
}
