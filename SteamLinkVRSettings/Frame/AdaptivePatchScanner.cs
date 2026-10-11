using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SteamLinkVRSettings.Frame;

// Accepts moved code/data only when all independently checked instruction contexts,
// function bounds, codec structure layouts and imported memcpy remain recognizable.
// There is no "best match": ambiguous or changed layouts are refused.
public static class AdaptivePatchScanner
{
    private const string Caps = "F3 44 0F 10 15 ?? ?? ?? ?? F3 0F 11 8F 40 01 00 00 49 8B 8E 88 01 00 00 48 8B 01 FF 90 88 00 00 00 84 C0 74 09 F3 44 0F 10 15 ?? ?? ?? ??";
    private const string Preset = "FF 96 A0 9B 00 00 48 8B 8E 70 02 00 00 48 8D 95 18 03 00 00 41 B8 00 0E 00 00 E8 ?? ?? ?? ?? 66 0F 6F 05 ?? ?? ?? ?? F3 0F 7F 44 24 30 C7 44 24 40 1B 00 00 00";
    private const string Level = "B8 BA 00 00 00 C6 45 90 00 F3 0F 10 81 B8 00 00 00 B9 B7 00 00 00 F3 0F 58 05 ?? ?? ?? ?? F3 48 0F 2C D0 66 0F 6F 05 ?? ?? ?? ?? 49 0F AF D0 F3 0F 7F 45 A0 41 B8 09 00 00 00 49 0F AF D1 48 81 FA 00 00 80 7F 48 8D 15 ?? ?? ?? ?? 0F 4F C8 48 8B 86 70 02 00 00 89 88 A8 00 00 00 48 8B 86 70 02 00 00 C7 80 AC 00 00 00 01 00 00 00";
    private const string Api = "C7 85 ?? ?? ?? ?? 0B 00 02 71 41 B8 F4 09 00 00";
    private const string PresetVersions = "C7 85 10 03 00 00 0B 00 04 F1 C7 85 18 03 00 00 0B 00 07 F1";

