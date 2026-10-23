using System.Text.RegularExpressions;
using Solo.DesignDocs.Contracts;
namespace Solo.DesignDocs.Tests;
public sealed class NoteAddressTests
{
    [Fact]
    public void Compact_addresses_preserve_all_guid_bits_and_have_a_url_safe_alphabet()
    {
        var seen=new HashSet<string>();
        for(var i=0;i<1000;i++){var id=Guid.CreateVersion7();var key=NoteAddress.Encode(id);Assert.Matches("^[A-Za-z0-9_-]{22}$",key);Assert.True(seen.Add(key));Assert.True(NoteAddress.TryDecode(key,out var restored));Assert.Equal(id,restored);}
        Assert.Equal("ABEiM0RVZneImaq7zN3u_w",NoteAddress.Encode(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")));
    }
    [Fact]
    public void Legacy_guid_links_and_compact_links_resolve_to_the_same_id()
    {
        var id=Guid.NewGuid();foreach(var key in new[]{id.ToString("N"),id.ToString(),NoteAddress.Encode(id)}){Assert.True(NoteAddress.TryReadUrl(NoteAddress.Prefix+key+"#heading",out var decoded));Assert.Equal(id,decoded);}
    }
    [Theory][InlineData("")][InlineData("../../outside")][InlineData("AAAAAAAAAAAAAAAAAAAAAB")][InlineData("AAAAAAAAAAAAAAAAAAAAA=")][InlineData("AAAAAAAAAAAAAAAAAAAAAA/extra")]
    public void Malformed_or_noncanonical_keys_are_rejected(string key)=>Assert.False(NoteAddress.TryDecode(key,out _));
    [Fact]
    public void Linked_doc_extraction_does_not_truncate_legacy_guids_or_invalid_tokens()
    {
        var id=Guid.NewGuid();var old=NoteAddress.Prefix+id.ToString("N")+"#old";var compact=NoteAddress.Url(id)+"#new";
        var urls=Regex.Matches(old+" "+compact+" /docs/note/"+new string('A',23),NoteAddress.UrlPattern).Select(m=>m.Value).ToArray();
        Assert.Equal(new[]{old,compact},urls);
    }
}
