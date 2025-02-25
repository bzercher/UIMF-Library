﻿using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace UIMFLibrary.UnitTests
{
    [TestFixture]
    public class EncodingTests
    {
        private const int BinCount = 148000;
        private readonly List<KeyValuePair<int, int>> testValues = new List<KeyValuePair<int, int>>() {new KeyValuePair<int, int>(49693, 8)};
        private int[] testInputData;
        private short[] testInputDataShort;
        private readonly int[] rlzEncodedOldImsData = new int[] { -32768, 0, -16925, 8, -32768, 0, -32768, 0, -32768, 0 };
        private readonly short[] rlzEncodedOldImsDataShort = new short[] { -32768, 0, -16925, 8, -32768, 0, -32768, 0, -32768, 0 };
        private readonly int[] rlzEncodedCurrentData = new int[] { -49693, 8 };
        private readonly short[] rlzEncodedCurrentDataShort = new short[] { -32768, -16925, 8, -32768, -32768, -32768 };

        [OneTimeSetUp]
        public void CreateTestData()
        {
            testInputData = new int[BinCount];
            testInputDataShort = new short[BinCount];
            foreach (var value in testValues)
            {
                testInputData[value.Key] = value.Value;
                testInputDataShort[value.Key] = (short)value.Value;
            }
        }

        [Test]
        public void RlzEncodeRoundTripTest1()
        {
            var encoded = RlzEncode(testInputData);
            var decoded = RlzDecode(encoded, BinCount);

            for (var i = 0; i < BinCount; i++)
            {
                Assert.AreEqual(testInputData[i], decoded[i], 0, "Mismatch at bin {0}", i);
            }
        }

        [Test]
        public void RlzEncodeRoundTripTest2()
        {
            var decoded = RlzDecode(rlzEncodedCurrentData, BinCount);
            var encoded = RlzEncode(decoded);

            Assert.AreEqual(rlzEncodedCurrentData.Length, encoded.Length);

            for (var i = 0; i < encoded.Length; i++)
            {
                Assert.AreEqual(rlzEncodedCurrentData[i], encoded[i], 0, "Mismatch at index {0}", i);
            }
        }

        [Test]
        public void RlzEncodeTest()
        {
            var encoded = RlzEncode(testInputData);
            Assert.AreEqual(rlzEncodedCurrentData.Length, encoded.Length);
            for (var i = 0; i < rlzEncodedCurrentData.Length; i++)
            {
                Assert.AreEqual(rlzEncodedCurrentData[i], encoded[i], 0, "Mismatch at index {0}", i);
            }
        }

        [Test]
        public void RlzDecodeOldImsDataTest()
        {
            var decoded = RlzDecode(rlzEncodedOldImsData, BinCount);

            for (var i = 0; i < BinCount; i++)
            {
                Assert.AreEqual(testInputData[i], decoded[i], 0, "Mismatch at bin {0}", i);
            }
        }

        [Test]
        public void RlzEncodeShortTest()
        {
            var encoded = RlzEncode(testInputDataShort);
            Assert.AreEqual(rlzEncodedCurrentDataShort.Length, encoded.Length);
            for (var i = 0; i < rlzEncodedCurrentDataShort.Length; i++)
            {
                Assert.AreEqual(rlzEncodedCurrentDataShort[i], encoded[i], 0, "Mismatch at index {0}", i);
            }
        }

        [Test]
        public void RlzEncodeOldImsShortTest()
        {
            var encoded = RlzEncodeOld(testInputDataShort);
            Assert.AreEqual(rlzEncodedOldImsDataShort.Length, encoded.Length);
            for (var i = 0; i < rlzEncodedOldImsDataShort.Length; i++)
            {
                Assert.AreEqual(rlzEncodedOldImsDataShort[i], encoded[i], 0, "Mismatch at index {0}", i);
            }
        }

        private static int[] RlzDecode(IReadOnlyList<int> rlzData, int binCount)
        {
            var intensityArray = new int[binCount];
            var decoded = UIMFLibrary.RlzEncode.Decode(rlzData);
            foreach (var nzPoint in decoded)
            {
                if (nzPoint.Item1 < binCount)
                {
                    intensityArray[nzPoint.Item1] = nzPoint.Item2;
                }
                else
                {
                    Console.WriteLine("Warning: index out of bounds in RlzDecode: {0} > {1} ", nzPoint.Item1, binCount);
                    break;
                }
            }

            return intensityArray;
        }

        private static int[] RlzEncode(IReadOnlyList<int> intensities)
        {
            return UIMFLibrary.RlzEncode.Encode(intensities);
        }

        private static short[] RlzEncode(IReadOnlyList<short> intensities)
        {
            return UIMFLibrary.RlzEncode.Encode(intensities);
        }

        private short[] RlzEncodeOld(IList<short> intensities)
        {
            var rlzeDataList = new List<short>();
            var zeroCount = 0;

            // run length zero encoding
            for (var i = 0; i < intensities.Count; i++)
            {
                var intensity = intensities[i];
                if (intensity > 0)
                {
                    if (zeroCount < 0)
                    {
                        rlzeDataList.Add((short)zeroCount);
                        zeroCount = 0;
                    }

                    rlzeDataList.Add(intensity);
                }
                else
                {
                    if (zeroCount == short.MinValue)
                    {
                        // Too many zeroes; need to append two points to rlzeDataList to avoid an overflow
                        rlzeDataList.Add((short)zeroCount);
                        rlzeDataList.Add((short)0); // This is the bug.
                        zeroCount = 0;
                    }

                    // Always count the zero
                    zeroCount--;
                }
            }
            // We don't care about any zeroes/zeroCount after the last non-zero value; it's better if we don't append them to rlzeDataList.

            return rlzeDataList.ToArray();
        }
    }
}
