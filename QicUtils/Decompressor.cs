using System;
using System.IO;

namespace QicUtils
{
    /// <summary>
    /// 
    /// Base class for various decompressors.
    /// 
    /// Copyright Dmitry Brant, 2020.
    /// </summary>
    public class Decompressor(Stream stream, int historySize)
    {
        protected Stream stream = stream;

        protected int historySize = historySize;
        protected byte[] history = new byte[historySize];
        protected int historyPtr = 0;

        private int curByte;
        private int curBitMask = 0;

        protected int NextBit()
        {
            if (curBitMask == 0)
            {
                curByte = stream.ReadByte();
                curBitMask = 0x80;
            }
            int ret = (curByte & curBitMask) != 0 ? 1 : 0;
            curBitMask >>= 1;
            return ret;
        }

        protected int NextNumBits(int bits)
        {
            int num = 0;
            for (int i = 0; i < bits; i++)
            {
                num <<= 1;
                num |= NextBit();
            }
            return num;
        }
    }
}
