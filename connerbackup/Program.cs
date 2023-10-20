using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QIC-113 (rev G) formatted tape images, with a flavor of Conner Backup Basics.
/// https://www.qic.org/html/standards/11x.x/qic113g.pdf
/// 
/// Copyright Dmitry Brant, 2021
/// 
/// This does not implement the entire specification exactly, just enough to recover data from tapes
/// I've seen in the wild so far.
/// 
/// For a bit more documentation of the basic format, see the QicStreamV1 project.
/// 
/// </summary>
namespace ConnerBackup
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
            bool decompress = false;
            bool absPos = false;
            bool catDump = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-o") { outFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "-x") { decompress = true; }
                else if (args[i] == "--offset") { initialOffset = QicUtils.Utils.StringOrHexToLong(args[i + 1]); }
                else if (args[i] == "--abspos") { absPos = true; }
                else if (args[i] == "--catdump") { catDump = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("connerbackup -x -f <file name> -o <out file name>");
                Console.WriteLine("connerbackup -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            if (decompress)
            {
                try
                {
                    using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                    stream.Position = initialOffset;

                    using var outStream = new FileStream(outFileName, FileMode.Create, FileAccess.Write);
                    while (stream.Position < stream.Length)
                    {
                        Console.Write(stream.Position.ToString("X8") + ": ");

                        if (stream.Position % 0x7400 == 0)
                        {
                            stream.Read(bytes, 0, 4);
                            uint absolutePos = BitConverter.ToUInt32(bytes, 0);

                            Console.Write("abs: " + absolutePos.ToString("X8"));

                            if (absPos && (absolutePos != outStream.Position))
                            {
                                outStream.Position = absolutePos;
                            }
                        }

                        stream.Read(bytes, 0, 2);
                        int frameSize = BitConverter.ToUInt16(bytes, 0);

                        if (frameSize == 0)
                        {
                            Console.WriteLine("Warning: empty frame size; bailing.");
                            break;
                        }

                        bool compressed = (frameSize & 0x8000) == 0;
                        frameSize &= 0x7FFF;

                        if (frameSize > 0x7F00)
                        {
                            Console.WriteLine("Warning: suspicious frame size; bailing.");
                            frameSize = BitConverter.ToUInt16(bytes, 0);
                            break;
                        }

                        Console.Write("\tframeSize: " + frameSize.ToString("X4") + (compressed ? " (c)" : ""));
                        Console.Write("\n");

                        stream.Read(bytes, 0, frameSize);

                        //if (stream.Position % 0x7400 != 0)
                        //{
                        //    stream.Seek(0x7400 - (stream.Position % 0x7400), SeekOrigin.Current);
                        //}

                        if (compressed)
                        {
                            try
                            {
                                new QicUtils.Qic122Decompressor(new MemoryStream(bytes)).DecompressTo(outStream);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Warning: failed to decompress frame: " + ex.Message);
                            }
                        }
                        else
                        {
                            outStream.Write(bytes, 0, frameSize);
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
                using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
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
                            // The first "invalid" header very likely represents the end of the catalog.
                            break;
                        }
                        if (catDump)
                        {
                            Console.WriteLine(header.Name + "\t" + (header.IsDirectory ? "<dir>" : header.Size.ToString()) + "\t" + header.DateTime);
                        }
                    }
                    if (catDump)
                    {
                        return;
                    }
                }

                bool printHeaderWarning = false;

                while (stream.Position < stream.Length)
                {
                    long posBeforeHeader = stream.Position;
                    var header = new FileHeader(stream, false);
                    if (!header.Valid)
                    {
                        if (printHeaderWarning)
                        {
                            printHeaderWarning = false;
                            Console.WriteLine("Warning: Invalid file header. Searching for next header...");
                        }
                        stream.Position = posBeforeHeader + 1;
                        continue;
                    }
                    else
                    {
                        printHeaderWarning = true;
                    }

                    if (header.IsDirectory)
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

                    while (File.Exists(filePath))
                    {
                        Console.WriteLine("Warning: file already exists (skipping): " + filePath);
                        filePath += "_";

                        continue;
                    }

                    Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

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
                                if (!QicUtils.Utils.VerifyFileFormat(header.Name, bytes))
                                {
                                    Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                    Console.ReadKey();
                                }
                            }

                            bytesLeft -= bytesToRead;
                        }
                    }

                    try
                    {
                        File.SetCreationTime(filePath, header.DateTime);
                        File.SetLastWriteTime(filePath, header.DateTime);
                        File.SetAttributes(filePath, header.Attributes);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Warning: " + e.Message);
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

                DateTime = QicUtils.Utils.GetQicDateTime(BitConverter.ToUInt32(bytes, 0x1));
                Size = BitConverter.ToInt32(bytes, 0x5);
                if (!isCatalog && Size == 0) { return; }

                int nameLength = stream.ReadByte();
                stream.Read(bytes, 0, nameLength);
                Name = QicUtils.Utils.ReplaceInvalidChars(Encoding.ASCII.GetString(bytes, 0, nameLength));

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

    }
}
