using QicUtils;
using System;
using System.IO;
using System.Text;


/// <summary>
/// 
/// Copyright Dmitry Brant, 2023.
/// 
/// </summary>
namespace arcserve
{
    class Program
    {
        private const uint FileHeaderBlock = 0x3A3A3A3A;

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            bool dryRun = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-f") { inFileName = args[i + 1]; }
                else if (args[i] == "-d") { baseDirectory = args[i + 1]; }
                else if (args[i] == "--dry") { dryRun = true; }
            }

            if (inFileName.Length == 0 || !File.Exists(inFileName))
            {
                Console.WriteLine("Usage:");
                return;
            }

            try
            {
                using var stream = new FileStream(inFileName, FileMode.Open, FileAccess.Read);
                while (stream.Position < stream.Length)
                {
                    // Make sure we're aligned properly.
                    if ((stream.Position % 0x100) > 0)
                    {
                        stream.Seek(0x100 - (stream.Position % 0x100), SeekOrigin.Current);
                    }

                    var header = new FileHeader(stream);
                    if (!header.Valid)
                    {
                        continue;
                    }

                    if (header.IsDirectory || header.Name.Trim() == "")
                    {
                        Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - " + header.Size.ToString() + " bytes");
                        continue;
                    }

                    if (header.Size == 0)
                    {
                        Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name);
                        Console.WriteLine("Warning: skipping zero-length file.");
                        continue;
                    }

                    var bytes = new byte[header.Size];
                    stream.Read(bytes, 0, (int)header.Size);
                    bool compressed = false;
                    if (bytes[0] == 0 && bytes[1] == 0x21)
                    {
                        compressed = true;
                    }

                    string filePath = compressed ? baseDirectory + "c" : baseDirectory;
                    string[] dirArray = header.Name.Split("\\");
                    string fileName = dirArray[^1];
                    for (int i = 0; i < dirArray.Length - 1; i++)
                    {
                        filePath = Path.Combine(filePath, dirArray[i]);
                    }

                    if (!dryRun)
                    {
                        Directory.CreateDirectory(filePath);

                        filePath = Path.Combine(filePath, fileName);

                        // Make sure the fully qualified name does not exceed 260 chars
                        if (filePath.Length >= 260)
                        {
                            filePath = filePath[..259];
                        }

                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            f.Write(bytes);
                            f.Flush();
                        }

                        try
                        {
                            //File.SetCreationTime(filePath, header.CreateDate);
                            //File.SetLastWriteTime(filePath, header.ModifyDate);
                            //File.SetAttributes(filePath, header.Attributes);
                        }
                        catch { }
                    }
                    else
                    {
                        filePath = Path.Combine(filePath, fileName);
                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.Message);
                Console.Write(e.StackTrace);
            }
        }

        private class FileHeader
        {
            public long Size { get; }
            public string Name { get; }
            public string DosName { get; }
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public DateTime AccessDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[0x60];

                stream.Read(bytes, 0, bytes.Length);
                if (Utils.LittleEndian(BitConverter.ToUInt32(bytes, 0)) != FileHeaderBlock) { return; }

                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, 4));

                Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0x10, 0x50));
                if (Name.Length == 0) { return; }

                // remove leading drive letter, if any
                if (Name[1] == ':' && Name[2] == '\\')
                {
                    Name = Name[3..];
                }

                DosName = Name;

                int date = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0x8));
                int time = Utils.LittleEndian(BitConverter.ToUInt16(bytes, 0xA));

                CreateDate = Utils.GetDosDateTime(date, time);
                ModifyDate = CreateDate;
                AccessDate = ModifyDate;

                Attributes = (FileAttributes)bytes[0xC];

                Valid = true;
            }

        }

    }
}
