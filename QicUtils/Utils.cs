using System;
using System.IO;

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

        private static UInt16 conv_endian(UInt16 val)
        {
            UInt16 temp;
            temp = (UInt16)(val << 8); temp &= 0xFF00; temp |= (UInt16)((val >> 8) & 0xFF);
            return temp;
        }
        private static UInt32 conv_endian(UInt32 val)
        {
            UInt32 temp = (val & 0x000000FF) << 24;
            temp |= (val & 0x0000FF00) << 8;
            temp |= (val & 0x00FF0000) >> 8;
            temp |= (val & 0xFF000000) >> 24;
            return (temp);
        }
        private static UInt64 conv_endian(UInt64 val)
        {
            UInt64 temp = (val & 0xFF) << 56;
            temp |= (val & 0xFF00) << 40;
            temp |= (val & 0xFF0000) << 24;
            temp |= (val & 0xFF000000) << 8;
            temp |= (val & 0xFF00000000) >> 8;
            temp |= (val & 0xFF0000000000) >> 24;
            temp |= (val & 0xFF000000000000) >> 40;
            temp |= (val & 0xFF00000000000000) >> 56;
            return (temp);
        }


        public static UInt16 LittleEndian(UInt16 val)
        {
            if (BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        public static UInt32 LittleEndian(UInt32 val)
        {
            if (BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        public static UInt64 LittleEndian(UInt64 val)
        {
            if (BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }

        public static UInt16 BigEndian(UInt16 val)
        {
            if (!BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        public static UInt32 BigEndian(UInt32 val)
        {
            if (!BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
        public static UInt64 BigEndian(UInt64 val)
        {
            if (!BitConverter.IsLittleEndian) return val;
            return conv_endian(val);
        }
    }
}