    public static PatchManifest Discover(byte[] source)
    {
        var pe = new Image(source);
        var cap = pe.Unique(Caps);
        var preset = pe.Unique(Preset);
        var level = pe.Unique(Level);
        var api = pe.Unique(Api);
        pe.FunctionContaining(api.Offset, api.Length);
        var presetFunction = pe.FunctionContaining(preset.Offset, preset.Length);
        pe.FunctionContaining(cap.Offset, cap.Length);
        var levelFunction = pe.FunctionContaining(level.Offset, level.Length);
        Require(levelFunction.Begin != presetFunction.Begin, "Preset and reconfiguration signatures are not distinct functions.");
        Require(CountMatches(source, pe.Offset(presetFunction.Begin), checked((int)(presetFunction.End - presetFunction.Begin)), Pattern(PresetVersions)) == 1,
            "The NVENC preset structure version/layout changed.");
        // These loads are the general and wireless-adapter-present ceiling selectors.
        var generalRva = pe.Relative(cap.Offset + 5, cap.Offset + 9);
        var wirelessRva = pe.Relative(cap.Offset + 42, cap.Offset + 46);
        var general = pe.ReadOnlyData(generalRva, 4);
        var wireless = pe.ReadOnlyData(wirelessRva, 4);
        Require(generalRva == wirelessRva + 4 && source.AsSpan(general, 4).SequenceEqual(Convert.FromHexString("0000af43")) &&
            source.AsSpan(wireless, 4).SequenceEqual(Convert.FromHexString("00006843")), "Bitrate cap constants or their references changed.");
        // The precise E00-byte copy follows NVENC's HEVC preset query. Resolve its
        // call target through an actual PE import; a similarly named/static function
        // is not enough to establish the tail-call contract.
        var hook = preset.Offset + 26;
        Require(source[hook] == 0xe8, "Preset copy is not a direct rel32 call.");
        var memcpyRva = pe.Relative(hook + 1, hook + 5);
        var memcpy = pe.Offset(memcpyRva, 6);
        Require(pe.IsExecutable(memcpyRva, 6) && source[memcpy] == 0xff && source[memcpy + 1] == 0x25, "Preset memcpy import thunk changed.");
        var iat = pe.Relative(memcpy + 2, memcpy + 6);
        Require(pe.MemcpyImports().Contains(iat), "Preset copy does not call an imported CRT memcpy.");
        var caveRva = checked((pe.Text.Rva + pe.Text.VirtualSize + 15u) & ~15u);
        Require((ulong)caveRva + 15 <= (ulong)pe.Text.Rva + (uint)pe.Text.RawSize, "No safe executable padding for the HEVC level stub.");
        var cave = pe.Offset(caveRva, 15);
        Require(source.AsSpan(cave, 15).IndexOfAnyExcept((byte)0) < 0, "Executable padding is occupied.");
        Require(pe.Functions.All(f => f.End <= pe.Text.Rva + pe.Text.VirtualSize), "A function extends into executable padding.");
        var stub = Convert.FromHexString("c782a8000000ba000000e900000000");
        BinaryPrimitives.WriteInt32LittleEndian(stub.AsSpan(11), Rel32(memcpyRva, caveRva + 15));
        var call = new byte[] { 0xe8, 0, 0, 0, 0 };
        BinaryPrimitives.WriteInt32LittleEndian(call.AsSpan(1), Rel32(caveRva, pe.Rva(hook + 5)));
        var changes = new List<PatchSegment>();
        Add(wireless, Convert.FromHexString("0000fa43"), "Wireless-adapter-present bitrate cap");
        Add(general, Convert.FromHexString("0000fa43"), "General bitrate cap");
        Add(level.Offset + 18, Convert.FromHexString("ba000000"), "HEVC reconfiguration default level 6.2");
        Add(cave, stub, "Preset HEVC level 6.2 leaf tail call");
        Add(hook, call, "Route preset copy through the level fix");
        var virtualSize = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(virtualSize, checked(caveRva + 15 - pe.Text.Rva));
        Add(pe.Text.Header + 8, virtualSize, "Include the leaf tail call in executable virtual size");
        var patched = (byte[])source.Clone();
        foreach (var change in changes) change.PatchedBytes.CopyTo(patched, change.FileOffset);
        var result = new PatchManifest
        {
            SchemaVersion = 1, FileLength = source.Length, CapMbps = 500, HevcLevel = 186,
            OriginalSha256 = Hash(source), PatchedSha256 = Hash(patched), Changes = changes
        };
        result.Validate(requirePinned: false);
        return result;

        void Add(int offset, byte[] after, string reason) => changes.Add(new PatchSegment
        {
            FileOffset = offset, OriginalHex = Convert.ToHexString(source.AsSpan(offset, after.Length)).ToLowerInvariant(),
            PatchedHex = Convert.ToHexString(after).ToLowerInvariant(), Reason = reason
        });
    }

    private static int Rel32(uint target, uint next) => checked((int)((long)target - next));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static byte?[] Pattern(string text) => text.Split(' ').Select(x => x == "??" ? (byte?)null : Convert.ToByte(x, 16)).ToArray();
    private static bool Matches(byte[] source, int start, byte?[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++) if (pattern[i] is byte expected && source[start + i] != expected) return false;
        return true;
    }
    private static int CountMatches(byte[] source, int start, int length, byte?[] pattern)
    {
        int count = 0;
        for (int i = start; i <= start + length - pattern.Length; i++) if (Matches(source, i, pattern)) count++;
        return count;
    }

