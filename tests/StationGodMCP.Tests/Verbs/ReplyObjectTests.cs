#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

public class ReplyObjectTests
{
    private sealed class SampleView
    {
        internal SampleView(string name, int? count, double ratio)
        {
            Name = name;
            Count = count;
            Ratio = ratio;
        }

        public string Name { get; }

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? Count { get; }

        public double Ratio { get; }
    }

    private static string Text(object reply) => ApiJson.WriteShared(reply);

    [Fact]
    public void KeysAreWrittenInTheOrderAddedAndAKeyAddedAgainKeepsItsPlace()
    {
        ReplyObject reply = new ReplyObject().Add("a", 1).Add("b", "two").Add("a", 3);

        Assert.Equal("""{"a":3,"b":"two"}""", Text(reply));
    }

    [Fact]
    public void ASpreadWritesTheViewsKeysAsItsOwnObjectWouldButThoseExcepted()
    {
        ReplyObject reply = new ReplyObject().Add("first", true).Spread(new SampleView("x", null, 0.5), "ratio")
            .Add("last", new SampleView("y", 2, 1.0));

        Assert.Equal("""{"first":true,"name":"x","last":{"name":"y","count":2,"ratio":1.0}}""", Text(reply));
    }

    [Fact]
    public void SpreadsNestAndTheirExceptsAdd()
    {
        ReplyObject inner = new ReplyObject().Add("index", 9).Add("value", 1).Spread(new JObject { ["ok"] = false, ["more"] = 2 });
        ReplyObject reply = new ReplyObject().Spread(inner, "index", "ok");

        Assert.Equal("""{"value":1,"more":2}""", Text(reply));
    }

    [Fact]
    public void PairsGiveTheWrittenKeysAndValuesWithoutWriting()
    {
        ReplyObject reply = new ReplyObject().Add("list", new List<int> { 1 }).Spread(new SampleView("x", null, 0.5));

        List<string> keys = new List<string>();
        foreach (KeyValuePair<string, object?> key in ViewKeys.Pairs(ApiJson.Serializer, reply, null))
        {
            keys.Add(key.Key + (ViewKeys.IsList(key.Value) ? "[]" : ""));
        }

        Assert.Equal(new[] { "list[]", "name", "ratio" }, keys);
    }

    [Fact]
    public void ListsAreTheCollectionsThatAreNoDictionaryOrText()
    {
        Assert.True(ViewKeys.IsList(new[] { 1 }));
        Assert.True(ViewKeys.IsList(new JArray()));
        Assert.False(ViewKeys.IsList("text"));
        Assert.False(ViewKeys.IsList(new Dictionary<string, int>()));
        Assert.False(ViewKeys.IsList(new JObject()));
        Assert.False(ViewKeys.IsList(null));
    }

    [Fact]
    public void TheCountIsTheTextsUtf8Length()
    {
        ReplyObject reply = new ReplyObject().Add("name", "Ä€\U0001F600x");

        string text = Text(reply);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), ReplyLength.Utf8Bytes(reply, ApiJson.Serializer));
        Assert.Equal(text.Length, ReplyLength.Characters(reply, ApiJson.Serializer));
    }

    [Fact]
    public void FindLooksIntoSpreadReplyObjectsAsAWalkPagesCursorIdSitsThere()
    {
        ReplyObject keys = new ReplyObject().Add("total_outer", 3).Add("cursor_id", "k7");
        ReplyObject page = new ReplyObject().Add("frames", new List<object>()).Spread(keys)
            .Spread(new SampleView("a", 1, 0.5));

        Assert.Equal("k7", page.Find("cursor_id"));
        Assert.Null(page["cursor_id"]);
        Assert.Null(page.Find("name"));
        Assert.Null(page.Find("missing"));
    }
}
