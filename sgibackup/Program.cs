using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace sgibackup
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
                Console.WriteLine("Usage: sgibackup -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {
                    int RemainingSize = 0;

                    while (true)
                    {
                        var header = new FileHeader(stream);

                        if (!header.Valid)
                        {
                            Console.WriteLine("Invalid file header, probably end of archive.");
                            break;
                        }

                        Console.WriteLine(">>> " + header.Name + new String(' ', 64 - header.Name.Length) + header.stats + "    " + header.stats2);

                        if (header.IsVolume || header.IsDirectory)
                        {
                            continue;
                        }

                        if (!header.HasData && header.Size > 0)
                        {
                            RemainingSize = header.Size;
                            continue;
                        }


                        var pathArr = header.Name.Split(new char[] { '/' });
                        string path = "";
                        for (int i = 0; i < pathArr.Length - 1; i++)
                        {
                            if (i > 0) path += Path.DirectorySeparatorChar;
                            path += pathArr[i];
                        }

                        path = Path.Combine(baseDirectory, path);

                        Directory.CreateDirectory(path);

                        string fileName = Path.Combine(path, pathArr[pathArr.Length - 1]);

                        using (var f = new FileStream(fileName, FileMode.Append, FileAccess.Write))
                        {
                            int bytesToWrite = 0x700;
                            if (bytesToWrite > RemainingSize)
                            {
                                bytesToWrite = RemainingSize;
                            }

                            f.Write(header.bytes, 0x100, bytesToWrite);

                            RemainingSize -= bytesToWrite;
                        }

                    }



                    /*


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

                                if (bytesLeft == header.Size)
                                {
                                    if (!QicUtils.Utils.VerifyFileFormat(header.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + fileName);
                                        Console.ReadKey();
                                    }
                                }

                                bytesLeft -= chunkSize;
                            }
                        }
                        File.SetCreationTime(fileName, header.DateTime);
                        File.SetLastWriteTime(fileName, header.DateTime);
                        File.SetAttributes(fileName, header.Attributes);

                        Console.WriteLine(stream.Position.ToString("X") + ": " + fileName + " - " + header.Size.ToString() + " bytes - " + header.DateTime.ToShortDateString());

                    */

                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }


        private class FileHeader
        {
            public bool IsVolume { get; }
            public bool IsDirectory { get; }
            public bool HasData { get; }
            public int Size { get; }
            public string Name { get; }
            public DateTime DateTime { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }

            public byte[] bytes;
            public string stats;
            public string stats2 = "";

            public FileHeader(Stream stream)
            {
                bytes = new byte[0x800];
                stream.Read(bytes, 0, bytes.Length);

                Name = Encoding.ASCII.GetString(bytes, 0, 0x80).Replace("\0", "");
                if (Name.Length < 1)
                {
                    Valid = false;
                    return;
                }

                stats = Encoding.ASCII.GetString(bytes, 0x80, 0x80).Replace("\0", "");

                var statsArr = stats.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                var type = statsArr[4].Substring(7);

                IsVolume = type == "1234" || type == "7890";
                HasData = type == "3456";

                if (type == "2345")
                {
                    stats2 = Encoding.ASCII.GetString(bytes, 0x180, 0x80).Replace("\0", "");

                    var stats2Arr = stats2.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    IsDirectory = stats2Arr[0][0] == '4';

                    Size = Convert.ToInt32(stats2.Substring(0x38, 8).Trim(), 16);
                }

                Valid = true;
            }
        }
    }
}
