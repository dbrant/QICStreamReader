using System;
using System.IO;
using System.Text;

namespace MacAIT3
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

            byte[] bytes = new byte[65536];

            int fileNum = 0;

            try
            {
                FileHeader curHeader = null;
                string curDirectory = "";

                while (true)
                {
                    if (fileNum > 70) { break; }
                    if (!File.Exists(fileNum + ".bin"))
                    {
                        fileNum++;
                        continue;
                    }

                    Console.WriteLine("Opening: " + fileNum + ".bin");
                    using (var stream = new FileStream(fileNum + ".bin", FileMode.Open, FileAccess.Read))
                    {
                        if (fileNum == 0)
                        {
                            stream.Seek(0x2000, SeekOrigin.Begin);

                            Directory.CreateDirectory(baseDirectory);
                        }

                        string currentDirectory = baseDirectory;

                        while (stream.Position < stream.Length)
                        {
                            stream.Read(bytes, 0, 8);

                            if (bytes[0] == 0 && bytes[1] == 0)
                            {
                                if ((stream.Position % 0x200) > 0)
                                {
                                    stream.Seek(0x200 - (stream.Position % 0x200), SeekOrigin.Current);
                                }
                                continue;
                            }

                            string blockName = Encoding.ASCII.GetString(bytes, 0, 4);
                            long blockSize = QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 4));

                            blockSize -= 8;


                            int bytesToWrite = 0;

                            Console.WriteLine(stream.Position.ToString("X2") + " > " + blockName + ": " + blockSize);

                            if (blockName == "File")
                            {

                                if (curHeader != null)
                                {
                                    try
                                    {
                                        File.SetCreationTime(curHeader.AbsPath, curHeader.DateTime);
                                        File.SetLastWriteTime(curHeader.AbsPath, curHeader.DateTime);
                                    }
                                    catch { }
                                }

                                curHeader = new FileHeader(stream, (int)blockSize);
                                Console.WriteLine(stream.Position.ToString("X2") + " file > " + curHeader.Name + ": " + curHeader.Size);
                            }
                            else if (blockName == "Diry")
                            {
                                var header = new DirectoryHeader(stream, (int)blockSize);
                                Console.WriteLine(stream.Position.ToString("X2") + " dir > " + header.Name);
                                curDirectory = header.Name;
                            }
                            else if (blockName == "Fork")
                            {
                                // Skip over fork header ??
                                stream.Seek(0x16, SeekOrigin.Current);
                                blockSize -= 0x16;

                                bytesToWrite = (int)blockSize;

                            }
                            else if (blockName == "Cont")
                            {
                                bytesToWrite = (int)blockSize;
                            }
                            else
                            {
                                stream.Seek(blockSize, SeekOrigin.Current);
                            }

                            if (bytesToWrite < 1 || curHeader == null)
                            {
                                continue;
                            }

                            if (curHeader.AbsPath == null)
                            {
                                string filePath = baseDirectory;
                                /*
                                if (curHeader.Subdirectory.Length > 0)
                                {
                                    string[] dirArray = curHeader.Subdirectory.Split('\0');
                                    for (int i = 0; i < dirArray.Length; i++)
                                    {
                                        filePath = Path.Combine(filePath, dirArray[i]);
                                    }
                                }
                                */

                                if (curDirectory.Length > 0)
                                {
                                    filePath = Path.Combine(filePath, curDirectory);
                                }

                                Directory.CreateDirectory(filePath);

                                filePath = Path.Combine(filePath, curHeader.Name);

                                while (File.Exists(filePath))
                                {
                                    filePath += "_";
                                    Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                                }

                                curHeader.AbsPath = filePath;
                                Console.WriteLine("--> " + curHeader.AbsPath + ", " + curHeader.DateTime);
                            }

                            using (var f = new FileStream(curHeader.AbsPath, FileMode.Append, FileAccess.Write))
                            {
                                long bytesLeft = bytesToWrite;
                                while (bytesLeft > 0)
                                {
                                    int bytesToRead = bytes.Length;
                                    if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                    stream.Read(bytes, 0, bytesToRead);
                                    f.Write(bytes, 0, bytesToRead);
                                    bytesLeft -= bytesToRead;
                                }
                            }

                        }
                    }

                    fileNum++;
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
            public bool IsDirectory { get; }
            public bool Valid { get; }
            public bool Continuation { get; }

            public string AbsPath = null;

            public FileHeader(Stream stream, int headerSize)
            {
                byte[] bytes = new byte[headerSize];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0x3E, headerSize - 0x3E).Replace("/", "_").Replace("\0", "").Trim();
                Size = (long) QicUtils.Utils.BigEndian(BitConverter.ToUInt64(bytes, 0x16));
                DateTime = QicUtils.Utils.DateTimeFrom1904(QicUtils.Utils.BigEndian(BitConverter.ToUInt32(bytes, 0xE)));

                Valid = true;
            }
        }

        private class DirectoryHeader
        {
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public DirectoryHeader(Stream stream, int headerSize)
            {
                byte[] bytes = new byte[headerSize];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0x48, headerSize - 0x48).Replace("/", "_").Replace("\0", "").Trim();

                Valid = true;
            }
        }

    }
}
