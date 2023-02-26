using System;
using System.Collections.Generic;
using System.IO;

namespace QicUtils
{
    public class ALDCDecompressor : Decompressor
    {
        private const int HISTORY_SIZE_BITS = 9;
        private const int HISTORY_SIZE = 1 << HISTORY_SIZE_BITS;

        /// <summary>
        /// Decompress data from one stream to another.
        /// </summary>
        /// <param name="stream">Stream containing a single compression frame that's compressed using the QIC-122 algorithm.</param>
        /// <param name="outStream">Stream to which uncompressed data will be written.</param>
        public ALDCDecompressor(Stream stream, Stream outStream)
            : base(stream, HISTORY_SIZE)
        {
            int historySizeMask = HISTORY_SIZE - 1;

            int type, offset, length;
            byte b;

            while (stream.Position < stream.Length)
            {
                type = NextBit();
                if (type == 0)
                {
                    // raw byte
                    b = (byte)NextNumBits(8);
                    outStream.WriteByte(b);
                    history[historyPtr] = b;
                    historyPtr++;
                    historyPtr %= HISTORY_SIZE;
                }
                else
                {
                    // copy ptr
                    length = NextLength();
                    offset = NextNumBits(HISTORY_SIZE_BITS);

                    if (length >= 270)
                    {
                        // Anything greater than or equal to 270 are control codes, and are reserved.
                        // Technically the code 285 is the official "end marker" control code, but we'll
                        // just interpret any control code as the end of the stream.
                        break;
                    }

                    for (int i = 0; i < length; i++)
                    {
                        b = history[(offset + i) & historySizeMask];
                        outStream.WriteByte(b);
                        history[historyPtr] = b;
                        historyPtr++;
                        historyPtr %= HISTORY_SIZE;
                    }
                }
            }
        }

        private int NextLength()
        {
            int b;
            int a = NextNumBits(2);
            if (a < 2)
            {
                return a + 2;
            }
            else if (a == 2)
            {
                b = NextNumBits(2);
                return a + b + 2;
            }
            a <<= 1;
            a |= NextBit();
            if ((a & 0x1) == 0)
            {
                b = NextNumBits(3);
                return a + b + 2;
            }
            a <<= 1;
            a |= NextBit();
            if ((a & 0x1) == 0)
            {
                b = NextNumBits(4);
                return a + b + 2;
            }
            a = NextNumBits(4);
            a <<= 4;
            a |= NextNumBits(4);
            return a + 32;
        }
    }
}
