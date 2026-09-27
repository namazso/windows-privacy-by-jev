using System.Buffers.Binary;
using System.Text;

namespace GpoCatalogue.Core.Pol;

/// <summary>Reader and writer for registry.pol (PReg version 1). The reader mirrors pipeline/preg.py.</summary>
public static class PolFile
{
    private static readonly byte[] Signature = "PReg"u8.ToArray();
    private const uint Version = 1;

    public static List<PolEntry> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Parse(memory.ToArray(), path);
    }

    /// <summary>A section without a file has no entries.</summary>
    public static List<PolEntry> ReadIfExists(string path) => File.Exists(path) ? Read(path) : [];

    public static List<PolEntry> Parse(byte[] buf, string source = "registry.pol")
    {
        if (buf.Length < 8 || !buf.AsSpan(0, 4).SequenceEqual(Signature)
            || BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)) != Version)
            throw new InvalidDataException($"{source}: not a PReg v1 file");

        var pos = 8;
        var entries = new List<PolEntry>();

        void Expect(char token)
        {
            if (pos + 2 > buf.Length || buf[pos] != token || buf[pos + 1] != 0)
                throw new InvalidDataException($"{source}: expected '{token}' at byte {pos}");
            pos += 2;
        }

        string ReadUtf16Z()
        {
            var end = pos;
            while (true)
            {
                if (end + 2 > buf.Length)
                    throw new InvalidDataException($"{source}: unterminated string at byte {pos}");
                if (buf[end] == 0 && buf[end + 1] == 0)
                    break;
                end += 2;
            }
            var text = Encoding.Unicode.GetString(buf, pos, end - pos);
            pos = end + 2;
            return text;
        }

        uint ReadUInt32()
        {
            if (pos + 4 > buf.Length)
                throw new InvalidDataException($"{source}: truncated at byte {pos}");
            var value = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos));
            pos += 4;
            return value;
        }

        while (pos < buf.Length)
        {
            Expect('[');
            var key = ReadUtf16Z();
            Expect(';');
            var valueName = ReadUtf16Z();
            Expect(';');
            var type = ReadUInt32();
            Expect(';');
            var size = ReadUInt32();
            Expect(';');
            if (size > buf.Length - pos)
                throw new InvalidDataException($"{source}: data of {size} bytes runs past the end at byte {pos}");
            var raw = buf.AsSpan(pos, (int)size).ToArray();
            pos += (int)size;
            Expect(']');
            entries.Add(new PolEntry(key, valueName, type, raw));
        }
        return entries;
    }

    public static byte[] Serialize(IEnumerable<PolEntry> entries)
    {
        using var memory = new MemoryStream();
        using (var w = new BinaryWriter(memory, Encoding.Unicode, leaveOpen: true))
        {
            w.Write(Signature);
            w.Write(Version);
            foreach (var e in entries)
            {
                w.Write((ushort)'[');
                WriteUtf16Z(w, e.Key);
                w.Write((ushort)';');
                WriteUtf16Z(w, e.ValueName);
                w.Write((ushort)';');
                w.Write(e.Type);
                w.Write((ushort)';');
                w.Write((uint)e.Raw.Length);
                w.Write((ushort)';');
                w.Write(e.Raw);
                w.Write((ushort)']');
            }
        }
        return memory.ToArray();
    }

    /// <summary>Writes through a temporary file so a reader never sees a half-written policy file.</summary>
    public static void Write(string path, IEnumerable<PolEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, Serialize(entries));
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteUtf16Z(BinaryWriter w, string text)
    {
        w.Write(Encoding.Unicode.GetBytes(text));
        w.Write((ushort)0);
    }
}
