using QicUtils;
using System.Text;


/// <summary>
/// 
/// Decoder for tape images saved using the TXPLUS tool.
/// 
/// Copyright Dmitry Brant, 2023.
/// 
/// 
/// * Sectors are 512 bytes, and all sectors end with a 2-byte checksum, so there
///   are 510 usable bytes per sector.
/// * All data fields are little-endian.
/// 
/// The basic structure of the archive is as follows:
/// [tape header sector]
/// [file header]
/// [file contents]
/// [file header] <-- aligned to next sector
/// [file contents]
/// [file header] <-- aligned to next sector
/// [file contents]
/// ...
/// 
/// * The [tape header sector] starts with the magic string "?TXVer-45", although
/// there may be other "Ver" numbers out there.
/// 
/// * Each [file header] is 0x60 bytes long, and has the following structure:
/// 
/// Byte offset (hex)         Meaning
/// ------------------------------------------------------------------------------
/// 0x0                       Magic bytes 0x3A3A3A3A
/// 0x4                       File length (32 bit)
/// 0x8                       File date/time (32-bit MS-DOS date-time format)
/// 0xC                       File attributes (1 byte, followed by 3 more padding
///                           bytes of 0x3A)
/// 0x10                      File name (full path, including drive letter, padded
///                           with null bytes until the end of the header)
/// 
/// * The header is followed immediately by the file contents.
/// * If the contents start with a null byte (0x0), it means that the file is
/// compressed, otherwise the file is uncompressed.
/// * The "file length" in the header is always the uncompressed length of the file.
/// * When reading the file contents, remember that every sector ends with a 2-byte
/// checksum, which is _not_ part of the file contents, and should be ignored (or
/// ideally used to verify the sector bytes).
/// * If the file contents don't end on a sector boundary, they are padded with bytes
/// of value 0x50 until the start of the next sector, which will be the next file
/// header, or the end of the archive.
/// 
/// </summary>
namespace txver45
{
    class Program
    {
        private const uint FileHeaderBlock = 0x3A3A3A3A;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--dry") { dryRun = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: txver45 -f <file name> [-d <output directory>]");
                return;
            }

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
            while (stream.Position < stream.Length)
            {
                // Make sure we're aligned properly.
                if ((stream.Position % 0x100) > 0)
                {
                    stream.Seek(0x100 - (stream.Position % 0x100), SeekOrigin.Current);
                }

                var header = new FileHeader(stream);
                if (!header.Valid)
                {
                    continue;
                }

                if (header.IsDirectory || header.Name.Trim() == "")
                {
                    Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - " + header.Size.ToString() + " bytes");
                    continue;
                }

                if (header.Size == 0)
                {
                    Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name);
                    Console.WriteLine("Warning: skipping zero-length file.");
                    continue;
                }


                bool compressed = false;
                {
                    int byte1 = stream.ReadByte();
                    int byte2 = stream.ReadByte();
                    if (header.Size > 0x190)
                    {
                        compressed = byte1 == 0;
                    }
                    else
                    {
                        compressed = byte1 == 0 && ((byte2 & 0xf) == 1);
                    }
                    stream.Seek(-2, SeekOrigin.Current);
                }
                    
                string filePath = baseDirectory;
                string[] dirArray = header.Name.Split("\\");
                string fileName = Utils.ReplaceInvalidChars(dirArray[^1]);
                for (int i = 0; i < dirArray.Length - 1; i++)
                {
                    filePath = Path.Combine(filePath, Utils.ReplaceInvalidChars(dirArray[i]));
                }

                if (!dryRun)
                {
                    Directory.CreateDirectory(filePath);

                    filePath = Path.Combine(filePath, fileName);

                    // Make sure the fully qualified name does not exceed 260 chars
                    if (filePath.Length >= 260)
                    {
                        filePath = filePath[..259];
                    }

                    while (File.Exists(filePath))
                    {
                        Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                        filePath += "_";
                    }

                    Console.WriteLine("(" + (compressed ? "c" : " ") + ")" + stream.Position.ToString("X") + ": " + filePath + " - "
                        + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                    long posBeforeRead = stream.Position;
                    int bytesPerChunk = 0x200;

                    if (compressed)
                    {
                        // read the whole file into a memory buffer, so that we can pass it into the decompressor when ready.
                        using var memStream = new MemoryStream();
                        // initialize, subtracting the initial size of the header.
                        int bytesToRead = bytesPerChunk - FileHeader.HEADER_SIZE;
                        int bytesToWrite = 0;
                        // Make the total bytes overshoot by a good amount, because the compression was so poor
                        // that it often made the compressed file take up more space than the original.
                        long bytesLeft = header.Size * 4;
                        var bytes = new byte[bytesPerChunk];

                        while (bytesLeft > 0)
                        {
                            stream.Read(bytes, 0, bytesToRead);
                            bytesToWrite = bytesToRead - 2;
                            bytesToRead = bytesPerChunk;

                            if (bytesToWrite > bytesLeft)
                            {
                                bytesToWrite = (int)bytesLeft;
                            }
                            memStream.Write(bytes, 0, bytesToWrite);

                            bytesLeft -= bytesToWrite;
                        }
                        memStream.Seek(0, SeekOrigin.Begin);

                        using var f = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                        try
                        {
                            new TxDecompressor(memStream).DecompressTo(f);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }
                        f.Flush();
                    }
                    else
                    {
                        using var f = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                        // initialize, subtracting the initial size of the header.
                        int bytesToRead = bytesPerChunk - FileHeader.HEADER_SIZE;
                        int bytesToWrite = 0;
                        long bytesLeft = header.Size;
                        var bytes = new byte[bytesPerChunk];

                        while (bytesLeft > 0)
                        {
                            stream.Read(bytes, 0, bytesToRead);
                            bytesToWrite = bytesToRead - 2;
                            bytesToRead = bytesPerChunk;

                            if (bytesToWrite > bytesLeft)
                            {
                                bytesToWrite = (int)bytesLeft;
                            }
                            f.Write(bytes, 0, bytesToWrite);

                            bytesLeft -= bytesToWrite;
                        }
                        f.Flush();
                    }

                    if (compressed)
                    {
                        // Rewind the stream back to the beginning of the current file.
                        stream.Seek(posBeforeRead, SeekOrigin.Begin);
                    }

                    try
                    {
                        File.SetCreationTime(filePath, header.CreateDate);
                        File.SetLastWriteTime(filePath, header.ModifyDate);
                        File.SetAttributes(filePath, header.Attributes);
                    }
                    catch { }
                }
                else
                {
                    filePath = Path.Combine(filePath, fileName);
                    Console.WriteLine("(" + (compressed ? "c" : " ") + ")" + stream.Position.ToString("X") + ": " + filePath + " - "
                        + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
                }
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public string DosName { get; }
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public DateTime AccessDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public const int HEADER_SIZE = 0x60;

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[HEADER_SIZE];

                stream.Read(bytes, 0, bytes.Length);
                if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != FileHeaderBlock) { return; }

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 4));

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0x10, 0x50));
                if (Name.Length == 0) { return; }

                // remove leading drive letter, if any
                if (Name[1] == ':' && Name[2] == '\\')
                {
                    Name = Name[3..];
                }

                DosName = Name;

                int date = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0x8));
                int time = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0xA));

                CreateDate = Utils.GetDosDateTime(date, time);
                ModifyDate = CreateDate;
                AccessDate = ModifyDate;

                Attributes = (FileAttributes)bytes[0xC];

                Valid = true;
            }

        }

        public class StreamParser
        {
            protected Stream stream;

            protected int historySize;
            protected byte[] history;
            protected int historyPtr = 0;

            private int curByte;
            private int curBitMask = 0x100;

            public StreamParser(Stream stream, int historySize)
            {
                this.stream = stream;
                this.historySize = historySize;
                history = new byte[historySize];
            }

            public bool Aligned()
            {
                return curBitMask > 0x80;
            }

            protected int NextBit()
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
}
