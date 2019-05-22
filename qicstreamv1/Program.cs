using System;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QICStream (V1?) formatted tape images.
/// 
/// Copyright Dmitry Brant, 2019
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
            long customOffset = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                if (args[i] == "--offset") { customOffset = Convert.ToInt64(args[i + 1]); }
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
                    if (customOffset == 0)
                    {

                        // Skip straight to the contents.
                        // The contents are supposed to begin at 0x10000, but let's adjust for the
                        // blocks that we removed.
                        int offset = 0xF800; // 0x10000 minus 2 * 0x400.
                        stream.Seek(offset, SeekOrigin.Begin);
                    }
                    else
                    {
                        // adjust offset to account for removed bytes
                        customOffset -= ((customOffset / 0x8000) * 0x400);

                        stream.Seek(customOffset, SeekOrigin.Begin);
                    }

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

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = header.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);
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




                Console.Write(">>> " + bytes[0].ToString("X2") + " " + bytes[1].ToString("X2") + " " + bytes[2].ToString("X2") + " " + bytes[3].ToString("X2") + " " + bytes[4].ToString("X2") + " ");




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


                Console.Write(Subdirectory + "\\" + Name + "\n");

            }
        }
    }
}
