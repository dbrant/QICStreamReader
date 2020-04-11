using System;
using System.IO;
using System.Text;

namespace qic80reader
{
    class Program
    {
        static void Main(string[] args)
        {
            string inFileName = "";
            string outFileName = "temp.bin";
            string baseDirectory = "out";
            bool doExtract = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                if (args[i] == "-o") { outFileName = args[i + 1]; }
                if (args[i] == "-x") { doExtract = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("Phase 1 (uncompress and unframe): qic80reader -x -f <file name> -o <out file name>");
                Console.WriteLine("Phase 2 (extract file tree): qic80reader -f <phase 1 out file> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            if (doExtract)
            {
                try
                {
                    using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                    {
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
                            bool firstCompressedFrame = true;

                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 6);
                                uint absolutePos = BitConverter.ToUInt32(bytes, 0);
                                int frameSize = BitConverter.ToUInt16(bytes, 4);

                                bool compressed = (frameSize & 0x8000) == 0;
                                frameSize &= 0x7FFF;

                                if (compressed && firstCompressedFrame)
                                {
                                    firstCompressedFrame = false;
                                    // pad to 32k
                                    if ((outStream.Position % 0x8000) > 0) {
                                        Array.Clear(bytes, 0, bytes.Length);
                                        outStream.Write(bytes, 0, 0x8000 - (int)(outStream.Position % 0x8000));
                                    }
                                }

                                stream.Read(bytes, 0, frameSize);

                                if (compressed)
                                {
                                    new Qic122Decompressor(new MemoryStream(bytes), outStream);
                                }
                                else
                                {
                                    outStream.Write(bytes, 0, frameSize);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Error: " + e.Message);
                }
                return;
            }


            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    // Blow through the catalog at the start of the file
                    while (stream.Position < stream.Length)
                    {
                        var rec = new CatalogRecord(stream, false);
                        if (!rec.Valid) { break; }
                    }

                    while (stream.Position < stream.Length)
                    {
                        // align to 0x8000
                        if (stream.Position % 0x8000 > 0)
                        {
                            stream.Seek(0x8000 - (stream.Position % 0x8000), SeekOrigin.Current);
                        }

                        stream.Read(bytes, 0, 4);
                        if (BitConverter.ToUInt32(bytes, 0) == 0x33CC33CC)
                        {
                            stream.Seek(-4, SeekOrigin.Current);
                            break;
                        }
                    }

                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 4);
                        if (BitConverter.ToUInt32(bytes, 0) != 0x33CC33CC) { break; }

                        var rec = new CatalogRecord(stream, true);
                        if (!rec.Valid)
                        {
                            Console.WriteLine("WARNING: invalid record found (probably end of archive).");
                            break;
                        }

                        if (!rec.isDirectory && rec.Size > 0)
                        {
                            // output file!
                            byte[] tempBytes = new byte[rec.Size];
                            stream.Read(tempBytes, 0, (int)rec.Size);

                            string fileName = baseDirectory;
                            if (rec.Path.Length > 0)
                            {
                                fileName += Path.DirectorySeparatorChar + rec.Path;
                            }
                            Directory.CreateDirectory(fileName);
                            fileName += Path.DirectorySeparatorChar + rec.Name;

                            Console.WriteLine(fileName + "\t" + rec.Size + "\t" + rec.Date);

                            using (var f = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                            {
                                f.Write(tempBytes, 0, tempBytes.Length);
                            }
                        }
                        else if (rec.Size > 0)
                        {
                            stream.Seek(rec.Size, SeekOrigin.Current);
                        }
                    }

                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }


        /*
        private static void TraverseCatalog(Stream stream, String dirName, String baseDir)
        {
            byte[] tempBytes = new byte[16];
            var queue = new Queue<CatalogRecord>();
            while (stream.Position < stream.Length)
            {
                stream.Read(tempBytes, 0, 4);
                if (BitConverter.ToUInt32(tempBytes, 0) != 0x33CC33CC) { break; }

                var rec = new CatalogRecord(stream, true);
                if (!rec.Valid) { return; }
                queue.Enqueue(rec);

                Console.WriteLine(dirName + "\\" + rec.Name + "\t" + rec.Size + "\t" + rec.Date);

                if (rec.Size > 0)
                {
                    stream.Seek(rec.Size, SeekOrigin.Current);
                }

                if (rec.isLastEntry || rec.isFinalEntry) { break; }
            }

            while (queue.Count > 0)
            {
                var rec = queue.Dequeue();
                if (rec.isDirectory)
                {
                    Traverse(stream, dirName + Path.DirectorySeparatorChar + rec.Name, baseDir);
                }
            }
        }
        */

        private class Qic122Decompressor
        {
            private Stream stream;

            private int curByte;
            private int curBitMask = 0;

            private byte[] history;
            private int historyPtr;

            public Qic122Decompressor(Stream stream, Stream outStream)
            {
                this.stream = stream;
                history = new byte[0x10000];

                int type, offset, length;
                byte b;

                while (true)
                {
                    type = NextBit();
                    if (type == 0)
                    {
                        // raw byte
                        b = (byte)NextNumBits(8);
                        outStream.WriteByte(b);
                        history[historyPtr++] = b;
                    }
                    else
                    {
                        // compressed bytes
                        offset = NextOffset();
                        if (offset == 0) { break; }

                        length = NextLength();

                        for (int i = 0; i < length; i++)
                        {
                            b = history[historyPtr - offset];
                            outStream.WriteByte(b);
                            history[historyPtr++] = b;
                        }
                    }

                    if (historyPtr > 0x8000)
                    {
                        Array.Copy(history, 0x4000, history, 0, historyPtr - 0x4000);
                        historyPtr -= 0x4000;
                    }
                }
            }

            private int NextBit()
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

            private int NextOffset()
            {
                int type = NextBit();
                int offsetLen = type == 1 ? 7 : 11;
                return NextNumBits(offsetLen);
            }

            private int NextLength()
            {
                int length = 2;
                length += NextNumBits(2);
                if (length < 5) { return length; }
                length += NextNumBits(2);
                if (length < 8) { return length; }

                int chunk;
                while (true)
                {
                    chunk = NextNumBits(4);
                    length += chunk;
                    if (chunk < 0xF) { break; }
                }
                return length;
            }

            private int NextNumBits(int bits)
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


        private class CatalogRecord
        {
            public bool Valid;

            public string Name;
            public long Size;
            public FileAttributes Attrs;
            public DateTime Date;
            public string Path = "";

            public bool isDirectory;
            public bool isLastEntry;
            public bool isFinalEntry;

            public CatalogRecord(Stream stream, bool getPath)
            {
                long initialPos = stream.Position;
                byte[] bytes = new byte[0xFF];
                int dataLen = stream.ReadByte();
                if (dataLen == 0) { return; }

                stream.Read(bytes, 0, dataLen);

                int flags = bytes[0];
                if ((flags & 0x2) == 0) { Attrs |= FileAttributes.ReadOnly; }
                if ((flags & 0x8) != 0) { Attrs |= FileAttributes.Hidden; }
                if ((flags & 0x10) != 0) { Attrs |= FileAttributes.System; }
                if ((flags & 0x20) != 0) { isDirectory = true; }
                if ((flags & 0x40) != 0) { isLastEntry = true; }
                if ((flags & 0x80) != 0) { isFinalEntry = true; }

                Date = GetDateTime(BitConverter.ToUInt32(bytes, 1));
                Size = BitConverter.ToUInt32(bytes, 5);

                int nameLen = stream.ReadByte();
                stream.Read(bytes, 0, nameLen);
                Name = CleanString(Encoding.ASCII.GetString(bytes, 0, nameLen));

                if (getPath)
                {
                    nameLen = stream.ReadByte();
                    if (nameLen > 0)
                    {
                        stream.Read(bytes, 0, nameLen);
                        Path = Encoding.ASCII.GetString(bytes, 0, nameLen).Replace('\0', '\\');
                    }
                }

                Size -= (stream.Position - initialPos) + 4;
                Valid = true;
            }
        }


        private class FormatParamRecord
        {
            public bool Valid;

            public int FormatCode;
            public int Revision;
            public int HeaderSegNum;
            public int HeaderSegDupNum;
            public int DataSegFirstLogicalArea;
            public int DataSegLastLogicalArea;
            public DateTime MostRecentFormat;
            public DateTime MostRecentWriteOrFormat;
            public int SegmentsPerTrack;
            public int TracksPerCartridge;
            public int MaxFloppySide;
            public int MaxFloppyTrack;
            public int MaxFloppySector;
            public string TapeName;
            public DateTime TapeNameTime;
            public int ReformatErrorFlag;
            public int NumSegmentsWritten;
            public DateTime InitialFormatTime;
            public int FormatCount;
            public string ManufacturerName;
            public string ManufacturerLotCode;


            public FormatParamRecord(Stream stream)
            {
                byte[] bytes = new byte[0xFF];
                long initialPos = stream.Position;
                int ptr = 0;

                stream.Read(bytes, 0, 0xFF);

                uint magic = BitConverter.ToUInt32(bytes, ptr); ptr += 4;
                if (magic != 0xAA55AA55)
                {
                    return;
                }

                FormatCode = bytes[ptr++];
                Revision = bytes[ptr++];

                HeaderSegNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                HeaderSegDupNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                DataSegFirstLogicalArea = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                DataSegLastLogicalArea = BitConverter.ToUInt16(bytes, ptr); ptr += 2;

                MostRecentFormat = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                MostRecentWriteOrFormat = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                ptr += 2;

                SegmentsPerTrack = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                TracksPerCartridge = bytes[ptr++];
                MaxFloppySide = bytes[ptr++];
                MaxFloppyTrack = bytes[ptr++];
                MaxFloppySector = bytes[ptr++];

                TapeName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;

                TapeNameTime = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;

                ptr = 128;
                ReformatErrorFlag = bytes[ptr++];
                ptr++;
                NumSegmentsWritten = BitConverter.ToInt32(bytes, ptr); ptr += 4;
                ptr += 4;
                InitialFormatTime = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                FormatCount = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                ptr += 2;

                ManufacturerName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
                ManufacturerLotCode = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;

                Valid = true;
            }
        }

        private static string CleanString(string str)
        {
            return str.Replace("\0", "").Trim();
        }

        private static DateTime GetDateTime(uint date)
        {
            DateTime d = new DateTime();
            int year = (int)((date & 0xFE000000) >> 25) + 1970;
            int s = (int)(date & 0x1FFFFFF);
            int second = s % 60; s /= 60;
            int minute = s % 60; s /= 60;
            int hour = s % 24; s /= 24;
            int day = s % 31; s /= 31;
            int month = s;
            try
            {
                d = new DateTime(year, month + 1, day + 1, hour, minute, second);
            }
            catch { }
            return d;
        }
    }
}
