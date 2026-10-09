using EveContracts.Core.Services;
using Xunit;

namespace EveContracts.Tests;

public class BrowseSearchTests
{
    // Fields as the browse snapshot builds them: title, system, station, destination, items.
    private static readonly string[] Fitted =
    [
        "pve raven, ready to go", "jita", "jita iv - moon 4 - caldari navy assembly plant", "",
        "raven", "ballistic control system ii", "large shield extender ii", "cruise missile launcher ii",
    ];

    [Fact]
    public void EmptySearch_ParsesToNoTerms_AndMatchesAll()
    {
        Assert.Empty(BrowseSearch.Parse(""));
        Assert.Empty(BrowseSearch.Parse(" , ,"));
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse(null), Fitted));
    }

    [Fact]
    public void Parse_SplitsOnCommasThenWords_Lowercased()
    {
        var t = BrowseSearch.Parse(" Raven ,  Ballistic  Control ");
        Assert.Equal(2, t.Length);
        Assert.Equal(["raven"], t[0]);
        Assert.Equal(["ballistic", "control"], t[1]);
    }

    [Fact]
    public void EveryCommaTermMustMatchSomeItem()
    {
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("raven, ballistic control"), Fitted));
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("shield extender, cruise launcher, ballistic"), Fitted));
        Assert.False(BrowseSearch.Matches(BrowseSearch.Parse("raven, damage control"), Fitted));
    }

    [Fact]
    public void WordsOfOneTerm_MustLandInTheSameItem()
    {
        // "ballistic" and "shield" both exist, but in different items.
        Assert.False(BrowseSearch.Matches(BrowseSearch.Parse("ballistic shield"), Fitted));
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("ballistic, shield"), Fitted));
    }

    [Fact]
    public void TermsCanMatchTitleOrLocation()
    {
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("jita, raven"), Fitted));
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("ready to go"), Fitted));
        Assert.True(BrowseSearch.Matches(BrowseSearch.Parse("caldari navy"), Fitted));
    }

    [Fact]
    public void AnyTermMatches_IdentifiesItemsToHighlight()
    {
        var t = BrowseSearch.Parse("raven, ballistic");
        Assert.True(BrowseSearch.AnyTermMatches(t, "ballistic control system ii"));
        Assert.True(BrowseSearch.AnyTermMatches(t, "raven"));
        Assert.False(BrowseSearch.AnyTermMatches(t, "large shield extender ii"));
    }
}
