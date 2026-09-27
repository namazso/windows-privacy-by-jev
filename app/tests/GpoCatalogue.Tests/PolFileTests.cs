using GpoCatalogue.Core.Pol;

namespace GpoCatalogue.Tests;

public class PolFileTests
{
    public static TheoryData<string> BaselineFiles() =>
        new(Repo.BaselinePolFiles().Select(p => Path.GetRelativePath(Repo.Root, p)));

    [Fact]
    public void Finds_all_nine_baseline_files() => Assert.Equal(9, Repo.BaselinePolFiles().Count());

    [Theory]
    [MemberData(nameof(BaselineFiles))]
    public void Writing_what_was_read_gives_the_same_bytes(string relative)
    {
        var path = Path.Combine(Repo.Root, relative);
        var entries = PolFile.Read(path);

        Assert.NotEmpty(entries);
        Assert.Equal(File.ReadAllBytes(path), PolFile.Serialize(entries));
    }

    [Fact]
    public void Entries_built_in_code_survive_a_round_trip()
    {
        PolEntry[] entries =
        [
            PolEntry.Dword(@"Software\Policies\X", "Flag", 4294967295),
            PolEntry.Qword(@"Software\Policies\X", "Big", ulong.MaxValue),
            PolEntry.Text(@"Software\Policies\X", "Path", @"%SystemRoot%\x", expandable: true),
            PolEntry.MultiText(@"Software\Policies\X", "Lines", ["a", "b c"]),
            PolEntry.Delete(@"Software\Policies\X", "Gone"),
            PolEntry.DeleteValues(@"Software\Policies\X\List"),
        ];

        var read = PolFile.Parse(PolFile.Serialize(entries));

        Assert.Equal(entries.Select(e => e.ToString()), read.Select(e => e.ToString()));
        Assert.Equal(4294967295L, read[0].Data);
        Assert.Equal(ulong.MaxValue, read[1].Data);
        Assert.Equal(RegType.ExpandSz, read[2].Type);
        Assert.Equal(new[] { "a", "b c" }, read[3].Data);
        Assert.Equal((PolMarker.Del, "Gone"), (read[4].Marker, read[4].Name));
        Assert.Equal(PolMarker.DelVals, read[5].Marker);
    }

    [Fact]
    public void Soft_prefix_targets_the_named_value()
    {
        var e = new PolEntry(@"Software\Policies\X", "**soft.Flag", RegType.Dword, [1, 0, 0, 0]);

        Assert.True(e.Soft);
        Assert.Equal(PolMarker.None, e.Marker);
        Assert.Equal("Flag", e.Name);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x50, 0x52, 0x65, 0x67, 2, 0, 0, 0 })]
    [InlineData(new byte[] { 0x50, 0x52, 0x65, 0x67, 1, 0, 0, 0, (byte)'[', 0, (byte)'a', 0 })]
    public void Damaged_files_are_rejected(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => PolFile.Parse(bytes));

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData(" ", "' '")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("it's \"x\"", "'it\\'s \"x\"'")]
    [InlineData(@"a\b", @"'a\\b'")]
    [InlineData("a\tb\n", @"'a\tb\n'")]
    [InlineData("é", "'é'")]
    public void Repr_matches_python(string text, string expected) => Assert.Equal(expected, PyFormat.Repr(text));

    [Fact]
    public void Repr_escapes_what_python_does_not_print()
    {
        // Built from code points so no invisible character sits in this file.
        var text = string.Concat((char)0x01, (char)0xA0, (char)0x200B);
        var expected = "'" + @"\x01" + @"\xa0" + @"\u" + "200b" + "'";

        Assert.Equal(expected, PyFormat.Repr(text));
    }
}
