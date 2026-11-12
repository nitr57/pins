// SPDX-License-Identifier: MPL-2.0

using NINA.WPF.Base.SkySurvey;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NINA.Test.SkySurvey {

    /// <summary>
    /// The software sky-map composition on the headless compat layer (SkyMapRasterRendererTest is STA and does not run on
    /// Linux): every cached image is converted to Pbgra32 before it is drawn.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class SkyMapRasterRendererHeadlessTest {

        [Test]
        public void ImageWithAlpha_IsComposited() {
            System.Windows.Interop.RenderMode previousMode = RenderOptions.ProcessRenderMode;

            try {
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

                // A 4-channel tile (a PNG with alpha) reports Bgra32; its conversion to Pbgra32 used to throw.
                BitmapSource tile = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
                SkyMapScene scene = new SkyMapScene([], [], [], [], []);
                using SkyMapRasterRenderer sut = new SkyMapRasterRenderer(100, 100);

                BitmapSource result = (BitmapSource)sut.Render(scene, [new SkyMapImagePlacement(tile, new Point(50, 50), 100, 100, 0)], null);

                byte[] pixels = new byte[100 * 100 * 4];
                result.CopyPixels(pixels, 100 * 4, 0);
                int center = (50 * 100 + 50) * 4;

                Assert.That(new[] { pixels[center], pixels[center + 1], pixels[center + 2], pixels[center + 3] }, Is.EqualTo(new byte[] { 0, 0, 255, 255 }));
            } finally {
                RenderOptions.ProcessRenderMode = previousMode;
            }
        }
    }
}
