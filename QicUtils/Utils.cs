using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QicUtils
{
    public enum Endianness
    {
        Little, Big, Pdp11
    }

    public class DecodeException : Exception
    {
        protected DecodeException() : base() { }
        public DecodeException(string message) : base(message) { }
    }

    public class ArgMap
    {
        private readonly List<string> args = new();
        private readonly Dictionary<string, string> map = new();

        public ArgMap(string[] args)
        {
            this.args.AddRange(args);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith('-') && i < (args.Length - 1))
                {
                    map.Add(args[i], args[i + 1]);
                }
            }
        }

        public string Get(string key, string defVal = "")
        {
            map.TryGetValue(key, out string val);
            return val ?? defVal;
        }

        public bool Has(string key)
        {
            return args.Contains(key);
        }
    }

    public class Utils
    {

		public static bool VerifyFileFormat(string fileName, byte[] bytes)
		{
			string nameLower = fileName.ToLower();

			if (nameLower.EndsWith(".exe") && (bytes[0] != 'M' || bytes[1] != 'Z')) { return false; }
			if (nameLower.EndsWith(".zip") && (bytes[0] != 'P' || bytes[1] != 'K')) { return false; }
			if (nameLower.EndsWith(".dwg") && (bytes[0] != 'A' || bytes[1] != 'C')) { return false; }

			return true;
		}

		public static string ReplaceInvalidChars(string filename)
		{
			string str = string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));
			str = string.Join("_", str.Split(Path.GetInvalidPathChars()));
			return str;
		}

		public static string CleanString(string str)
		{
			return str.Replace("\0", "").Trim();
		}

        public static string GetNullTerminatedString(string str)
        {
            int i = str.IndexOf('\0');
            return i >= 0 ? str.Substring(0, i) : str;
        }

        public static long StringOrHexToLong(string _str)
		{
            string str = _str.ToLower().Trim();
            if (str.StartsWith("0x") || str.StartsWith("&h") || str.EndsWith("h"))
            {
                str = str.Replace("0x", "").Replace("h", "").Replace("&", "");
                return Convert.ToInt64(str, 16);
            }
            return Convert.ToInt64(str);
        }

        public static DateTime GetQicDateTime(uint date)
		{
			DateTime d = new();
			int year = (int)((date & 0xFE000000) >> 25) + 1970;
			int s = (int)(date & 0x1FFFFFF);
			int second = s % 60; s /= 60;
			int minute = s % 60; s /= 60;
			int hour = s % 24; s /= 24;
			int day = s % 31; s /= 31;
			int month = s;
			try { d = new DateTime(year, month + 1, day + 1, hour, minute, second); }
			catch { }
			return d;
		}

        public static DateTime GetDosDateTime(int date, int time)
        {
            DateTime d = new();
            int sec = time & 0x1F; time >>= 5;
            int min = time & 0x3F; time >>= 6;
            int hour = time & 0x1F;
            int day = date & 0x1F; date >>= 5;
            int month = date & 0xF; date >>= 4;
            int year = 1980 + (date & 0x7F);
            try { d = new DateTime(year, month, day, hour, min, sec); }
            catch { }
            return d;
        }

        public static DateTime DateTimeFromTimeT(long timeT)
		{
			return new DateTime(1970, 1, 1).AddSeconds(timeT);
		}

        public static DateTime DateTimeFrom1904(long time)
        {
            var epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            try { epoch = epoch.AddSeconds(time); }
            catch { }
            return epoch;
        }

        public static DateTime GetMtfDateTime(byte[] bytes, int offset)
		{
			int year = (bytes[offset] << 6) | (bytes[offset + 1] >> 2);
			int month = ((bytes[offset + 1] & 0x3) << 2) | (bytes[offset + 2] >> 6);
			int day = (bytes[offset + 2] >> 1) & 0x1F;
			int hour = ((bytes[offset + 2] & 0x1) << 4) | (bytes[offset + 3] >> 4);
			int minute = ((bytes[offset + 3] & 0xF) << 2) | (bytes[offset + 4] >> 6);
			int second = bytes[offset + 4] & 0x3F;
			DateTime date;
			try { date = new DateTime(year, month, day, hour, minute, second); }
			catch { date = DateTime.Now; }
			return date;
		}

        public static string EbcdicToAscii(byte[] ebcdicData, int index, int count)
        {
            return Encoding.ASCII.GetString(Encoding.Convert(Encoding.GetEncoding("IBM037"), Encoding.ASCII, ebcdicData, index, count));
        }

        private static ushort ConvEndian(ushort val)
        {
            ushort temp;
            temp = (ushort)(val << 8); temp &= 0xFF00; temp |= (ushort)((val >> 8) & 0xFF);
            return temp;
        }
        private static uint ConvEndian(uint val)
        {
            uint temp = (val & 0x000000FF) << 24;
            temp |= (val & 0x0000FF00) << 8;
            temp |= (val & 0x00FF0000) >> 8;
            temp |= (val & 0xFF000000) >> 24;
            return temp;
        }
        private static ulong ConvEndian(ulong val)
        {
            ulong temp = (val & 0xFF) << 56;
            temp |= (val & 0xFF00) << 40;
            temp |= (val & 0xFF0000) << 24;
            temp |= (val & 0xFF000000) << 8;
            temp |= (val & 0xFF00000000) >> 8;
            temp |= (val & 0xFF0000000000) >> 24;
            temp |= (val & 0xFF000000000000) >> 40;
            temp |= (val & 0xFF00000000000000) >> 56;
            return temp;
        }

        public static ushort LittleEndian(ushort val)
        {
            return BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }
        public static uint LittleEndian(uint val)
        {
            return BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }
        public static ulong LittleEndian(ulong val)
        {
            return BitConverter.IsLittleEndian ? val : ConvEndian(val);
        }

        public static ushort BigEndian(ushort val)
        {
            return BitConverter.IsLittleEndian ? ConvEndian(val) : val;
        }
        public static uint BigEndian(uint val)
        {
            return BitConverter.IsLittleEndian ? ConvEndian(val) : val;
        }
        public static ulong BigEndian(ulong val)
        {
            return BitConverter.IsLittleEndian ? ConvEndian(val) : val;
        }

        public static uint Pdp11EndianInt(byte[] bytes, int offset)
        {
            uint temp = (uint)bytes[offset] << 16;
            temp |= (uint)bytes[offset + 1] << 24;
            temp |= (uint)bytes[offset + 2];
            temp |= (uint)bytes[offset + 3] << 8;
            return temp;
        }

        public static ushort GetUInt16(byte[] bytes, int offset, Endianness endianness)
        {
            if (endianness == Endianness.Big)
            {
                return BigEndian(BitConverter.ToUInt16(bytes, offset));
            }
            return LittleEndian(BitConverter.ToUInt16(bytes, offset));
        }

        public static uint GetUInt32(byte[] bytes, int offset, Endianness endianness)
        {
            if (endianness == Endianness.Big)
            {
                return BigEndian(BitConverter.ToUInt32(bytes, offset));
            }
            else if (endianness == Endianness.Pdp11)
            {
                return Pdp11EndianInt(bytes, offset);
            }
            return LittleEndian(BitConverter.ToUInt32(bytes, offset));
        }

        public static int Get3ByteInt(byte[] bytes, int offset, Endianness endianness)
        {
            int n;
            if (endianness == Endianness.Big)
            {
                n = (bytes[offset++] << 16);
                n |= (bytes[offset++] << 8);
                n |= bytes[offset++];
            }
            else if (endianness == Endianness.Pdp11)
            {
                n = (bytes[offset++] << 16);
                n |= bytes[offset++];
                n |= (bytes[offset++] << 8);
            }
            else
            {
                n = bytes[offset++];
                n |= (bytes[offset++] << 8);
                n |= (bytes[offset++] << 16);
            }
            return n;
        }
    }
}
