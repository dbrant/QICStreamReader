using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace qic80reader
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
                Console.WriteLine("Usage: qic80reader -f <file name> [-d <output directory>]");
                return;
            }

            byte[] bytes = new byte[65536];

            try
            {
                using (var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read))
                {





                    // NOTE: for every 0xE800 bytes, skip 6 bytes.
                    // (create a stream reader adapter that does this transparently?)
                    stream.Seek(6, SeekOrigin.Begin);



                    while (stream.Position < stream.Length)
                    {
                        var rec = new CatalogRecord(stream);
                        if (!rec.Valid) { break; }

                        Console.WriteLine(Convert.ToString(rec.Attrs, 16) + "\t" + rec.Name + "\t" + rec.Size + "\t" + rec.Date);

                    }

                    //var header = new FormatParamRecord(stream);
                    //Console.WriteLine(header);


                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
            }
        }



        private class CatalogRecord
        {
            public bool Valid;

            public string Name;
            public long Size;
            public int Attrs;
            public DateTime Date;

            public CatalogRecord(Stream stream)
            {
                byte[] bytes = new byte[0xFF];
                int dataLen = stream.ReadByte();
                if (dataLen == 0) { return; }

                stream.Read(bytes, 0, dataLen);

                Attrs = bytes[0];
                Date = GetDateTime(BitConverter.ToUInt32(bytes, 1));
                Size = BitConverter.ToUInt32(bytes, 5);

                int nameLen = stream.ReadByte();
                stream.Read(bytes, 0, nameLen);
                Name = CleanString(Encoding.ASCII.GetString(bytes, 0, nameLen));
                Valid = true;
            }
        }


        private class FormatParamRecord
        {
            public bool Valid;

            public int FormatCode;
            public int Revision;
            public int HeaderSegNum;
            public int HeaderSegDupNum;
            public int DataSegFirstLogicalArea;
            public int DataSegLastLogicalArea;
            public DateTime MostRecentFormat;
            public DateTime MostRecentWriteOrFormat;
            public int SegmentsPerTrack;
            public int TracksPerCartridge;
            public int MaxFloppySide;
            public int MaxFloppyTrack;
            public int MaxFloppySector;
            public string TapeName;
            public DateTime TapeNameTime;
            public int ReformatErrorFlag;
            public int NumSegmentsWritten;
            public DateTime InitialFormatTime;
            public int FormatCount;
            public string ManufacturerName;
            public string ManufacturerLotCode;


            public FormatParamRecord(Stream stream)
            {
                byte[] bytes = new byte[0xFF];
                long initialPos = stream.Position;
                int ptr = 0;

                stream.Read(bytes, 0, 0xFF);

                uint magic = BitConverter.ToUInt32(bytes, ptr); ptr += 4;
                if (magic != 0xAA55AA55)
                {
                    return;
                }

                FormatCode = bytes[ptr++];
                Revision = bytes[ptr++];

                HeaderSegNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                HeaderSegDupNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                DataSegFirstLogicalArea = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                DataSegLastLogicalArea = BitConverter.ToUInt16(bytes, ptr); ptr += 2;

                MostRecentFormat = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                MostRecentWriteOrFormat = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                ptr += 2;

                SegmentsPerTrack = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                TracksPerCartridge = bytes[ptr++];
                MaxFloppySide = bytes[ptr++];
                MaxFloppyTrack = bytes[ptr++];
                MaxFloppySector = bytes[ptr++];

                TapeName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;

                TapeNameTime = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;

                ptr = 128;
                ReformatErrorFlag = bytes[ptr++];
                ptr++;
                NumSegmentsWritten = BitConverter.ToInt32(bytes, ptr); ptr += 4;
                ptr += 4;
                InitialFormatTime = GetDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
                FormatCount = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
                ptr += 2;

                ManufacturerName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
                ManufacturerLotCode = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;

                Valid = true;
            }
        }

        private static string CleanString(string str)
        {
            return str.Replace("\0", "").Trim();
        }

        private static DateTime GetDateTime(uint date)
        {
            DateTime d = new DateTime();
            int year = (int)((date & 0xFE000000) >> 25) + 1970;
            int s = (int)(date & 0x1FFFFFF);
            int second = s % 60; s /= 60;
            int minute = s % 60; s /= 60;
            int hour = s % 24; s /= 24;
            int day = s % 31; s /= 31;
            int month = s;
            try
            {
                d = new DateTime(year, month + 1, day + 1, hour, minute, second);
            }
            catch { }
            return d;
        }
    }
}
