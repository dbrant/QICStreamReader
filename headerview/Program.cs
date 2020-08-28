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

					f.Seek(0, SeekOrigin.Begin);
					Vtbl1Record vtbl1 = new Vtbl1Record(f);
					if (vtbl1.Valid)
					{
						while (vtbl1.Valid)
						{
							Console.Write(vtbl1.ToString());
							Console.WriteLine("----------------");
							vtbl1 = new Vtbl1Record(f);
						}
						return;
					}

					f.Seek(0, SeekOrigin.Begin);
					Vtbl2Record vtbl2 = new Vtbl2Record(f);
					if (vtbl2.Valid)
					{
						Console.Write(vtbl2.ToString());
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
			MostRecentFormat = Util.GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			MostRecentWriteOrFormat = Util.GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4; ptr += 2;
			SegmentsPerTrack = BitConverter.ToUInt16(bytes, ptr); ptr += 2;

			TracksPerCartridge = bytes[ptr++];
			MaxFloppySide = bytes[ptr++];
			MaxFloppyTrack = bytes[ptr++];
			MaxFloppySector = bytes[ptr++];

			TapeName = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
			TapeNameTime = Util.GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			ptr = 128;
			ReformatErrorFlag = bytes[ptr++]; ptr++;
			NumSegmentsWritten = BitConverter.ToInt32(bytes, ptr); ptr += 4;
			ptr += 4;
			InitialFormatTime = Util.GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;
			FormatCount = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			ptr += 2;
			ManufacturerName = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
			ManufacturerLotCode = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 44)); ptr += 44;
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
	}

	public class Vtbl1Record
	{
		public bool Valid;
		public int StartSegNum;
		public int EndSegNum;
		public string VolumeDescription;
		public DateTime Date;
		public int VolumeFlags;
		public int MultiCartSeq;
		public int MajorSpecVer;
		public int MinorSpecVer;
		public string Password;
		public int DirSectionSize;
		public long DataSectionSize;
		public int OsVersion;
		public string SourceDrive;
		public int LogicalFileSet;
		public int CompressionMethod;
		public int FormatOsType;

		public Vtbl1Record(Stream stream)
		{
			byte[] bytes = new byte[0x80];
			long initialPos = stream.Position;
			int ptr = 0;
			stream.Read(bytes, 0, bytes.Length);
			if (Encoding.ASCII.GetString(bytes, ptr, 4) != "VTBL")
			{
				return;
			}
			ptr += 4;
			StartSegNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			EndSegNum = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			VolumeDescription = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 0x2C)); ptr += 0x2C;
			Date = Util.GetShortDateTime(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;

			VolumeFlags = bytes[ptr++];
			MultiCartSeq = bytes[ptr++];

			MajorSpecVer = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			MinorSpecVer = BitConverter.ToUInt16(bytes, ptr); ptr += 2;

			// vendor-specific data
			ptr += 22;

			Password = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 8)); ptr += 8;

			DirSectionSize = BitConverter.ToInt32(bytes, ptr); ptr += 4;
			DataSectionSize = BitConverter.ToInt64(bytes, ptr); ptr += 8;

			OsVersion = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			SourceDrive = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, 16)); ptr += 16;

			LogicalFileSet = bytes[ptr++];
			ptr++;
			CompressionMethod = bytes[ptr++];
			FormatOsType = bytes[ptr++];

			Valid = true;
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine("StartSegNum: " + StartSegNum);
			sb.AppendLine("EndSegNum: " + EndSegNum);
			sb.AppendLine("VolumeDescription: " + VolumeDescription);
			sb.AppendLine("Date: " + Date);
			sb.AppendLine(string.Format("VolumeFlags: 0x{0:X2}", VolumeFlags));
			sb.AppendLine("MultiCartSeq: " + MultiCartSeq);
			sb.AppendLine("MajorSpecVer: " + MajorSpecVer);
			sb.AppendLine("MinorSpecVer: " + MinorSpecVer);
			sb.AppendLine("Password: " + Password);
			sb.AppendLine("DirSectionSize: " + DirSectionSize);
			sb.AppendLine("DataSectionSize: " + DataSectionSize);
			sb.AppendLine("OsVersion: " + OsVersion);
			sb.AppendLine("SourceDrive: " + SourceDrive);
			sb.AppendLine("LogicalFileSet: " + LogicalFileSet);
			sb.AppendLine(string.Format("CompressionMethod: 0x{0:X2}", CompressionMethod));
			sb.AppendLine("FormatOsType: " + FormatOsType);
			return sb.ToString();
		}
	}

	public class Vtbl2Record
	{
		public bool Valid;
		public DateTime Date;
		public string ArchiveName;
		public string ArchiveDrive;

		public Vtbl2Record(Stream stream)
		{
			byte[] bytes = new byte[255];
			long initialPos = stream.Position;
			int ptr = 0;
			stream.Read(bytes, 0, 255);

			ptr += 4;
			if (Encoding.ASCII.GetString(bytes, ptr, 4) != "VTBL")
			{
				return;
			}

			ptr = 0x1C;
			Date = Util.DateTimeFromTimeT(BitConverter.ToUInt32(bytes, ptr)); ptr += 4;

			ptr = 0x3C;
			int nameLen = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			ArchiveName = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, nameLen)); ptr += nameLen;

			// align to even byte
			if (ptr % 2 > 0)
			{
				ptr++;
			}

			nameLen = BitConverter.ToUInt16(bytes, ptr); ptr += 2;
			ArchiveDrive = Util.CleanString(Encoding.ASCII.GetString(bytes, ptr, nameLen)); ptr += nameLen;
			Valid = true;
		}

		public override string ToString()
		{
			var sb = new StringBuilder();
			sb.AppendLine("ArchiveName: " + ArchiveName);
			sb.AppendLine("ArchiveDrive: " + ArchiveDrive);
			sb.AppendLine("Date: " + Date);
			return sb.ToString();
		}
	}

}