using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>
/// 
/// Decoder for QIC Unknown Format #2.
/// 
/// Copyright Dmitry Brant, 2019
/// </summary>
namespace QicStreamV4
{
    class Program
    {
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
                Console.WriteLine("Usage: qicbackup -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    // Read the volume header
                    stream.Read(bytes, 0, 0x200);

                    string volName = Encoding.ASCII.GetString(bytes, 0xAC, 0x80);
                    int zeroPos = volName.IndexOf("\0");
                    if (zeroPos > 0) { volName = volName.Substring(0, zeroPos); }
                    Console.WriteLine("Backup label: " + volName);


                    // Maintain a list of subdirectories into which we'll descend and un-descend.
                    List<string> currentDirList = new List<string>();
                    Directory.CreateDirectory(baseDirectory);
                    string currentDirectory = baseDirectory;

                    // And now begins the main sequence of the backup, which consists of a file or directory header,
                    // and the contents of the file.
                    // Each new file/directory header is aligned on a block boundary (512 bytes).

                    while (stream.Position < stream.Length)
                    {
                        FileHeader header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            continue;
                        }

                        if (header.IsDirectory)
                        {
                            Console.WriteLine(stream.Position.ToString("X") + ": Directory: " + header.Name + ", " + header.Size.ToString("X") + " - " + header.DateTime.ToLongDateString());

                            if (header.Name.Length > 0)
                            {
                                string[] dirArray = header.Name.Split('\0');
                                currentDirectory = baseDirectory;
                                for (int i = 0; i < dirArray.Length; i++)
                                {
                                    currentDirectory = Path.Combine(currentDirectory, dirArray[i]);
                                }
                                Directory.CreateDirectory(currentDirectory);
                                Directory.SetCreationTime(currentDirectory, header.DateTime);
                                Directory.SetLastWriteTime(currentDirectory, header.DateTime);
                            }
                        }
                        else
                        {
                            Console.WriteLine(stream.Position.ToString("X") + ": File: " + header.Name + ", " + header.Size.ToString("X") + " - " + header.DateTime.ToLongDateString());

                            string fileName = Path.Combine(currentDirectory, header.Name);
                            using (var f = new FileStream(fileName, FileMode.Create, FileAccess.Write))
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
                                            Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + header.Name);
                                            Console.ReadKey();
                                        }
                                    }

                                    bytesLeft -= bytesToRead;
                                }
                            }
                            File.SetCreationTime(fileName, header.DateTime);
                            File.SetLastWriteTime(fileName, header.DateTime);
                            File.SetAttributes(fileName, header.Attributes);
                        }

                        // align position to the next 0x200 bytes
                        long align = stream.Position % 0x200;
                        if (align > 0) { stream.Seek(0x200 - align, SeekOrigin.Current); }

                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
            finally
            {
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool IsDirectory { get; }
            public bool Valid { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];
                stream.Read(bytes, 0, 2);

                if (bytes[0] != 0x8 && bytes[0] != 9)
                {
                    Console.WriteLine(stream.Position.ToString("X") + " -- Warning: skipping over block.");
                    Console.ReadKey();

                    // skip over this block
                    stream.Seek(0x1fe, SeekOrigin.Current);
                    return;
                }

                IsDirectory = bytes[0] == 0x8;

                // Read extra unknown bytes at the beginning
                stream.Read(bytes, 0, IsDirectory ? 0x48 : 0x4C);

                bool hasExtraData = bytes[0x16] != 0;

                // Read header with meaningful data
                stream.Read(bytes, 0, 0x50);

                if (!IsDirectory)
                {
                    Size = BitConverter.ToUInt32(bytes, 0x4);
                    if (Size == 0)
                    {
                        Size = BitConverter.ToInt64(bytes, 0x8);
                    }
                    else
                    {
                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: using secondary size field.");
                        Console.ReadKey();

                        // hack?
                        //Size += 0x200;
                    }
                }

                Attributes = (FileAttributes)bytes[0x11];

                int year = BitConverter.ToUInt16(bytes, 0x1A);
                int month = BitConverter.ToUInt16(bytes, 0x1C);
                int day = BitConverter.ToUInt16(bytes, 0x1E);
                int hour = BitConverter.ToUInt16(bytes, 0x20);
                int minute = BitConverter.ToUInt16(bytes, 0x22);
                int second = BitConverter.ToUInt16(bytes, 0x24);
                try
                {
                    DateTime = new DateTime(year, month, day, hour, minute, second);
                }
                catch { }

                int nameLength = BitConverter.ToUInt16(bytes, 0x4C);

                stream.Read(bytes, 0, nameLength);
                // the name length includes null terminator, so trim it.
                Name = Encoding.ASCII.GetString(bytes, 0, nameLength - 1);

                // make sure we end on a two-byte boundary
                if (stream.Position % 2 == 1)
                {
                    stream.ReadByte();
                }

                if (hasExtraData)
                {
                    stream.Seek(0x24, SeekOrigin.Current);
                }

                Valid = true;
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
