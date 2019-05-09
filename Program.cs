using System;
using System.IO;
using System.Text;

namespace QicStreamReader
{
    class Program
    {
        static void Main(string[] args)
        {

            /*
            byte[] bytes2 = new byte[65536];
            using (var stream = new FileStream("tape1.bin", FileMode.Open, FileAccess.Read))
            {
                using (var outStream = new FileStream("tape1a.bin", FileMode.Create, FileAccess.Write))
                {
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes2, 0, 0x8000);
                        outStream.Write(bytes2, 0, 0x8000 - 0x400);
                    }
                }
            }
            return;
            */


            string inFileName = "";
            string baseDirectory = "out";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                if (args[i] == "-d") { baseDirectory = args[i + 1]; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage: qicstreamreader -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
            {
                stream.Read(bytes, 0, 0x3e);

                string magic = Encoding.ASCII.GetString(bytes, 4, 4);
                if (magic != "FSET")
                {
                    throw new ApplicationException("Incorrect magic value.");
                }

                int volNameLen = bytes[0x3C];


                stream.Read(bytes, 0, volNameLen);

                string volName = Encoding.ASCII.GetString(bytes, 0, volNameLen);
                Console.WriteLine("Volume name: " + volName);


                stream.Read(bytes, 0, 3);

                int driveNameLen = bytes[1];

                stream.Read(bytes, 0, driveNameLen);

                string driveName = Encoding.ASCII.GetString(bytes, 0, driveNameLen);
                Console.WriteLine("Drive name: " + driveName);

                stream.Read(bytes, 0, 7);


                string currentDirectory = baseDirectory;

                Directory.CreateDirectory(currentDirectory);

                while (true)
                {
                    var header = new FileHeader(stream);
                    if (!header.valid) { break; }

                    if (header.isDirectory)
                    {
                        Console.WriteLine("Directory: " + header.name + " - " + header.dateTime.ToLongDateString());
                        currentDirectory = Path.Combine(baseDirectory, header.name);
                        Directory.CreateDirectory(currentDirectory);
                        Directory.SetCreationTime(currentDirectory, header.dateTime);
                        Directory.SetLastWriteTime(currentDirectory, header.dateTime);
                    }
                    else
                    {
                        Console.WriteLine("File: " + header.name + ", " + header.size.ToString("X") + " - " + header.dateTime.ToLongDateString());

                        string fileName = Path.Combine(currentDirectory, header.name);
                        using (var f = new FileStream(Path.Combine(currentDirectory, header.name), FileMode.Create, FileAccess.Write))
                        {
                            int bytesLeft = header.size;

                            while (bytesLeft > 0)
                            {
                                do
                                {
                                    if (stream.Position >= stream.Length) { return; }
                                    stream.Read(bytes, 0, 1);
                                }
                                while (bytes[0] != 9);

                                stream.Read(bytes, 0, 3);
                                int chunkSize = BitConverter.ToUInt16(bytes, 1);

                                stream.Read(bytes, 0, chunkSize);
                                f.Write(bytes, 0, chunkSize);

                                bytesLeft -= chunkSize;
                            }
                        }
                        File.SetCreationTime(fileName, header.dateTime);
                        File.SetLastWriteTime(fileName, header.dateTime);
                    }
                }

            }
        }

        private static DateTime DateTimeFromTimeT(long timeT)
        {
            return new DateTime(1970, 1, 1).AddSeconds(timeT);
        }

        class FileHeader
        {
            public bool valid;

            public int size;
            public string name;
            public DateTime dateTime;

            public bool isDirectory;

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];

                while (bytes[0] != 5 && bytes[0] != 6)
                {
                    if (stream.Position >= stream.Length) { return; }
                    stream.Read(bytes, 0, 1);
                }

                isDirectory = bytes[0] == 0x6;

                stream.Read(bytes, 0, 3);

                int structLength = bytes[1];
                stream.Read(bytes, 0, structLength);

                dateTime = DateTimeFromTimeT(BitConverter.ToUInt32(bytes, 4));

                if (!isDirectory)
                {
                    size = BitConverter.ToInt32(bytes, 8);
                }

                int nameLength = structLength - 0x16;
                name = Encoding.ASCII.GetString(bytes, structLength - nameLength, nameLength);

                valid = true;
            }
        }
    }
}
