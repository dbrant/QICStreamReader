using System;
using System.IO;
using System.Text;

namespace QicUtils
{
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

        public static DateTime GetQicDateTime(uint date)
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
    }
}
