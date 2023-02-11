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
    public class Decompressor
    {
        protected Stream stream;

        protected int historySize;
        protected byte[] history;
        protected int historyPtr = 0;

        private int curByte;
        private int curBitMask = 0;

        public Decompressor(Stream stream, int historySize)
        {
            this.stream = stream;
            this.historySize = historySize;
            history = new byte[historySize];
        }

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
