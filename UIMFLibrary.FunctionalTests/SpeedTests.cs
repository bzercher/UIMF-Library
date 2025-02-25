﻿// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   Speed tests.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
#if (EXPERIMENTAL)
using BenchmarkIt;
#endif
using NUnit.Framework;

namespace UIMFLibrary.FunctionalTests
{
    /// <summary>
    /// The speed tests.
    /// </summary>
    [TestFixture]
    public class SpeedTests
    {
        #region Fields

        /// <summary>
        /// Standard non-multiplexed file
        /// </summary>
        private const string uimfStandardFile1 = @"\\proto-2\UnitTest_Files\DeconTools_TestFiles\UIMF\Sarc_MS2_90_6Apr11_Cheetah_11-02-19.uimf";

        #endregion

        #region Public Methods and Operators


        public static void PrintMethodName(System.Reflection.MethodBase methodInfo)
        {
            // Call with PrintMethodName(System.Reflection.MethodBase.GetCurrentMethod());

            var nameSpace = "?";
            var className = "?";

            if (methodInfo.ReflectedType != null)
            {
                nameSpace = methodInfo.ReflectedType.Namespace;
                className = methodInfo.ReflectedType.Name;
            }

            var methodDescriptor = nameSpace + ".";

            if (nameSpace != null && nameSpace.EndsWith("." + className))
            {
                methodDescriptor += methodInfo.Name;
            }
            else
            {
                methodDescriptor += className + "." + methodInfo.Name;
            }

            Console.WriteLine($"{Environment.NewLine}{Environment.NewLine}===== " + methodDescriptor + " =====");

        }

#if (EXPERIMENTAL)
        [Test]
        public void CompressionComparisonSpeedTest()
        {

            /*
             * Example run on core i9 7900X @ 4GHz.
             * Name                                    Milliseconds        Percent
             * LZ4 Compress                            163                 193.3%
             * ZRLE LZ4 Compress                       84                  100%
             * CLZF2 Compress                          945                 1118.5%
             * ZREL CLZF2 Compress                     91                  107.7%
             * Name                                    Milliseconds        Percent
             * LZ4 Decompress                          325                 16880.7%
             * ZRLE LZ4 Decompress                     1                   100%
             * CLZF2 Decompress                        1176                61106.9%
             * ZREL CLZF2 Decompress                   8                   448.4%
             */

            var intensities = new int[148000];
            var randGenerator = new Random();
            for (var i = 0; i < intensities.Length; i++)
            {
                var nextRandom = randGenerator.Next(0, 255);
                if (nextRandom < 245)
                    intensities[i] = 0;
                else
                    intensities[i] = nextRandom;

            }

            var spectra = new byte[intensities.Length * sizeof(int)];
            Buffer.BlockCopy(intensities, 0, spectra, 0, spectra.Length);
            var decompressedIntensities = new int[intensities.Length];
            byte[] zrleLz4Result = new byte[] { };
            byte[] clzf2Result = new byte[] { };
            byte[] zrleClzf2Result = new byte[] { };
            byte[] snappyResult = new byte[] { };
            byte[] zrleSnappyResult = new byte[] { };

            Benchmark.This("CLZF2 Compress", () =>
            {
                clzf2Result = CLZF2.Compress(spectra);
            }).WithWarmup(100).Against.This("ZREL CLZF2 Compress", () =>
            {
                IntensityConverterCLZF.Compress(intensities, out zrleClzf2Result, out var tic, out var bpi,
                    out var indexOfMaxIntensity);
            }).WithWarmup(100).Against.This("Snappy", () =>
                {
                    snappyResult = Snappy.SnappyCodec.Compress(spectra);
                }).WithWarmup(100).Against.This("ZRLE Snappy", () =>
                {
                    IntensityConverterInt32.EncodeSnappy(intensities, out zrleSnappyResult, out var tic, out var bpi,
                        out var indexOfMaxIntensity);
                }).WithWarmup(100)

            .For(100).Iterations().PrintComparison();

            Console.WriteLine($"Snappy Compress Size: {snappyResult.Length}");
            Console.WriteLine($"ZRLE Snappy Compress Size: {zrleSnappyResult.Length}");
            Console.WriteLine($"CLZF2 Compress Size: {clzf2Result.Length}");
            Console.WriteLine($"ZRLE CLZF2 Compress Size: {zrleClzf2Result.Length}");
            Console.WriteLine();

            Benchmark.This("Snappy Decompress", () =>
            {
                var result = Snappy.SnappyCodec.Uncompress(snappyResult);
            }).WithWarmup(100).Against.This("ZRLE Snappy Decompress", () =>
            {
                var result = Snappy.SnappyCodec.Uncompress(zrleSnappyResult);
            }).WithWarmup(100).Against.This("CLZF2 Decompress", () =>
            {
                var result = CLZF2.Decompress(clzf2Result);
            }).WithWarmup(100).Against.This("ZREL CLZF2 Decompress", () =>
            {
                var result = CLZF2.Decompress(zrleClzf2Result);
            }).WithWarmup(100).For(100).Iterations().PrintComparison();

        }
#endif

        /// <summary>
        /// Summed mass spectrum speed tests.
        /// </summary>
        [Test]
        [Category("PNL_Domain")]
        public void GetSummedMassSpectrumSpeedTests()
        {
            PrintMethodName(System.Reflection.MethodBase.GetCurrentMethod());

            const int numIterations = 100;

            // int numFramesToSum = 1;
            const int numIMSScansToSum = 7;

            using (var dr = new DataReader(uimfStandardFile1))
            {

                const int frameStart = 500;
                const int frameStop = frameStart + numIterations;
                const int scanStart = 250;
                const int scanStop = scanStart + numIMSScansToSum - 1;

                long nonZeroValues = 0;
                long mzValuesRead = 0;
                long intensityValuesRead = 0;

                var sw = new Stopwatch();
                sw.Start();
                for (var frame = frameStart; frame < frameStop; frame++)
                {

                    var nonZeros = dr.GetSpectrum(
                        frame,
                        frame,
                        UIMFData.FrameType.MS1,
                        scanStart,
                        scanStop,
                        out var mzValues,
                        out var intensities);

                    nonZeroValues += nonZeros;
                    mzValuesRead += mzValues.Length;
                    intensityValuesRead += intensities.Length;
                }

                sw.Stop();

                Assert.AreEqual(mzValuesRead, intensityValuesRead, "Read a differing number of m/z versus intensity values");

                Console.WriteLine($"Total time to read {numIterations} scans = {sw.ElapsedMilliseconds} msec");
                Console.WriteLine($"Average time/scan = {sw.ElapsedMilliseconds / (double)numIterations} msec");

                Console.WriteLine("Read {0} data points, of which {1} were non-zero", mzValuesRead, nonZeroValues);

            }
        }

        /// <summary>
        /// Single summed mass spectrum test 1.
        /// </summary>
        [Test]
        [Category("PNL_Domain")]
        public void GetSingleSummedMassSpectrumTest1()
        {
            PrintMethodName(System.Reflection.MethodBase.GetCurrentMethod());

            var dr = new DataReader(uimfStandardFile1);
            var gp = dr.GetGlobalParams();

            var intensities = new int[gp.Bins];
            var mzValues = new double[gp.Bins];

            // int startFrame = 500;
            // int stopFrame = 502;
            // int startScan = 250;
            // int stopScan = 256;

            // int nonZeros = dr.SumScansNonCached(mzValues, intensities, 0, startFrame, stopFrame, startScan, stopScan);

            // TestUtilities.displayRawMassSpectrum(mzValues, intensities);
        }

        #endregion
    }
}