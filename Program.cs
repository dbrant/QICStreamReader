using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QICStream-formatted tape images.
/// 
/// Copyright Dmitry Brant, 2019
/// </summary>
namespace QicStreamReader
{
    class Program
    {
        private enum ControlCode
        {
            None = 0,
            ContentsStart = 1,
            CatalogStart = 2,
            ParentDirectory = 3,
            File = 5,
            Directory = 6,
            DataChunk = 9
        }


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
                Console.WriteLine("Usage: qicstream -f <file name> [-d <output directory>]");
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

                        // Each block of 0x8000 bytes ends with 0x402 bytes of something (perhaps for parity checking)
                        // We'll just remove it and write the good bytes to the temporary file.
                        outStream.Write(bytes, 0, 0x8000 - 0x402);
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
                        // Read the volume header, which doesn't really contain much vital information.
                        stream.Read(bytes, 0, 0x3e);

                        string magic = Encoding.ASCII.GetString(bytes, 4, 4);
                        if (magic != "FSET")
                        {
                            throw new ApplicationException("Incorrect magic value.");
                        }

                        // The volume header continues with a dynamically-sized volume label:
                        int volNameLen = BitConverter.ToUInt16(bytes, 0x3C);
                        stream.Read(bytes, 0, volNameLen);

                        string volName = Encoding.ASCII.GetString(bytes, 0, volNameLen);
                        Console.WriteLine("Backup label: " + volName);

                        // ...followed by a dynamically-sized drive name:
                        // (which must be aligned on a 2-byte boundary)
                        if (stream.Position % 2 == 1)
                        {
                            stream.ReadByte();
                        }
                        stream.Read(bytes, 0, 2);
                        int driveNameLen = BitConverter.ToUInt16(bytes, 0);
                        stream.Read(bytes, 0, driveNameLen);

                        string driveName = Encoding.ASCII.GetString(bytes, 0, driveNameLen);
                        Console.WriteLine("Drive name: " + driveName);
                    }
                    else
                    {
                        // adjust offset to account for removed bytes
                        customOffset -= ((customOffset / 0x8000) * 0x402);

                        stream.Seek(customOffset, SeekOrigin.Begin);
                    }

                    // Maintain a list of subdirectories into which we'll descend and un-descend.
                    List<string> currentDirList = new List<string>();
                    Directory.CreateDirectory(baseDirectory);
                    string currentDirectory = baseDirectory;

                    // And now begins the main sequence of the backup, which consists of a control code,
                    // followed by the data (if any) that the control code represents.

                    while (stream.Position < stream.Length)
                    {
                        ControlCode code = (ControlCode)stream.ReadByte();

                        if (code == ControlCode.CatalogStart)
                        {
                            // The catalog is at the end of the backup, so if we've reached it, it means
                            // that we're done extracting all the files.
                            break;
                        }
                        else if (code == ControlCode.ParentDirectory)
                        {
                            // Go "up" to the parent directory
                            if (currentDirList.Count > 0) { currentDirList.RemoveAt(currentDirList.Count - 1); }
                        }
                        else if (code == ControlCode.Directory)
                        {
                            // This control code is followed by a directory header which tells us the name
                            // of the directory that we're descending into.
                            var header = new DirectoryHeader(stream);
                            currentDirList.Add(header.Name);

                            currentDirectory = baseDirectory;
                            for (int i = 0; i < currentDirList.Count; i++)
                            {
                                currentDirectory = Path.Combine(currentDirectory, currentDirList[i]);
                            }

                            Directory.CreateDirectory(currentDirectory);
                            Directory.SetCreationTime(currentDirectory, header.DateTime);
                            Directory.SetLastWriteTime(currentDirectory, header.DateTime);

                            Console.WriteLine("Directory: " + currentDirectory + " - " + header.DateTime.ToLongDateString());
                        }
                        else if (code == ControlCode.File)
                        {
                            // This control code is followed by a file header which tells us all the details
                            // about the file, followed by the actual file contents.
                            var header = new FileHeader(stream);
                            string fileName = Path.Combine(currentDirectory, header.Name);
                            using (var f = new FileStream(Path.Combine(currentDirectory, header.Name), FileMode.Create, FileAccess.Write))
                            {
                                int bytesLeft = header.Size;

                                while (bytesLeft > 0)
                                {
                                    do
                                    {
                                        if (stream.Position >= stream.Length) { return; }
                                        code = (ControlCode)stream.ReadByte();
                                    }
                                    while (code != ControlCode.DataChunk);

                                    stream.Read(bytes, 0, 3);
                                    int chunkSize = BitConverter.ToUInt16(bytes, 1);

                                    stream.Read(bytes, 0, chunkSize);
                                    f.Write(bytes, 0, chunkSize);

                                    bytesLeft -= chunkSize;
                                }
                            }
                            File.SetCreationTime(fileName, header.DateTime);
                            File.SetLastWriteTime(fileName, header.DateTime);
                            File.SetAttributes(fileName, header.Attributes);

                            Console.WriteLine("File: " + header.Name + ", " + header.Size.ToString("X") + " - " + header.DateTime.ToLongDateString());
                        }
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
            public int Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];
                stream.Read(bytes, 0, 3);

                int structLength = BitConverter.ToUInt16(bytes, 1);
                stream.Read(bytes, 0, structLength);

                Attributes = (FileAttributes)bytes[0];
                DateTime = DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 4));
                Size = BitConverter.ToInt32(bytes, 8);

                int nameLength = structLength - 0x16;
                Name = Encoding.ASCII.GetString(bytes, structLength - nameLength, nameLength);
            }
        }

        private class DirectoryHeader : FileHeader
        {
            public DirectoryHeader(Stream stream)
                : base(stream)
            {
            }
        }
    }
}
