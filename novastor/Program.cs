using QicUtils;
using System;
using System.IO;
using System.Text;


/// <summary>
/// 
/// Decoder for tape backup images made with a legacy backup utility,
/// identifiable by the presence of the string "<<NoVaStOr>>" in the header(s).
/// 
/// Copyright Dmitry Brant, 2023.
/// 
/// </summary>
namespace novastor
{
    class Program
    {
        private const int BlockSize = 0x400;
        private const string Magic = "<<NoVaStOr>>";

        static void Main(string[] args)
        {
            string inFileName = "";
            string baseDirectory = "out";

            bool dryRun = false;
            byte[] bytes = new byte[0x10000];

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
                    if ((stream.Position % BlockSize) > 0)
                    {
                        stream.Seek(BlockSize - (stream.Position % BlockSize), SeekOrigin.Current);
                    }


                    var header = new FileHeader(stream);
                    if (!header.Valid)
                    {
                        continue;
                    }

                    // file contents follow immediately after the header.

                    Console.WriteLine(stream.Position.ToString("X") + ": " + header.Name + " - " + header.Size.ToString() + " bytes");

                    if (header.IsDirectory || header.Name.Trim() == "")
                    {
                        continue;
                    }

                    if (header.Size == 0)
                    {
                        Console.WriteLine("Warning: skipping zero-length file.");
                        continue;
                    }

                    string filePath = baseDirectory;
                    string[] dirArray = header.Name.Split("\\");
                    string fileName = dirArray[^1];
                    for (int i = 0; i < dirArray.Length - 1; i++)
                    {
                        filePath = Path.Combine(filePath, dirArray[i].Replace(":", ""));
                    }

                    if (!dryRun)
                    {
                        Directory.CreateDirectory(filePath);

                        filePath = Path.Combine(filePath, fileName);


                        while (File.Exists(filePath))
                        {
                            Console.WriteLine("Warning: file already exists (amending name): " + filePath);
                            filePath += "_";
                        }

                        Console.WriteLine(stream.Position.ToString("X") + ": " + filePath + " - " + header.Size.ToString() + " bytes - " + header.CreateDate.ToShortDateString());

                        using (var f = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            long bytesLeft = header.Size;
                            while (bytesLeft > 0)
                            {
                                int bytesToRead = bytes.Length;
                                if (bytesToRead > bytesLeft) { bytesToRead = (int)bytesLeft; }
                                stream.Read(bytes, 0, bytesToRead);
                                f.Write(bytes, 0, bytesToRead);

                                if (bytesLeft == header.Size)
                                {
                                    if (!Utils.VerifyFileFormat(header.Name, bytes))
                                    {
                                        Console.WriteLine(stream.Position.ToString("X") + " -- Warning: file format doesn't match: " + filePath);
                                    }
                                }

                                bytesLeft -= bytesToRead;
                            }

                            f.Flush();
                        }

                        try
                        {
                            File.SetCreationTime(filePath, header.CreateDate);
                            File.SetLastWriteTime(filePath, header.ModifyDate);
                            File.SetAttributes(filePath, header.Attributes);
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
            public DateTime CreateDate { get; }
            public DateTime ModifyDate { get; }
            public FileAttributes Attributes { get; }
            public bool Valid { get; }
            public bool IsDirectory { get; }

            public FileHeader(Stream stream)
            {
                byte[] bytes = new byte[0x1000];
                stream.Read(bytes, 0, 0x80);

                int bytePtr = 0;
                Size = Utils.LittleEndian(BitConverter.ToUInt32(bytes, bytePtr)); bytePtr += 4;

                CreateDate = Utils.GetDosDateTime(Utils.LittleEndian(BitConverter.ToUInt16(bytes, bytePtr)),
                    Utils.LittleEndian(BitConverter.ToUInt16(bytes, bytePtr + 2))); bytePtr += 4;
                ModifyDate = Utils.GetDosDateTime(Utils.LittleEndian(BitConverter.ToUInt16(bytes, bytePtr)),
                    Utils.LittleEndian(BitConverter.ToUInt16(bytes, bytePtr + 2))); bytePtr += 4;

                bytePtr++;
                Attributes = (FileAttributes)bytes[bytePtr++];

                IsDirectory = (Attributes & FileAttributes.Directory) == FileAttributes.Directory;

                bool veryLongName = false;
                if (bytes[bytePtr] != 0xFF)
                {
                    Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, bytePtr, 0x52)).Trim();
                }
                else
                {
                    Name = "";
                    veryLongName = true;
                }
                bytePtr += 0x52;

                bytePtr = 0x74;
                string magic = Encoding.ASCII.GetString(bytes, bytePtr, 0xC);
                if (magic != Magic)
                    return;

                if (veryLongName)
                {
                    stream.Read(bytes, 0, 0x100);
                    Name = Utils.GetNullTerminatedString(Encoding.ASCII.GetString(bytes, 0, 0x100)).Trim();
                }

                if (Name.Length == 0) { return; }

                Valid = true;
            }
        }

    }
}
