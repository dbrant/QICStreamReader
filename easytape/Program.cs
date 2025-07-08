using QicUtils;
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// 
/// Decoder for EasyTape backups found on some QIC-150 cartridges.
/// 
/// Copyright Dmitry Brant, 2025-
/// 
/// 
/// Brief outline of the format, to the best of my reverse-engineering ability:
/// 
/// 
/// </summary>
namespace easytape
{
    class Program
    {
        private const int BLOCK_SIZE = 0x200;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: easytape -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);

            Directory.CreateDirectory(baseDirectory);
            string currentDirectory = baseDirectory;

            // Read the file contents
            while (stream.Position < stream.Length)
            {
                AlignToNextBlock(stream);

                FileHeader header = null;

                stream.Read(bytes, 0, BLOCK_SIZE);
                if (bytes[0] == 0x0 && bytes[1] == 0x4 && bytes[2] == 0x2 && bytes[3] == 0x2)
                {
                    // volume header?
                    string volName = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0x80, 0x10));
                    Console.WriteLine("Backup label: " + volName);
                }
                else if (bytes[0] == 0x0 && bytes[1] == 0x4 && bytes[2] == 0x5 && bytes[3] == 0x5)
                {
                    // file record
                    header = new FileHeader(bytes);
                }

                if (header == null || !header.Valid)
                    continue;

                if (header.IsContinuation)
                {
                    Console.WriteLine("Warning: continuation of file " + header.Name);
                    continue;
                }

                string fileName = Path.Combine(currentDirectory, header.Name);
                if (File.Exists(fileName))
                {
                    Console.WriteLine("Warning: file exists: " + header.Name);
                    Console.ReadKey();
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fileName));

                using (var f = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    long bytesLeft = header.Size;
                    while (bytesLeft > 0)
                    {
                        int bytesToRead = bytes.Length;
                        if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                        if (bytesToRead > (stream.Length - stream.Position))
                        {
                            bytesToRead = (int)(stream.Length - stream.Position);
                            if (bytesToRead == 0)
                            {
                                // reached the end of the tape image, but still need data. This implies
                                // that the file continues on another tape.
                                Console.WriteLine("Warning: file contents truncated. Probably continues on another tape.");
                                break;
                            }
                        }
                        int bytesRead = stream.Read(bytes, 0, bytesToRead);
                        f.Write(bytes, 0, bytesToRead);

                        if (bytesLeft == header.Size)
                        {
                            if (!Utils.VerifyFileFormat(header.Name, bytes))
                            {
                                Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + fileName);
                                Console.ReadKey();
                            }
                        }

                        bytesLeft -= bytesToRead;
                    }
                }
                try
                {
                    File.SetCreationTime(fileName, header.DateTime);
                    File.SetLastWriteTime(fileName, header.DateTime);
                    File.SetAttributes(fileName, header.Attributes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Warning: could not set file attributes for " + fileName + ": " + ex.Message);
                }

                Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + ", " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());
            }
        }

        static void AlignToNextBlock(Stream stream)
        {
            long align = stream.Position % BLOCK_SIZE;
            if (align > 0) { stream.Seek(BLOCK_SIZE - align, SeekOrigin.Current); }
        }


        private class FileHeader
        {
            public long Size { get; set; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool IsDirectory { get; }
            public bool Valid { get; }

            public bool IsContinuation { get; }

            public FileHeader(byte[] bytes)
            {
                if (bytes[0] != 0x0 || bytes[1] != 0x4 || bytes[2] != 0x5 || bytes[3] != 0x5)
                    return;

                DateTime = Utils.GetDosDateTime(BitConverter.ToUInt16(bytes, 0x66), BitConverter.ToUInt16(bytes, 0x64));
                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0x68));
                Name = Utils.GetNullTerminatedString(Encoding.Latin1.GetString(bytes, 0x80, 0x40));
                if (Name.Length > 3 && Name[1] == ':' && Name[2] == '\\')
                    Name = Name.Substring(3);

                IsContinuation = bytes[0x28] != 0;

                Valid = true;
            }
        }
    }
}
