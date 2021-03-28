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

	}
}
