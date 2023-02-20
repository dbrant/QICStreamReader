using System;
using System.Collections.Generic;

namespace txver45
{
    /// <summary>
    /// 
    /// Decompressor for tape images saved using the TXPLUS tool.
    /// 
    /// Copyright Dmitry Brant, 2023.
    /// 
    /// </summary>
    internal class TxDecompressor
    {
        protected Stream stream;

        private int curByte;
        private int curBitMask = 0x100;

        private readonly List<byte>[] history = new List<byte>[0x10000];
        private int histPtr = 0;

        public TxDecompressor(Stream stream)
        {
            this.stream = stream;
        }

        void pushValue(int i)
        {
            pushValue(new List<byte> { (byte)i });
        }

        void pushValue(List<byte> list)
        {
            history[histPtr] = list;
            histPtr++;
            if (histPtr >= history.Length) { histPtr = 0; }
        }

        bool execute(int instr, int offset, Stream inStream, Stream outStream)
        {
            if (instr == 0)
            {
                // literal byte
                outStream.WriteByte((byte)offset);
                pushValue(offset);
            }
            else if (instr >= 1 && instr < 0xF0)
            {
                offset += ((instr - 1) * 0x100);

                if (offset == 0)
                {
                    // start of compressed data / clear dictionary
                    for (int i = 0; i < history.Length; i++)
                        history[i] = null;
                    histPtr = 0;
                }
                else if (offset == 1)
                {
                    // end of compressed data
                    return false;
                }
                else if (offset == 2)
                {
                    // read next byte, and take that many next bytes literally
                    int n = inStream.ReadByte();

                    if (n == 0)
                    {
                        throw new ApplicationException("Warning: empty run length.");
                    }
                    for (int i = 0; i < n; i++)
                    {
                        byte a = (byte)inStream.ReadByte();
                        pushValue(a);
                        outStream.WriteByte(a);
                    }
                }
                else
                {
                    // repeat past bytes
                    offset -= 3;

                    if (offset < 0 || history[offset] == null)
                    {
                        throw new ApplicationException("Warning: invalid offset?");
                    }
                    else
                    {
                        var list = history[offset];
                        var newList = new List<byte>();

                        newList.AddRange(list);

                        offset++;
                        if (offset < history.Length && (history[offset] != null))
                        {
                            newList.Add(history[offset].First());
                        }
                        else
                        {
                            newList.Add(list.First());
                        }

                        for (int i = 0; i < newList.Count; i++)
                        {
                            outStream.WriteByte(newList[i]);
                        }
                        pushValue(newList);
                    }
                }
            }
            else
            {
                throw new ApplicationException("Warning: invalid instruction?");
            }
            return true;
        }


        public void DecompressTo(Stream outStream)
        {
            int a, instr, offset;

            while (stream.Position < stream.Length)
            {
                offset = NextNumBits(8);
                instr = NextNumBits(4);

                if (offset == 2 && instr < 2)
                {
                    if (instr == 1)
                    {
                        // repeat the next number of literal bytes

                        if (Aligned())
                        {
                            offset = NextNumBits(8);
                        }
                        else
                        {
                            offset = NextNumBits(4);
                            // if offset is 0, then the actual number is the next byte
                            if (offset == 0)
                            {
                                offset = NextNumBits(8);
                            }
                        }
                        // if offset is 0, then the actual number is 0x100
                        if (offset == 0)
                        {
                            offset = 0x100;
                        }

                        if (offset == 1)
                        {
                            throw new ApplicationException("Warning: offset seems wrong?");
                        }

                        for (int i = 0; i < offset; i++)
                        {
                            a = stream.ReadByte();
                            outStream.WriteByte((byte)a);
                            pushValue(a);
                        }
                    }
                    else
                    {
                        // literal 02 byte
                        outStream.WriteByte((byte)offset);
                        pushValue((byte)offset);
                    }
                }
                else
                {
                    if (!execute(instr, offset, stream, outStream))
                        return;
                }
            }
        }

        public bool Aligned()
        {
            return curBitMask > 0x80;
        }

        private int NextBit()
        {
            if (curBitMask > 0x80)
            {
                curByte = stream.ReadByte();
                curBitMask = 0x1;
            }
            int ret = (curByte & curBitMask) != 0 ? 1 : 0;
            curBitMask <<= 1;
            return ret;
        }

        public int NextNumBits(int bits)
        {
            int num = 0;
            for (int i = 0; i < bits; i++)
            {
                num |= (NextBit() << i);
            }
            return num;
        }
    }
}
