using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QICStream (V1?) formatted tape images.
/// 
/// Copyright Dmitry Brant, 2019
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// * Every block of 0x8000 bytes ends with 0x400 bytes of extra data (for parity checking or
///   some other kind of error correction?). In other words, for every 0x8000 bytes, only the
///   first 0x7C00 bytes are useful data.
/// * Little-endian.
/// * The archive begins with a catalog, which is just a list of all the files and directories that
///   will follow. The catalog is not actually necessary to read, since each actual file in the
///   contents is prepended by a catalog entry. The contents will appear directly after the catalog,
///   but will be aligned on a boundary of 0x8000 bytes.
/// * For extracting the contents, we skip past the catalog and directly to the stream of files
///   and directories.
/// 
/// The actual archive simply consists of a sequence of files, one after the other. Each file
/// starts with a header, followed by its contents. (Directory information is part of the file
/// header.)
/// 
/// Here is a breakdown of the file header:
/// 
/// Byte offset (hex)     Meaning
/// ----------------------------------------
/// 0-3                   Always seems to be 0x33CC33CC; possibly a magic identifier.
/// 4                     Always seems to be 9.
/// 
/// 5                     Possibly file attributes, but doesn't seem to be encoded in the usual DOS way.
/// 
/// 6-9                   File date, encoded as time_t (seconds since 1970 epoch).
/// 
/// A-D                   File size INCLUDING this header. To calculate the actual file size, subtract the
///                       length of this header including the variable-length names below.
/// 
/// E                     File name length (No null terminator).
/// 
/// [variable]            File name.
/// 
/// [variable+1]          Directory name length (i.e. the directory in which this file should be placed). This
///                       may be 0, which would mean that this file is in the root directory.
/// 
/// [variable]            Directory name. This name may also contain one or more null characters in the middle, to
///                       denote subdirectories. For example, the name "FOO\0BAR\0BAZ" would translate
///                       into the directory structure of FOO\BAR\BAZ.
/// -------------------------------------------------------------------------
/// 
/// The file contents follow immediately after the header.
/// 
/// 
/// </summary>
namespace QicStreamV1
{
    class Program
    {
        private const uint FileHeaderMagic = 0x33CC33CC;

