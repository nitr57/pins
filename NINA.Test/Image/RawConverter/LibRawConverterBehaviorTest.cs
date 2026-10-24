using FluentAssertions;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NUnit.Framework;
using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Image.RawConverter {

    [TestFixture]
    public class LibRawConverterBehaviorTest {

        /// <summary>
        /// Verifies LibRaw visible-area color codes are translated into the Bayer pattern used by N.I.N.A.'s debayer pipeline.
        /// </summary>
        [TestCase("RGGB", SensorType.RGGB)]
        [TestCase("GBRG", SensorType.GBRG)]
        [TestCase("GRBG", SensorType.GRBG)]
        [TestCase("BGGR", SensorType.BGGR)]
        [TestCase("RGBG", SensorType.RGBG)]
        [TestCase("GRGB", SensorType.GRGB)]
        [TestCase("GBGR", SensorType.GBGR)]
        [TestCase("BGRG", SensorType.BGRG)]
        public void TryGetBayerPattern_MapsVisibleLibRawPatternToSensorType(string visiblePattern, SensorType expectedSensorType) {
            (bool mapped, SensorType sensorType) = TryGetBayerPattern(visiblePattern);

            mapped.Should().BeTrue();
            sensorType.Should().Be(expectedSensorType);
        }

        /// <summary>
        /// Verifies unsupported CFA color descriptions are not misreported as Bayer patterns.
        /// </summary>
        [Test]
        public void TryGetBayerPattern_RejectsUnsupportedPattern() {
            (bool mapped, SensorType sensorType) = TryGetBayerPattern("GCMY");

            mapped.Should().BeFalse();
            sensorType.Should().Be(SensorType.Monochrome);
        }

        [Test]
        public void CopyUshortFrame_ScalesConfiguredBitDepthTo16BitWhenEnabled() {
            var source = new ushort[] { 1, 128, 4095 };

            var copiedFrame = CopyUshortFrame(source, width: 3, height: 1, bitDepth: 12, bitScaling: true);

            copiedFrame.OutputBitDepth.Should().Be(16);
            copiedFrame.EffectiveBitDepth.Should().Be(12);
            copiedFrame.MaxPixelValue.Should().Be(4095);
            copiedFrame.Pixels.Should().Equal(16, 2048, 65520);
        }

        [Test]
        public void CopyUshortFrame_LeavesPixelsAndBitDepthUnchangedWhenScalingIsDisabled() {
            var source = new ushort[] { 1, 128, 4095 };

            var copiedFrame = CopyUshortFrame(source, width: 3, height: 1, bitDepth: 12, bitScaling: false);

            copiedFrame.OutputBitDepth.Should().Be(12);
            copiedFrame.EffectiveBitDepth.Should().Be(12);
            copiedFrame.MaxPixelValue.Should().Be(4095);
            copiedFrame.Pixels.Should().Equal(1, 128, 4095);
        }

        [Test]
        public void CopyUshortFrame_UsesObservedBitDepthWhenConfiguredBitDepthIsTooLow() {
            var source = new ushort[] { 1, 12000 };

            var copiedFrame = CopyUshortFrame(source, width: 2, height: 1, bitDepth: 12, bitScaling: true);

            copiedFrame.OutputBitDepth.Should().Be(16);
            copiedFrame.EffectiveBitDepth.Should().Be(14);
            copiedFrame.MaxPixelValue.Should().Be(12000);
            copiedFrame.Pixels.Should().Equal(4, 48000);
        }

        [Test]
        public void CopyUshortFrame_RaisesUnscaledOutputBitDepthWhenConfiguredBitDepthIsTooLow() {
            var source = new ushort[] { 1, 12000 };

            var copiedFrame = CopyUshortFrame(source, width: 2, height: 1, bitDepth: 12, bitScaling: false);

            copiedFrame.OutputBitDepth.Should().Be(14);
            copiedFrame.EffectiveBitDepth.Should().Be(14);
            copiedFrame.MaxPixelValue.Should().Be(12000);
            copiedFrame.Pixels.Should().Equal(1, 12000);
        }

        /// <summary>
        /// Verifies the reported bit depth follows the reach of the RAW data (white level and brightest pixel) and is
        /// only ever lowered, so pixel values and a too-low profile bit depth keep their existing handling.
        /// </summary>
        // Profile 16 bit (the default), 14-bit NEF: the white level limits the reported range to 14 bits.
        [TestCase(16, 16, (ushort)15000, 15520, 14)]
        // Profile 16 bit, 12-bit NEF.
        [TestCase(16, 16, (ushort)3880, 3880, 12)]
        // No white level from LibRaw: keep the bit depth the copy produced.
        [TestCase(16, 16, (ushort)15000, 0, 16)]
        // A white level that fills 16 bits (e.g. some DNGs) keeps 16 bits.
        [TestCase(16, 16, (ushort)15000, 65535, 16)]
        // A pixel above the white level keeps the bits needed to hold it.
        [TestCase(16, 16, (ushort)5000, 4095, 13)]
        // Profile 14 bit with bit scaling, 14-bit data: scaled to 16 bits, which the data now fills.
        [TestCase(16, 14, (ushort)15000, 15520, 16)]
        // Profile 14 bit with bit scaling, 12-bit data: scaled by 4, so it reaches 14 bits.
        [TestCase(16, 14, (ushort)3880, 3880, 14)]
        // Profile 12 bit without scaling, 14-bit data: the copy already raised the bit depth to 14.
        [TestCase(14, 14, (ushort)12000, 15520, 14)]
        // Profile 12 bit with bit scaling on a dark 14-bit frame: the white level would not fit after the shift.
        [TestCase(16, 12, (ushort)3000, 15520, 16)]
        public void GetDataBitDepth_LowersBitDepthToTheReachOfTheRawData(
            int outputBitDepth,
            int effectiveBitDepth,
            ushort maxPixelValue,
            int whiteLevel,
            int expectedBitDepth) {
            var bitDepth = GetDataBitDepth(outputBitDepth, effectiveBitDepth, maxPixelValue, whiteLevel);

            bitDepth.Should().Be(expectedBitDepth);
        }

        [Test]
        public async Task ObsoleteConvertOverload_DisablesBitScalingForCompatibility() {
            IRawConverter converter = new RawConverterStub();

#pragma warning disable CS0618
            await converter.Convert(new MemoryStream(new byte[] { 1 }), 12, "dng", new ImageMetaData(), CancellationToken.None);
#pragma warning restore CS0618

            ((RawConverterStub)converter).BitScaling.Should().BeFalse();
        }

        private static (bool Mapped, SensorType SensorType) TryGetBayerPattern(string visiblePattern) {
            Type converterType = typeof(ImageDataFactory).Assembly.GetType(
                "NINA.Image.RawConverter.LibRawConverter",
                throwOnError: true)!;
            MethodInfo method = converterType.GetMethod(
                "TryGetBayerPattern",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            object[] parameters = { visiblePattern, SensorType.Monochrome };
            var mapped = (bool)method.Invoke(null, parameters)!;
            return (mapped, (SensorType)parameters[1]);
        }

        private static int GetDataBitDepth(int outputBitDepth, int effectiveBitDepth, ushort maxPixelValue, int whiteLevel) {
            Type converterType = typeof(ImageDataFactory).Assembly.GetType(
                "NINA.Image.RawConverter.LibRawConverter",
                throwOnError: true)!;
            MethodInfo method = converterType.GetMethod(
                "GetDataBitDepth",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            return (int)method.Invoke(null, new object[] { outputBitDepth, effectiveBitDepth, maxPixelValue, whiteLevel })!;
        }

        private static (ushort[] Pixels, ushort MaxPixelValue, int EffectiveBitDepth, int OutputBitDepth) CopyUshortFrame(
            ushort[] source,
            int width,
            int height,
            int bitDepth,
            bool bitScaling) {
            Type converterType = typeof(ImageDataFactory).Assembly.GetType(
                "NINA.Image.RawConverter.LibRawConverter",
                throwOnError: true)!;
            Type activeFrameType = converterType.GetNestedType("ActiveFrame", BindingFlags.NonPublic)!;
            object activeFrame = Activator.CreateInstance(
                activeFrameType,
                0,
                0,
                width,
                height,
                width)!;
            MethodInfo method = converterType.GetMethod(
                "CopyUshortFrame",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
            try {
                object result = method.Invoke(null, new object[] { handle.AddrOfPinnedObject(), activeFrame, bitDepth, bitScaling })!;
                Type resultType = result.GetType();
                return (
                    (ushort[])resultType.GetProperty("Pixels")!.GetValue(result)!,
                    (ushort)resultType.GetProperty("MaxPixelValue")!.GetValue(result)!,
                    (int)resultType.GetProperty("EffectiveBitDepth")!.GetValue(result)!,
                    (int)resultType.GetProperty("OutputBitDepth")!.GetValue(result)!);
            } finally {
                handle.Free();
            }
        }

        private sealed class RawConverterStub : IRawConverter {
            public bool? BitScaling { get; private set; }

            public Task<IImageData> Convert(
                MemoryStream s,
                int bitDepth,
                bool bitScaling,
                string rawType,
                ImageMetaData metaData,
                CancellationToken token = default) {
                BitScaling = bitScaling;
                return Task.FromResult<IImageData>(default!);
            }
        }
    }
}
