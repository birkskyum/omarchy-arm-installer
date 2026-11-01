using System.Text;

namespace OmarchyArmInstaller.Core.Hardware;

/// Takes the one thing in the ACPI tables that identifies a unit rather than
/// a model: Qualcomm's DSDT declares Name (SOSN, ...) and the firmware fills
/// it with the SoC's serial number at boot, so it tells two of the same
/// laptop apart the way the SMBIOS serial numbers do.
public static class AcpiRedact
{
    /// Named objects whose value the firmware fills with a serial number.
    static readonly byte[][] SerialNames = ["SOSN"u8.ToArray()];

    const byte NameOp = 0x08, ZeroOp = 0x00, OneOp = 0x01, OnesOp = 0xFF;
    const byte BytePrefix = 0x0A, WordPrefix = 0x0B, DWordPrefix = 0x0C, StringPrefix = 0x0D, QWordPrefix = 0x0E, BufferOp = 0x11;

    /// The table with each such name's value zeroed and the checksum put
    /// right, and the values it held as text (hex and decimal), so they can be
    /// taken out of everything else that spells them. The same array comes
    /// back when the table holds none.
    public static (byte[] Table, List<string> Serials) Redact(byte[] table)
    {
        var serials = new List<string>();
        byte[]? copy = null;
        foreach (var name in SerialNames)
        {
            for (var at = IndexOf(table, name, 1); at > 0; at = IndexOf(table, name, at + 1))
            {
                if (table[at - 1] != NameOp) continue;
                var value = at + name.Length;
                if (value >= table.Length) break;
                var (start, length, texts) = DataObject(copy ?? table, value);
                if (length == 0) continue;
                copy ??= (byte[])table.Clone();
                serials.AddRange(texts);
                Array.Clear(copy, start, length);
                // Zero is a serial number too, as far as the AML is concerned:
                // a Name's integer value is read, not its encoding.
            }
        }
        if (copy is null) return (table, serials);
        copy[9] = 0;
        byte sum = 0;
        foreach (var b in copy) sum += b;
        copy[9] = (byte)(0x100 - sum);
        return (copy, serials.Distinct().ToList());
    }

    /// The data object at <at>: where its payload starts, how long it is, and
    /// its value as text, in the spellings it may have elsewhere. A length of
    /// 0 when it is not one this understands.
    static (int Start, int Length, string[] Texts) DataObject(byte[] t, int at)
    {
        (int, int, string[]) none = (0, 0, []);
        int width = t[at] switch { BytePrefix => 1, WordPrefix => 2, DWordPrefix => 4, QWordPrefix => 8, _ => 0 };
        if (width > 0)
        {
            if (at + 1 + width > t.Length) return none;
            ulong v = 0;
            for (var i = width - 1; i >= 0; i--) v = (v << 8) | t[at + 1 + i];
            return (at + 1, width, v == 0 ? [] : [$"{v:x}", $"{v}"]);
        }
        if (t[at] is ZeroOp or OneOp or OnesOp) return none; // nothing in it to hide
        if (t[at] == StringPrefix)
        {
            var end = Array.IndexOf(t, (byte)0, at + 1);
            if (end < 0) return none;
            // Keep it a string: the terminator stays, the characters go.
            return (at + 1, end - (at + 1), [Encoding.ASCII.GetString(t, at + 1, end - (at + 1))]);
        }
        if (t[at] == BufferOp)
        {
            // PkgLength, then the buffer's size as an integer, then its bytes.
            var p = at + 1;
            var lead = t[p];
            var count = (lead >> 6) + 1;
            if (p + count > t.Length) return none;
            var pkg = count == 1 ? lead & 0x3F : lead & 0x0F;
            for (var i = 1; i < count; i++) pkg |= t[p + i] << (4 + 8 * (i - 1));
            var end = p + pkg;
            p += count;
            if (end > t.Length || p >= end) return none;
            var sizeWidth = t[p] switch { BytePrefix => 1, WordPrefix => 2, DWordPrefix => 4, _ => -1 };
            if (sizeWidth < 0) return none;
            p += 1 + sizeWidth;
            if (p > end) return none;
            var bytes = t.AsSpan(p, end - p);
            return (p, end - p, bytes.IndexOfAnyExcept((byte)0) < 0 ? [] : [Convert.ToHexString(bytes).ToLowerInvariant()]);
        }
        return none;
    }

    static int IndexOf(byte[] hay, byte[] needle, int from)
    {
        var i = hay.AsSpan(from).IndexOf(needle);
        return i < 0 ? -1 : from + i;
    }
}
