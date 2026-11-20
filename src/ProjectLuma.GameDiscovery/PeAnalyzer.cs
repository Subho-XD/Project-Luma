using System.Text;
using ProjectLuma.Core.Interfaces;

namespace ProjectLuma.GameDiscovery;

/// <summary>Safe binary PE32/PE32+ header &amp; Import Table inspector without loading DLLs into process.</summary>
public sealed class PeAnalyzer : IPeAnalyzer
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileDll = 0x2000;
    private const ushort Pe32PlusMagic = 0x020B;

    private sealed record SectionRange(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

    public PeInspectionResult? InspectFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);
            if (fs.Length < 64) return null;

            ushort dosMagic = br.ReadUInt16();
            if (dosMagic != 0x5A4D) return null; // 'MZ'

            fs.Position = 0x3C;
            uint peOffset = br.ReadUInt32();
            if (peOffset + 24 > fs.Length) return null;

            fs.Position = peOffset;
            uint peSig = br.ReadUInt32();
            if (peSig != 0x00004550) return null; // 'PE\0\0'

            ushort machine = br.ReadUInt16();
            ushort numberOfSections = br.ReadUInt16();
            fs.Position += 12; // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            ushort sizeOfOptionalHeader = br.ReadUInt16();
            ushort characteristics = br.ReadUInt16();

            var result = new PeInspectionResult
            {
                Is64Bit = machine == ImageFileMachineAmd64,
                IsDll = (characteristics & ImageFileDll) != 0
            };

            long optStart = fs.Position;
            if (sizeOfOptionalHeader < 112 || optStart + sizeOfOptionalHeader > fs.Length) return result;

            ushort optMagic = br.ReadUInt16();
            bool isPe32Plus = optMagic == Pe32PlusMagic;
            result.Is64Bit = result.Is64Bit && isPe32Plus;

            // DataDirectory[1] is Import Table: offset 104 in PE32, 120 in PE32+
            long importDirOffset = optStart + (isPe32Plus ? 120 : 104);
            if (importDirOffset + 8 > optStart + sizeOfOptionalHeader) return result;

            fs.Position = importDirOffset;
            uint importRva = br.ReadUInt32();
            uint importSize = br.ReadUInt32();

            long sectionTableStart = optStart + sizeOfOptionalHeader;
            var sections = new List<SectionRange>(numberOfSections);
            for (int i = 0; i < numberOfSections; i++)
            {
                long pos = sectionTableStart + i * 40L;
                if (pos + 40 > fs.Length) break;
                fs.Position = pos + 8;
                uint vsize = br.ReadUInt32();
                uint vaddr = br.ReadUInt32();
                uint rsize = br.ReadUInt32();
                uint roff = br.ReadUInt32();
                sections.Add(new SectionRange(vaddr, Math.Max(vsize, rsize), roff, rsize));
            }

            if (importRva == 0 || importSize == 0) return result;

            if (!TryRvaToOffset(sections, importRva, out uint importFileOffset)) return result;

            for (int entryIndex = 0; entryIndex < 512; entryIndex++)
            {
                long entryPos = importFileOffset + entryIndex * 20L;
                if (entryPos + 20 > fs.Length) break;

                fs.Position = entryPos;
                uint originalFirstThunk = br.ReadUInt32();
                uint timeDateStamp = br.ReadUInt32();
                uint forwarderChain = br.ReadUInt32();
                uint nameRva = br.ReadUInt32();
                uint firstThunk = br.ReadUInt32();

                if (originalFirstThunk == 0 && timeDateStamp == 0 && forwarderChain == 0 && nameRva == 0 && firstThunk == 0)
                    break;

                if (nameRva != 0 && TryRvaToOffset(sections, nameRva, out uint nameOffset))
                {
                    string dllName = ReadAsciiZ(fs, nameOffset);
                    if (!string.IsNullOrWhiteSpace(dllName))
                        result.ImportedDlls.Add(dllName);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryRvaToOffset(List<SectionRange> sections, uint rva, out uint fileOffset)
    {
        foreach (var s in sections)
        {
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize)
            {
                fileOffset = s.RawOffset + (rva - s.VirtualAddress);
                return true;
            }
        }
        fileOffset = 0;
        return false;
    }

    private static string ReadAsciiZ(FileStream fs, uint offset)
    {
        if (offset >= fs.Length) return string.Empty;
        long saved = fs.Position;
        fs.Position = offset;
        var bytes = new List<byte>(64);
        int b;
        while (bytes.Count < 260 && (b = fs.ReadByte()) > 0)
            bytes.Add((byte)b);
        fs.Position = saved;
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
}
