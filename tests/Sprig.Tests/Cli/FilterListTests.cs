using Sprig.Cli;

namespace Sprig.Tests.Cli;

public class FilterListTests
{
    sealed record Item(string Name);

    static FilterList<Item> Of(params string[] names) => new(names.Select(n => new Item(n)), i => i.Name);

    static void Type(FilterList<Item> list, string text)
    {
        foreach (var c in text) list.Type(c);
    }

    [Fact]
    public void Typing_hides_what_doesnt_match()
    {
        var list = Of("shop-web--1 · feature/auth", "shop-web--2 · feature/cart", "shop-api--1 · feature/auth");
        Type(list, "auth");

        Assert.Equal(["shop-web--1 · feature/auth", "shop-api--1 · feature/auth"], list.Shown.Select(i => i.Name));
    }

    [Fact]
    public void Every_word_has_to_match_in_any_order()
    {
        var list = Of("shop-web--1 · feature/auth", "shop-api--1 · feature/auth");
        Type(list, "AUTH api");

        Assert.Equal(["shop-api--1 · feature/auth"], list.Shown.Select(i => i.Name));
    }

    [Fact]
    public void A_new_search_puts_the_cursor_on_the_first_match()
    {
        var list = Of("alpha", "beta", "gamma");
        list.Move(2);
        Type(list, "a");

        Assert.Equal(0, list.Cursor);
        Assert.Equal("alpha", list.Picked!.Name);
    }

    [Fact]
    public void None_is_the_last_row_and_no_search_hides_it()
    {
        var list = Of("alpha", "beta");
        Type(list, "zzz");

        Assert.Empty(list.Shown);
        Assert.True(list.OnNone);
        Assert.Null(list.Picked);
    }

    [Fact]
    public void Backspace_and_clear_bring_things_back()
    {
        var list = Of("alpha", "beta");
        Type(list, "bx");
        Assert.Empty(list.Shown);

        list.Backspace();
        Assert.Equal(["beta"], list.Shown.Select(i => i.Name));

        list.Clear();
        Assert.Equal(2, list.Shown.Count);
        Assert.Equal("", list.Query);
    }

    [Fact]
    public void The_cursor_stays_on_the_rows()
    {
        var list = Of("alpha", "beta");
        list.Move(-5);
        Assert.Equal(0, list.Cursor);

        list.Move(10);
        Assert.Equal(2, list.Cursor);
        Assert.True(list.OnNone);
    }

    [Fact]
    public void A_page_keeps_the_cursor_in_view()
    {
        var list = Of([.. Enumerable.Range(0, 40).Select(n => $"item {n}")]);

        Assert.Equal(0, list.PageStart(15));
        list.Move(20);
        Assert.Equal(13, list.PageStart(15));
        list.ToLast();
        Assert.Equal(41 - 15, list.PageStart(15));
    }
}
