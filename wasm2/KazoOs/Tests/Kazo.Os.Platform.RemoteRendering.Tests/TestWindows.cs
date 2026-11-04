using Kazo.Os.AppSdk;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Kazo.Os.Platform.RemoteRendering.Tests;

/// <summary>A window component: the root hands it the process's context, as to any app window.</summary>
public abstract class TestWindow : ComponentBase
{
    [Parameter]
    public IAppExecutionContext? AppContext { get; set; }
}

/// <summary>Text, a conditional element, a bool attribute, markup and a click handler.</summary>
public sealed class CounterWindow : TestWindow
{
    private int _count;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "counter");
        builder.OpenElement(2, "button");
        builder.AddAttribute(3, "id", "inc");
        builder.AddAttribute(4, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => _count++));
        builder.AddAttribute(5, "disabled", _count >= 3);
        builder.AddContent(6, "Add");
        builder.CloseElement();
        builder.OpenElement(7, "span");
        builder.AddAttribute(8, "id", "count");
        builder.AddContent(9, $"Count: {_count}");
        builder.CloseElement();
        if (_count % 2 == 1)
        {
            builder.OpenElement(10, "em");
            builder.AddAttribute(11, "id", "odd");
            builder.AddContent(12, "odd");
            builder.CloseElement();
        }

        builder.AddMarkupContent(13, "<b>fixed</b>");
        builder.OpenElement(14, "p");
        builder.AddAttribute(15, "id", "tail");
        builder.AddContent(16, "end");
        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>Child components, one of them conditional and placed before another component.</summary>
public sealed class NestedWindow : TestWindow
{
    private bool _first = true;
    private int _extra;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "ul");
        builder.AddAttribute(1, "id", "list");
        if (_first)
        {
            builder.OpenComponent<ItemComponent>(2);
            builder.AddComponentParameter(3, nameof(ItemComponent.Label), "first");
            builder.CloseComponent();
        }

        builder.OpenComponent<PairComponent>(4);
        builder.CloseComponent();
        for (int i = 0; i < _extra; i++)
        {
            builder.OpenComponent<ItemComponent>(5);
            builder.AddComponentParameter(6, nameof(ItemComponent.Label), $"extra{i}");
            builder.CloseComponent();
        }

        builder.CloseElement();
        builder.OpenElement(7, "button");
        builder.AddAttribute(8, "id", "toggle");
        builder.AddAttribute(9, "onclick", EventCallback.Factory.Create(this, () => _first = !_first));
        builder.CloseElement();
        builder.OpenElement(10, "button");
        builder.AddAttribute(11, "id", "more");
        builder.AddAttribute(12, "onclick", EventCallback.Factory.Create(this, () => _extra++));
        builder.CloseElement();
    }
}

/// <summary>One list item.</summary>
public sealed class ItemComponent : ComponentBase
{
    [Parameter]
    public string Label { get; set; } = "";

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "li");
        builder.AddContent(1, Label);
        builder.CloseElement();
    }
}

/// <summary>A component with two top-level nodes, inside a region.</summary>
public sealed class PairComponent : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenRegion(0);
        builder.OpenElement(0, "li");
        builder.AddContent(1, "a");
        builder.CloseElement();
        builder.CloseRegion();
        builder.OpenElement(1, "li");
        builder.AddContent(2, "b");
        builder.CloseElement();
    }
}

/// <summary>A keyed list reordered in place.</summary>
public sealed class KeyedWindow : TestWindow
{
    private List<string> _keys = ["a", "b", "c", "d"];

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "ol");
        foreach (string key in _keys)
        {
            builder.OpenElement(1, "li");
            builder.SetKey(key);
            builder.AddAttribute(2, "id", key);
            builder.AddContent(3, key);
            builder.CloseElement();
        }

        builder.CloseElement();
        builder.OpenElement(4, "button");
        builder.AddAttribute(5, "id", "reverse");
        builder.AddAttribute(6, "onclick", EventCallback.Factory.Create(this, () => _keys.Reverse()));
        builder.CloseElement();
        builder.OpenElement(7, "button");
        builder.AddAttribute(8, "id", "rotate");
        builder.AddAttribute(9, "onclick", EventCallback.Factory.Create(this, () => _keys = [.. _keys.Skip(1), "e"]));
        builder.CloseElement();
    }
}

/// <summary><c>@bind</c> of a text field, echoed in a paragraph, and a checkbox.</summary>
public sealed class BindWindow : TestWindow
{
    public string Name { get; private set; } = "";

    public bool Checked { get; private set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "input");
        builder.AddAttribute(1, "id", "name");
        builder.AddAttribute(2, "value", BindConverter.FormatValue(Name));
        builder.AddAttribute(3, "oninput", EventCallback.Factory.CreateBinder(this, value => Name = value ?? "", Name));
        builder.SetUpdatesAttributeName("value");
        builder.CloseElement();
        builder.OpenElement(4, "p");
        builder.AddAttribute(5, "id", "echo");
        builder.AddContent(6, Name);
        builder.CloseElement();
        builder.OpenElement(7, "input");
        builder.AddAttribute(8, "id", "check");
        builder.AddAttribute(9, "type", "checkbox");
        builder.AddAttribute(10, "checked", BindConverter.FormatValue(Checked));
        builder.AddAttribute(11, "onchange", EventCallback.Factory.CreateBinder(this, value => Checked = value, Checked));
        builder.SetUpdatesAttributeName("checked");
        builder.CloseElement();
        builder.OpenElement(12, "p");
        builder.AddAttribute(13, "id", "state");
        builder.AddContent(14, Checked ? "on" : "off");
        builder.CloseElement();
    }
}

/// <summary>preventDefault/stopPropagation, turned off by a click, and an SVG drawing.</summary>
public sealed class FlagsWindow : TestWindow
{
    private bool _prevent = true;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "a");
        builder.AddAttribute(1, "id", "link");
        builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => _prevent = false));
        builder.AddEventPreventDefaultAttribute(3, "onclick", _prevent);
        builder.AddEventStopPropagationAttribute(4, "onclick", true);
        builder.CloseElement();
        builder.OpenElement(5, "svg");
        builder.AddAttribute(6, "viewBox", "0 0 10 10");
        builder.OpenElement(7, "circle");
        builder.AddAttribute(8, "id", "dot");
        builder.AddAttribute(9, "r", "4");
        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>A handler that is a new closure on each render, so each render disposes the previous handler id.</summary>
public sealed class StaleWindow : TestWindow
{
    private int _count;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        int current = _count;
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "id", "go");
        builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => _count = current + 1));
        builder.AddContent(3, _count);
        builder.CloseElement();
    }
}

/// <summary>A handler that throws: the window's render fails as on the page.</summary>
public sealed class FailingWindow : TestWindow
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "button");
        builder.AddAttribute(1, "id", "boom");
        builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => throw new InvalidOperationException("boom")));
        builder.CloseElement();
    }
}
