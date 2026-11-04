namespace Kazo.Os.Platform.RemoteRendering.Tests;

/// <summary>
/// ADR 0133: the worker's renderer turns Blazor's batches into DOM operations that, applied by the
/// page's executor, give the HTML the page's own renderer would have produced.
/// </summary>
public sealed class WorkerWindowRendererTests
{
    [Fact]
    public async Task The_first_batch_builds_the_window_content()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();

        Assert.Equal(
            """<div class="counter"><button id="inc">Add</button><span id="count">Count: 0</span><b>fixed</b><p id="tail">end</p></div>""",
            window.Dom.Html);
        Assert.Equal(1, window.Batches);
        Assert.True(window.Dom.Find("inc").Listeners.ContainsKey("click"));
    }

    [Fact]
    public async Task A_click_updates_text_and_inserts_a_conditional_element_in_place()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();

        await window.FireAsync("inc");

        Assert.Equal(
            """<div class="counter"><button id="inc">Add</button><span id="count">Count: 1</span><em id="odd">odd</em><b>fixed</b><p id="tail">end</p></div>""",
            window.Dom.Html);

        await window.FireAsync("inc");
        Assert.Equal("Count: 2", window.Dom.Find("count").TextContent);
        Assert.DoesNotContain("odd", window.Dom.Html);
    }

    [Fact]
    public async Task A_bool_attribute_is_present_only_when_true()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();

        for (int i = 0; i < 3; i++)
        {
            await window.FireAsync("inc");
        }

        Assert.Equal(string.Empty, window.Dom.Find("inc").Attributes["disabled"]);
        Assert.StartsWith("""<div class="counter"><button id="inc" disabled>Add</button>""", window.Dom.Html);
    }

    [Fact]
    public async Task Components_and_regions_are_flattened_into_their_parent_element()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<NestedWindow>();
        Assert.Equal(
            """<ul id="list"><li>first</li><li>a</li><li>b</li></ul><button id="toggle"></button><button id="more"></button>""",
            window.Dom.Html);

        await window.FireAsync("toggle");
        Assert.StartsWith("""<ul id="list"><li>a</li><li>b</li></ul>""", window.Dom.Html);

        // Re-inserted before the next component: its first DOM node is inside a region.
        await window.FireAsync("toggle");
        Assert.StartsWith("""<ul id="list"><li>first</li><li>a</li><li>b</li></ul>""", window.Dom.Html);

        await window.FireAsync("more");
        await window.FireAsync("more");
        Assert.StartsWith("""<ul id="list"><li>first</li><li>a</li><li>b</li><li>extra0</li><li>extra1</li></ul>""", window.Dom.Html);
    }

    [Fact]
    public async Task A_keyed_list_moves_its_elements_instead_of_recreating_them()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<KeyedWindow>();
        FakeElement a = window.Dom.Find("a");
        FakeElement d = window.Dom.Find("d");

        window.Dom.Operations.Clear();
        await window.FireAsync("reverse");

        Assert.StartsWith("""<ol><li id="d">d</li><li id="c">c</li><li id="b">b</li><li id="a">a</li></ol>""", window.Dom.Html);
        Assert.Same(a, window.Dom.Find("a"));
        Assert.Same(d, window.Dom.Find("d"));
        Assert.Contains(9, window.Dom.Operations);
        Assert.DoesNotContain(1, window.Dom.Operations);

        await window.FireAsync("rotate");
        Assert.StartsWith("""<ol><li id="c">c</li><li id="b">b</li><li id="a">a</li><li id="e">e</li></ol>""", window.Dom.Html);
        Assert.Same(a, window.Dom.Find("a"));
    }

    [Fact]
    public async Task Bind_takes_the_typed_value_without_rewriting_the_field()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<BindWindow>();
        Assert.Equal(string.Empty, window.Dom.Find("name").Attributes["value"]);

        await window.FireAsync("name", "input", """{"type":"input","value":"abc"}""");

        Assert.Equal("abc", window.Dom.Find("echo").TextContent);
        // The field already shows what the user typed: Blazor's EventFieldInfo keeps the value out of the diff.
        Assert.Equal(string.Empty, window.Dom.Find("name").Attributes["value"]);
    }

    [Fact]
    public async Task A_checkbox_change_carries_its_checked_state()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<BindWindow>();
        Assert.False(window.Dom.Find("check").Attributes.ContainsKey("checked"));

        await window.FireAsync("check", "change", """{"type":"change","value":true}""");

        Assert.Equal("on", window.Dom.Find("state").TextContent);
        // The box is already ticked on the page: as for the text, the diff leaves "checked" alone.
        Assert.False(window.Dom.Find("check").Attributes.ContainsKey("checked"));
    }

    [Fact]
    public async Task Event_flags_and_svg_elements_reach_the_page()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<FlagsWindow>();
        FakeElement link = window.Dom.Find("link");
        Assert.Contains("p:click", link.Flags);
        Assert.Contains("s:click", link.Flags);
        Assert.True(window.Dom.Find("dot").IsSvg);
        Assert.False(link.IsSvg);

        await window.FireAsync("link");

        Assert.DoesNotContain("p:click", link.Flags);
        Assert.Contains("s:click", link.Flags);
        Assert.DoesNotContain("__internal", window.Dom.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_event_for_a_disposed_handler_is_logged_and_ignored()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<StaleWindow>();
        ulong first = window.Dom.Find("go").Listeners["click"];

        await window.FireAsync("go");
        ulong second = window.Dom.Find("go").Listeners["click"];
        Assert.NotEqual(first, second);

        // A click the page sent before it applied the re-render.
        await window.FireAsync(first);

        Assert.Equal("1", window.Dom.Find("go").TextContent);
        Assert.Contains(window.Logs, line => line.Contains("disposed handler", StringComparison.Ordinal));
        Assert.False(window.Renderer.Ended.IsCompleted);
    }

    [Fact]
    public async Task A_failing_handler_ends_the_window_with_its_exception()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<FailingWindow>();

        await window.FireAsync("boom");

        Exception? error = await window.Renderer.Ended.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains(window.Logs, line => line.Contains("window render failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closing_ends_the_window_without_an_error()
    {
        await using WindowHarness window = await WindowHarness.MountAsync<CounterWindow>();

        window.Renderer.Close();

        Assert.Null(await window.Renderer.Ended.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
