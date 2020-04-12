using System;
using System.IO;
using System.Text;

namespace headerview
{
    class Program
    {
        static void Main(string[] args)
        {
			if (args.Length < 1)
			{
				Console.WriteLine("Usage: headerview <tape_header.bin>");
				return;
			}

			try
			{
				using (var f = new FileStream(args[0], FileMode.Open, FileAccess.Read))
				{
					FormatParamRecord record = new FormatParamRecord(f);
					if (record.Valid)
					{
						Console.Write(record.ToString());
						return;
					}

				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Error: " + e.Message);
			}
        }
    }

	public class FormatParamRecord
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
			byte[] bytes = new byte[255];
			long initialPos = stream.Position;
			int ptr = 0;
			stream.Read(bytes, 0, 255);
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
			MostRecentFormat = GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			MostRecentWriteOrFormat = GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4; ptr += 2;
			SegmentsPerTrack = BitConverter.ToUInt16(bytes, ptr); ptr += 2;

			TracksPerCartridge = bytes[ptr++];
			MaxFloppySide = bytes[ptr++];
			MaxFloppyTrack = bytes[ptr++];
			MaxFloppySector = bytes[ptr++];

			TapeName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
			TapeNameTime = GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			ptr = 128;
			ReformatErrorFlag = bytes[ptr++]; ptr++;
			NumSegmentsWritten = BitConverter.ToInt32(bytes, ptr); ptr += 4;
			ptr += 4;
			InitialFormatTime = GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			FormatCount = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			ptr += 2;
			ManufacturerName = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
			ManufacturerLotCode = CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
			Valid = true;
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine("FormatCode: " + FormatCode);
			sb.AppendLine("Revision: " + Revision);
			sb.AppendLine("HeaderSegNum: " + HeaderSegNum);
			sb.AppendLine("HeaderSegDupNum: " + HeaderSegDupNum);
			sb.AppendLine("DataSegFirstLogicalArea: " + DataSegFirstLogicalArea);
			sb.AppendLine("DataSegLastLogicalArea: " + DataSegLastLogicalArea);
			sb.AppendLine("MostRecentFormat: " + MostRecentFormat);
			sb.AppendLine("MostRecentWriteOrFormat: " + MostRecentWriteOrFormat);
			sb.AppendLine("SegmentsPerTrack: " + SegmentsPerTrack);
			sb.AppendLine("TracksPerCartridge: " + TracksPerCartridge);
			sb.AppendLine("MaxFloppySide: " + MaxFloppySide);
			sb.AppendLine("MaxFloppyTrack: " + MaxFloppyTrack);
			sb.AppendLine("MaxFloppySector: " + MaxFloppySector);
			sb.AppendLine("TapeName: " + TapeName);
			sb.AppendLine("TapeNameTime: " + TapeNameTime);
			sb.AppendLine("ReformatErrorFlag: " + ReformatErrorFlag);
			sb.AppendLine("NumSegmentsWritten: " + NumSegmentsWritten);
			sb.AppendLine("InitialFormatTime: " + InitialFormatTime);
			sb.AppendLine("FormatCount: " + FormatCount);
			sb.AppendLine("ManufacturerName: " + ManufacturerName);
			sb.AppendLine("ManufacturerLotCode: " + ManufacturerLotCode);
			return sb.ToString();
		}

		private static DateTime GetShortDateTime(uint date)
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

		private static string CleanString(string str)
		{
			return str.Replace("\0", "").Trim();
		}
	}
}
