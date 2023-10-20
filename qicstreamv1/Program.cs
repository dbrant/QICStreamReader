﻿using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QIC-113 (rev G) formatted tape images.
/// https://www.qic.org/html/standards/11x.x/qic113g.pdf
/// 
/// Copyright Dmitry Brant, 2019, 2020
/// 
/// This does not implement the entire specification exactly, just enough to recover data from tapes
/// I've seen in the wild so far.
/// 
/// NOTE: this supports only the DOS-style formatting of the tape. For Windows 95 formatting, see
/// the `qicstream95` code. (TODO: combine these into one tool.)
/// 
/// This tool works in three possible phases, depending on the state of the raw tape image:
/// 
/// * Phase 1: remove and/or process ECC data. In every block of 0x8000 bytes on the tape, the first
/// 0x7C00 bytes are actual data, and the last 0x400 bytes are error-correction bytes. When obtaining
/// a binary dump of a tape, sometimes the ECC bytes will be included (e.g. when using `dd` on QIC-150
/// tapes), and sometimes they will be left out and applied automatically by the tape driver (e.g. when
/// using `dd` to read QIC-80 tapes using the ftape driver). You should look at the raw data and see
/// if ECC is included or not, and decide whether it needs to be removed.
/// 
/// * Phase 2: decompress the data. The data on the tape will sometimes be compressed. This tool
/// supports only QIC-122 compression so far. You should look at the raw data and see if the data
/// section (after the catalog) starts with something other than 0x33CC33CC. It will very likely have
/// the bytes 0x660C... at offset 6 in the data section, which is the compressed version of 0x33CC...
/// In a compressed volume the catalog will also start with 0x00000000FAF3.
/// 
/// * Phase 3: extract the files from the archive. Once the raw data is prepared by removing ECC and/or
/// decompressing, the files are ready to be extracted from it. At this point, the format of the data
/// is as follows:
/// 
/// * The archive begins with a catalog, which is just a list of all the files and directories that
/// will follow. The catalog is not actually necessary to read, since each actual file in the
/// contents is prepended by a copy of the catalog entry. The contents will appear directly after the
/// catalog, but will be aligned on a sector boundary (0x200 bytes).
/// 
/// * For extracting the contents, we skip past the catalog and directly to the stream of files
///   and directories.
/// 
/// The archive then simply consists of a sequence of files, one after the other. Each file starts
/// with a header, followed by its contents. (Directory information is part of the file header.)
/// 
/// Here is a breakdown of the file header:
/// 
/// Byte count            Meaning
/// ----------------------------------------
/// 4 bytes               Magic value of 0x33CC33CC.
/// 
/// 1 byte                Length of the following metadata. (it can possibly contain vendor-specific
///                       metadata that can be safely ignored.)
///                       
/// --- start metadata
/// 
/// 1 byte                File attributes, as specified in QIC-113.
/// 
/// 4 bytes               File date, as specified in QIC-113.
/// 
/// 4 bytes               File size INCLUDING this header. To calculate the actual file size, subtract the
///                       length of this header including the variable-length names below.
/// 
/// n bytes               Zero or more vendor-specific bytes.
/// 
/// --- end metadata (see length)
/// 
/// 1 byte                File name length (No null terminator).
/// 
/// [variable]            File name.
/// 
/// --- end filename (see length)
/// 
/// 1 byte                Directory name length (i.e. the directory in which this file should be placed). This
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
        private const int SEG_SIZE = 0x7400;

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
                Console.WriteLine("qicstreamv1 -x -f <file name> -o <out file name>");
                Console.WriteLine("qicstreamv1 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[0x10000];

            if (decompress)
            {
                try
                {
                    using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                    {
                        Stream outStream = new FileStream(outFileName, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                        {
                            while (stream.Position < stream.Length)
                            {
                                // always align to segment boundary
                                if ((stream.Position % 0x100) > 0)
                                {
                                    stream.Position += 0x100 - (int)(stream.Position % 0x100);
                                }


                                int segBytesLeft = SEG_SIZE;

                                stream.Read(bytes, 0, 4);
                                segBytesLeft -= 4;
                                uint absolutePos = BitConverter.ToUInt32(bytes, 0);
                                // I've seen tapes that have an 8-byte chunk header, instead of a 6-byte header:
                                // stream.Read(bytes, 0, 8);
                                // uint absolutePos = BitConverter.ToUInt32(bytes, 2);
                                // int frameSize = BitConverter.ToUInt16(bytes, 6);


                                while (segBytesLeft > 18)
                                {

                                    stream.Read(bytes, 0, 2);
                                    segBytesLeft -= 2;
                                    int frameSize = BitConverter.ToUInt16(bytes, 0);

                                    bool compressed = (frameSize & 0x8000) == 0;
                                    frameSize &= 0x7FFF;

                                    if (frameSize > segBytesLeft)
                                    {
                                        Console.WriteLine("Warning: frame extends beyond segment boundary.");
                                    }

                                    stream.Read(bytes, 0, frameSize);
                                    segBytesLeft -= frameSize;

                                    if (frameSize == 0)
                                    {
                                        Console.WriteLine("Warning: skipping empty frame.");
                                        break;
                                    }

                                    Console.WriteLine("input: " + stream.Position.ToString("X") + ", frameSize: " + frameSize.ToString("X")
                                        + ", absPos: " + absolutePos.ToString("X") + ", outputPos: " + outStream.Position.ToString("X"));

                                    if (absolutePos < outStream.Position)
                                    {
                                        Console.WriteLine("Warning: frame position out of sync with output. Starting new stream.");
                                        outFileName += "_";
                                        outStream = new FileStream(outFileName, FileMode.OpenOrCreate, FileAccess.Write);
                                    }

                                    if (absolutePos > 0x10000000)
                                    {
                                        Console.WriteLine(">>> Absolute position a bit too large...");
                                        break;
                                    }

                                    if (absPos && (absolutePos != outStream.Position))
                                    {
                                        Console.WriteLine(">>> adjusting position!");
                                        outStream.Position = absolutePos;
                                    }

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
                                    absolutePos = (uint)outStream.Position;


                                    if ((segBytesLeft - 0x400) >= 0 && (segBytesLeft - 0x400) < 18)
                                    {
                                        break;
                                    }

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
                        /*
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
                        */
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
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") +  ": " + filePath + " - "
                            + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

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
