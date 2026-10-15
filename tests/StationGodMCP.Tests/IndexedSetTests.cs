#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The world index's bucket (IndexedSet): a set by reference whose members are read by place.</summary>
public sealed class IndexedSetTests
{
    [Fact]
    public void MembersAreAddedOnceAndRemovedByMovingTheLastIntoTheGap()
    {
        IndexedSet<object> set = new IndexedSet<object>(ReferenceEqualityComparer.Instance);
        object a = new object(), b = new object(), c = new object();

        Assert.True(set.Add(a));
        Assert.True(set.Add(b));
        Assert.True(set.Add(c));
        Assert.False(set.Add(b));
        Assert.True(set.Remove(a));
        Assert.False(set.Remove(a));

        Assert.Equal(2, set.Count);
        Assert.Same(c, set[0]);
        Assert.Same(b, set[1]);
        Assert.True(set.Contains(b));
        Assert.False(set.Contains(a));
        Assert.True(set.Remove(b));
        Assert.True(set.Remove(c));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void CopyToAppendsEveryMemberInPlaceOrder()
    {
        IndexedSet<object> set = new IndexedSet<object>(ReferenceEqualityComparer.Instance);
        List<object> added = new List<object>();
        for (int index = 0; index < 100; index++)
        {
            added.Add(new object());
            set.Add(added[index]);
        }

        for (int index = 0; index < 100; index += 3)
        {
            set.Remove(added[index]);
        }

        List<object> copied = new List<object>();
        set.CopyTo(copied);

        Assert.Equal(set.Count, copied.Count);
        for (int place = 0; place < copied.Count; place++)
        {
            Assert.Same(set[place], copied[place]);
        }
    }
}
