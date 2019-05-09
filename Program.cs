using System;
using System.IO;
using System.Text;

namespace QicStreamReader
{
    class Program
    {
        static void Main(string[] args)
        {
            byte[] bytes = new byte[65536];

            using (var stream = new FileStream("tape2.bin", FileMode.Open, FileAccess.Read))
            {
                /*
                using (var outStream = new FileStream("tape2.bin", FileMode.Create, FileAccess.Write))
                {
                    while (stream.Position < stream.Length)
                    {
                        stream.Read(bytes, 0, 0x8000);
                        outStream.Write(bytes, 0, 0x8000 - 0x402);
                    }
                }
                */




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


                while (true)
                {


                    var header = new FileHeader(stream);

                    if (header.isDirectory)
                    {
                        Console.WriteLine("Directory: " + header.name);
                    }
                    else
                    {
                        Console.WriteLine("File: " + header.name + ", "  + header.size.ToString("X"));
                    }
                    Console.WriteLine("Date: " + header.dateTime.ToLongDateString());

                    stream.Seek(header.size, SeekOrigin.Current);


                }




            }
        }



        class FileHeader
        {
            public int size;
            public string name;
            public DateTime dateTime;

            public bool isDirectory;


            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[1024];


                while (bytes[0] != 5 && bytes[0] != 6)
                {
                    stream.Read(bytes, 0, 1);
                }

                isDirectory = bytes[0] == 0x6;

                stream.Read(bytes, 0, 3);

                

                int structLength = bytes[1];
                
                stream.Read(bytes, 0, structLength);


                long secondsSinceEpoch = BitConverter.ToUInt32(bytes, 4);
                dateTime = new DateTime(1970, 1, 1).AddSeconds(secondsSinceEpoch);

                if (!isDirectory)
                {
                    size = BitConverter.ToInt32(bytes, 8);
                }


                int nameLength = structLength - 0x16;

                name = Encoding.ASCII.GetString(bytes, structLength - nameLength, nameLength);

                if (!isDirectory)
                {
                    stream.Read(bytes, 0, 1);
                    if (bytes[0] != 9)
                    {
                        stream.Read(bytes, 0, 1);
                    }

                    stream.Read(bytes, 0, 3);
                    // size = BitConverter.ToUInt16(bytes, 1);


                    if (size >= 0xFFFA)
                    {

                        int sizeFix = BitConverter.ToInt16(bytes, 1);
                        //Console.WriteLine(">>> fix size: " + sizeFix);

                        //size += 4;

                        size += ((size / 0x8000) * 1);
                    }


                }
            }
        }
    }
}
