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
/// * The archive begins with a catalog, which takes up the first 0x10000 bytes (or possibly
///   aligned to a boundary of 0x8000 bytes).
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
        static void Main(string[] args)
        {
            string inFileName = "";
            string tempFileName;
            string baseDirectory = "out";
            long initialOffset = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                if (args[i] == "--offset") { initialOffset = Convert.ToInt64(args[i + 1]); }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: qicstreamv1 -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];
            tempFileName = inFileName + ".tmp";

            // Pass 1: remove unused bytes (parity?) from the original file, and write to temporary file

            using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
            {
                using (var outStream = new FileStream(tempFileName, FileMode.Create, FileAccess.Write))
                {
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 0x8000);

                        // Each block of 0x8000 bytes ends with 0x400 bytes of something (perhaps for parity checking)
                        // We'll just remove it and write the good bytes to the temporary file.
                        outStream.Write(bytes, 0, 0x8000 - 0x400);
                    }
                }
            }

            // Pass 2: extract files.

            try
            {
                using (var stream = new FileStream(tempFileName, FileMode.Open, FileAccess.Read))
                {
                    if (initialOffset == 0)
                    {
                        // Skip past the catalog and straight to the contents.
                        initialOffset = 0x10000;
                    }

                    // adjust offset to account for removed bytes
                    initialOffset -= ((initialOffset / 0x8000) * 0x400);
                    stream.Seek(initialOffset, SeekOrigin.Begin);

                    Directory.CreateDirectory(baseDirectory);
                    string currentDirectory = baseDirectory;


                    while (stream.Position < stream.Length)
                    {
                        var header = new FileHeader(stream);

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
            finally
            {
                File.Delete(tempFileName);
            }
        }

        private static DateTime DateTimeFromTimeT(long timeT)
        {
            return new DateTime(1970, 1, 1).AddSeconds(timeT);
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public string Subdirectory { get; }

            public FileHeader(Stream stream)
            {
                Subdirectory = "";
                byte[] bytes = new byte[1024];
                long initialPos = stream.Position;

                stream.Read(bytes, 0, 0xF);

                //Attributes = (FileAttributes)bytes[0];

                DateTime = DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 0x6));
                Size = BitConverter.ToInt32(bytes, 0xA);

                int nameLength = bytes[0xE];

                stream.Read(bytes, 0, nameLength);
                Name = Encoding.ASCII.GetString(bytes, 0, nameLength);


                int subDirLength = stream.ReadByte();

                if (subDirLength > 0) {
                    stream.Read(bytes, 0, subDirLength);
                    Subdirectory = Encoding.ASCII.GetString(bytes, 0, subDirLength);
                }

                // The Size field *includes* the size of the header, so adjust it.
                Size -= (stream.Position - initialPos);
            }
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