    private sealed record Section(int Header, uint Rva, uint VirtualSize, int Raw, int RawSize, uint Flags);
    private sealed record Function(uint Begin, uint End);
    private sealed class Image
    {
        private readonly byte[] data;
        private readonly List<Section> sections = [];
        private readonly int optional;
        public Section Text { get; }
        public List<Function> Functions { get; } = [];
        public Image(byte[] bytes)
        {
            data = bytes;
            Require(bytes.Length is >= 4096 and <= 128 * 1024 * 1024 && U16(0) == 0x5a4d, "Not a bounded PE image.");
            var pe = checked((int)U32(0x3c));
            Require(U32(pe) == 0x4550 && U16(pe + 4) == 0x8664, "Only x64 Windows PE drivers are supported.");
            var count = U16(pe + 6); var size = U16(pe + 20); optional = pe + 24;
            Require(count is > 0 and <= 64 && size >= 240 && U16(optional) == 0x20b && U32(optional + 108) >= 4, "Unsupported PE optional header.");
            var headers = checked((int)U32(optional + 60));
            Require(headers <= data.Length && headers >= optional + size + count * 40, "PE section headers exceed the header area.");
            for (int i = 0; i < count; i++)
            {
                var h = optional + size + i * 40;
                var section = new Section(h, U32(h + 12), U32(h + 8), checked((int)U32(h + 20)), checked((int)U32(h + 16)), U32(h + 36));
                Require(section.Raw >= headers && section.RawSize > 0 && section.Raw <= bytes.Length - section.RawSize &&
                    (ulong)section.Rva + Math.Max(section.VirtualSize, (uint)section.RawSize) <= U32(optional + 56), "Invalid PE section extent.");
                sections.Add(section);
            }
            foreach (var a in sections)
                foreach (var b in sections.Where(b => !ReferenceEquals(a, b)))
                    Require((long)a.Raw + a.RawSize <= b.Raw || (long)b.Raw + b.RawSize <= a.Raw, "PE raw sections overlap.");
            var virtualSections = sections.OrderBy(s => s.Rva).ToArray();
            for (int i = 1; i < virtualSections.Length; i++)
                Require((ulong)virtualSections[i - 1].Rva + Math.Max(virtualSections[i - 1].VirtualSize, (uint)virtualSections[i - 1].RawSize) <= virtualSections[i].Rva, "PE virtual sections overlap.");
            var executable = sections.Where(s => (s.Flags & 0x20000000) != 0).OrderByDescending(s => s.VirtualSize).ToArray();
            Require(executable.Length > 0 && executable.All(s => (s.Flags & 0x80000000) == 0) &&
                (executable.Length == 1 || executable[0].VirtualSize > executable[1].VirtualSize), "Expected a unique primary non-writable executable section.");
            Text = executable[0];
            var exceptionRva = U32(optional + 112 + 3 * 8); var exceptionSize = U32(optional + 116 + 3 * 8);
            Require(exceptionSize > 0 && exceptionSize % 12 == 0 && exceptionSize <= 4 * 1024 * 1024, "Missing or malformed x64 function table.");
            int table = Offset(exceptionRva, checked((int)exceptionSize)); uint previousEnd = 0;
            for (int p = table; p < table + exceptionSize; p += 12)
            {
                var begin = U32(p); var end = U32(p + 4); var unwind = U32(p + 8);
                Require(begin >= previousEnd && end > begin && IsExecutable(begin, checked((int)(end - begin))), "Malformed or overlapping function bounds.");
                Offset(unwind, 4); Functions.Add(new(begin, end)); previousEnd = end;
            }
        }
        public (int Offset, int Length) Unique(string signature)
        {
            var pattern = Pattern(signature); int found = -1;
            for (int p = Text.Raw; p <= Text.Raw + Math.Min(Text.RawSize, checked((int)Text.VirtualSize)) - pattern.Length; p++)
                if (Matches(data, p, pattern))
                {
                    Require(found < 0, "Instruction signature is ambiguous."); found = p;
                }
            Require(found >= 0, "A required instruction signature/layout was not found.");
            return (found, pattern.Length);
        }
        public Function FunctionContaining(int offset, int length)
        {
            var start = Rva(offset); var end = Rva(offset + length - 1) + 1;
            return Functions.SingleOrDefault(f => f.Begin <= start && end <= f.End)
                ?? throw new InvalidDataException("Instruction context is outside a declared x64 function.");
        }
        public uint Rva(int offset)
        {
            var s = sections.SingleOrDefault(s => s.Raw <= offset && offset < (long)s.Raw + s.RawSize)
                ?? throw new InvalidDataException("File offset is not in a PE section.");
            return checked(s.Rva + (uint)(offset - s.Raw));
        }
        public int Offset(uint rva, int length = 1)
        {
            Require(length > 0, "Invalid image read.");
            var s = sections.SingleOrDefault(s => s.Rva <= rva && (ulong)rva + (uint)length <= (ulong)s.Rva + (uint)s.RawSize)
                ?? throw new InvalidDataException("Referenced RVA is not backed by a bounded PE section.");
            return checked(s.Raw + (int)(rva - s.Rva));
        }
        public bool IsExecutable(uint rva, int length) => rva >= Text.Rva && (ulong)rva + (uint)length <= (ulong)Text.Rva + Text.VirtualSize;
        public int ReadOnlyData(uint rva, int length)
        {
            var offset = Offset(rva, length);
            Require(sections.Any(s => s.Rva <= rva && (ulong)rva + (uint)length <= (ulong)s.Rva + s.VirtualSize && (s.Flags & 0xa0000000) == 0 && (s.Flags & 0x40000000) != 0), "Cap reference is not read-only data.");
            return offset;
        }
        public uint Relative(int displacement, int next) => checked((uint)((long)Rva(next - 1) + 1 + I32(displacement)));
        public HashSet<uint> MemcpyImports()
        {
            var imports = new HashSet<uint>();
            uint rva = U32(optional + 112 + 8); uint size = U32(optional + 116 + 8);
            Require(size is >= 20 and <= 1024 * 1024, "Missing or malformed import directory.");
            var start = Offset(rva, checked((int)size)); bool terminated = false;
            for (int p = start; p <= start + size - 20; p += 20)
            {
                var lookup = U32(p); var name = U32(p + 12); var iat = U32(p + 16);
                if (lookup == 0 && name == 0 && iat == 0) { terminated = true; break; }
                var dll = CString(name);
                Require(lookup != 0 && iat != 0, "Unresolved import lookup table.");
                bool end = false;
                for (uint index = 0; index < 4096; index++)
                {
                    var entry = U64(Offset(checked(lookup + index * 8), 8));
                    if (entry == 0) { end = true; break; }
                    if ((entry & (1ul << 63)) != 0) continue;
                    Require(entry <= uint.MaxValue, "Invalid import name RVA.");
                    if (CString(checked((uint)entry + 2)) == "memcpy" && IsCrt(dll))
                    {
                        var slot = checked(iat + index * 8); Offset(slot, 8); imports.Add(slot);
                    }
                }
                Require(end, "Import lookup table is unterminated.");
            }
            Require(terminated && imports.Count > 0, "CRT memcpy import unavailable.");
            return imports;
        }
        private static bool IsCrt(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower is "ucrtbase.dll" or "api-ms-win-crt-string-l1-1-0.dll") return true;
            if (!lower.StartsWith("vcruntime") || !lower.EndsWith(".dll")) return false;
            var versions = lower[9..^4].Split('_');
            return versions.Length is 1 or 2 && versions.All(v => v.Length > 0 && v.All(c => c is >= '0' and <= '9'));
        }
        private string CString(uint rva)
        {
            var p = Offset(rva); var bytes = new List<byte>();
            for (int i = 0; i < 256; i++)
            {
                var next = Offset(checked(rva + (uint)i));
                if (data[next] == 0) return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
                Require(data[next] is >= 32 and < 127, "Import name is not bounded ASCII."); bytes.Add(data[next]);
            }
            throw new InvalidDataException("Import name is unterminated.");
        }
        private ReadOnlySpan<byte> Read(int offset, int length)
        {
            Require(offset >= 0 && offset <= data.Length - length, "PE field exceeds the file."); return data.AsSpan(offset, length);
        }
        private ushort U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(Read(p, 2));
        private uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(Read(p, 4));
        private ulong U64(int p) => BinaryPrimitives.ReadUInt64LittleEndian(Read(p, 8));
        private int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(Read(p, 4));
    }
}
