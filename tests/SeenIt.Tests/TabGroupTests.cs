using SeenIt.Contracts;

namespace SeenIt.Tests;

public class TabGroupTests
{
    [Fact]
    public void Every_page_but_home_is_in_exactly_one_category()
    {
        var grouped = UiPrefs.TabGroups.SelectMany(g => g.Keys).ToList();
        Assert.Equal(grouped.Count, grouped.Distinct().Count());
        Assert.Equal(UiPrefs.Tabs.Select(t => t.Key).Where(k => k != "home").Order(), grouped.Order());
    }
}
