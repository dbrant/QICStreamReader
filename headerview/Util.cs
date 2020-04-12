using System;

namespace headerview
{
    public class Util
    {
		public static DateTime GetShortDateTime(uint date)
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

		public static string CleanString(string str)
		{
			return str.Replace("\0", "").Trim();
		}
	}
}