        static void Main(string[] args)
        {
            string inFileName = "";
            string outFileName = "out.bin";
            string baseDirectory = "out";

            long initialOffset = 0;
            bool removeEcc = false;
            bool decompress = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-ecc") { removeEcc = true; }
                else if (args[i] == "-x") { decompress = true; }
                else if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("Phase 1 - remove/apply ECC data (if present):");
                Console.WriteLine("Usage: qicstreamv1 -ecc -f <file name> -o <out file name>");
                Console.WriteLine("Phase 2 - uncompress the archive (if compression is used):");
                Console.WriteLine("Usage: qicstreamv1 -x -f <file name> -o <out file name>");
                Console.WriteLine("Phase 3 - extract files/folders from archive:");
                Console.WriteLine("Usage: qicstreamv1 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            if (removeEcc)
            {
                try
                {
                    using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                    {
                        using (var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write))
                        {
                            while (stream.Position < stream.Length)
                            {
                                stream.Read(bytes, 0, 0x8000);

                                // Each block of 0x8000 bytes ends with 0x400 bytes of ECC and/or parity data.
                                // TODO: actually use the ECC to verify and correct the data itself.
                                outStream.Write(bytes, 0, 0x8000 - 0x400);
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
            else if (decompress)
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
                                    // pad to 0x200
                                    if ((outStream.Position % 0x200) > 0)
                                    {
                                        Array.Clear(bytes, 0, bytes.Length);
                                        outStream.Write(bytes, 0, 0x200 - (int)(outStream.Position % 0x200));
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
                    if (initialOffset != 0)
                    {
                        stream.Seek(initialOffset, SeekOrigin.Begin);
                    }
                    else
                    {
                        // read through the catalog (don't do anything with it).
                        while (stream.Position < stream.Length)
                        {
                            var header = new FileHeader(stream, true);
                            if (!header.Valid)
                            {
                                break;
                            }
                        }

                        // jump to boundaries of 0x200 until we come upon the first file header
                        while (stream.Position < stream.Length)
                        {
                            if (stream.Position % 0x200 > 0)
                            {
                                stream.Seek(0x200 - (stream.Position % 0x200), SeekOrigin.Current);
                            }
                            stream.Read(bytes, 0, 4);
                            if (BitConverter.ToInt32(bytes, 0) == FileHeaderMagic)
                            {
                                stream.Seek(-4, SeekOrigin.Current);
                                break;
                            }
                        }
                    }

                    while (stream.Position < stream.Length)
                    {
                        var header = new FileHeader(stream, false);
                        if (!header.Valid)
                        {
                            Console.WriteLine("Invalid file header, probably end of archive.");
                            break;
                        }
                        else if (header.IsDirectory)
                        {
                            if (header.Size > 0)
                            {
                                stream.Seek(header.Size, SeekOrigin.Current);
                            }
                            continue;
                        }

                        string filePath = baseDirectory;
                        if (header.Subdirectory.Length > 0)
                        {
                            string[] dirArray = header.Subdirectory.Split('\0');
                            for (int i = 0; i < dirArray.Length; i++)
                            {
                                filePath = Path.Combine(filePath, dirArray[i]);
                            }
                        }

                        Directory.CreateDirectory(filePath);
                        filePath = Path.Combine(filePath, header.Name);

                        Console.WriteLine(stream.Position.ToString("X") +  ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = header.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);

                                if (bytesLeft == header.Size)
                                {
                                    if (!VerifyFileFormat(header.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                        Console.ReadKey();
                                    }
                                }

                                bytesLeft -= bytesToRead;
                            }
                        }

                        File.SetCreationTime(filePath, header.DateTime);
                        File.SetLastWriteTime(filePath, header.DateTime);
                        File.SetAttributes(filePath, header.Attributes);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public string Subdirectory { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }
            public bool IsLastEntry { get; }
            public bool IsFinalEntry { get; }

            public FileHeader(Stream stream, bool isCatalog)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                if (!isCatalog)
                {
                    stream.Read(bytes, 0, 4);
                    if (BitConverter.ToInt32(bytes, 0) != FileHeaderMagic) { return; }
                }

                int dataLen = stream.ReadByte();
                if (dataLen == 0) { return; }
                stream.Read(bytes, 0, dataLen);

                int flags = bytes[0];
                if ((flags & 0x2) == 0) { Attributes |= FileAttributes.ReadOnly; }
                if ((flags & 0x8) != 0) { Attributes |= FileAttributes.Hidden; }
                if ((flags & 0x10) != 0) { Attributes |= FileAttributes.System; }
                if ((flags & 0x20) != 0) { IsDirectory = true; }
                if ((flags & 0x40) != 0) { IsLastEntry = true; }
                if ((flags & 0x80) != 0) { IsFinalEntry = true; }

                DateTime = GetShortDateTime(BitConverter.ToUInt32(bytes, 0x1));
                Size = BitConverter.ToInt32(bytes, 0x5);
                if (!isCatalog && Size == 0) { return; }

                int nameLength = stream.ReadByte();
                stream.Read(bytes, 0, nameLength);
                Name = Encoding.ASCII.GetString(bytes, 0, nameLength);

                if (!isCatalog)
                {
                    int subDirLength = stream.ReadByte();
                    if (subDirLength > 0)
                    {
                        stream.Read(bytes, 0, subDirLength);
                        Subdirectory = Encoding.ASCII.GetString(bytes, 0, subDirLength);
                    }
                    // The Size field *includes* the size of the header, so adjust it.
                    Size -= (stream.Position - initialPos);
                }
                Valid = true;
            }
        }

        private static DateTime GetShortDateTime(uint date)
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

        private static bool VerifyFileFormat(string fileName, byte[] bytes)
        {
            string nameLower = fileName.ToLower();

            if (nameLower.EndsWith(".exe") && (bytes[0] != 'M' || bytes[1] != 'Z')) { return false; }
            if (nameLower.EndsWith(".zip") && (bytes[0] != 'P' || bytes[1] != 'K')) { return false; }
            if (nameLower.EndsWith(".dwg") && (bytes[0] != 'A' || bytes[1] != 'C')) { return false; }

            return true;
        }
    }
}
