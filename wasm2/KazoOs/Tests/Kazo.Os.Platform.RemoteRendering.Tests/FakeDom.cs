using System.Text;
using System.Text.Json;

namespace Kazo.Os.Platform.RemoteRendering.Tests;

/// <summary>
/// A DOM that runs the worker's operations the way the page's executor does
/// (<c>RemoteWindowHost.razor.js</c>): numbered nodes, node 0 is the window's content element,
/// "before" 0 appends, inserted markup is kept as one raw node. <see cref="Html"/> serializes the
/// content so a test compares it with what the page's own renderer would have produced.
/// </summary>
internal sealed class FakeDom
{
    private readonly Dictionary<int, FakeNode> _nodes = [];

    public FakeDom() => _nodes[0] = Root;

    /// <summary>Gets the window's content element.</summary>
    public FakeElement Root { get; } = new("content", svg: false);

    /// <summary>Gets the content as HTML, without the content element itself.</summary>
    public string Html => Root.InnerHtml;

    /// <summary>Gets the number of numbered nodes the page holds (node 0 excluded).</summary>
    public int NodeCount => _nodes.Count - 1;

    /// <summary>Gets every operation applied, for assertions on their codes.</summary>
    public List<int> Operations { get; } = [];

    /// <summary>Applies one batch (<c>{k:"b", o:[...]}</c>).</summary>
    public void Apply(JsonElement batch)
    {
        foreach (JsonElement op in batch.GetProperty("o").EnumerateArray())
        {
            Run(op);
        }
    }

    /// <summary>Finds the element whose <c>id</c> attribute is <paramref name="id"/>.</summary>
    public FakeElement Find(string id) =>
        Root.Descendants().OfType<FakeElement>().Single(e => e.Attributes.TryGetValue("id", out string? value) && value == id);

    private void Run(JsonElement op)
    {
        int code = op[0].GetInt32();
        int id = op[1].GetInt32();
        Operations.Add(code);
        switch (code)
        {
            case 1:
                Insert(id, new FakeElement(op[4].GetString()!, op[5].GetInt32() == 1), op[2].GetInt32(), op[3].GetInt32());
                break;
            case 2:
                Insert(id, new FakeText(op[4].GetString()!), op[2].GetInt32(), op[3].GetInt32());
                break;
            case 3:
                Insert(id, new FakeMarkup(op[4].GetString()!), op[2].GetInt32(), op[3].GetInt32());
                break;
            case 4:
                FakeNode removed = _nodes[id];
                removed.Parent!.Children.Remove(removed);
                removed.Parent = null;
                // The page forgets the whole subtree; the worker only names its top node.
                foreach (int key in _nodes.Where(pair => pair.Key != 0 && pair.Value.IsInside(removed)).Select(pair => pair.Key).ToList())
                {
                    _nodes.Remove(key);
                }

                break;
            case 5:
                Element(id).Attributes[op[2].GetString()!] = op[3].GetString()!;
                break;
            case 6:
                Element(id).Attributes.Remove(op[2].GetString()!);
                break;
            case 7:
                ((FakeText)_nodes[id]).Text = op[2].GetString()!;
                break;
            case 8:
                ulong handler = op[3].GetUInt64();
                if (handler == 0)
                {
                    Element(id).Listeners.Remove(op[2].GetString()!);
                }
                else
                {
                    Element(id).Listeners[op[2].GetString()!] = handler;
                }

                break;
            case 9:
                FakeNode moved = _nodes[id];
                moved.Parent!.Children.Remove(moved);
                Place(moved, op[2].GetInt32(), op[3].GetInt32());
                break;
            case 10:
                string flag = $"{op[2].GetString()}:{op[3].GetString()}";
                if (op[4].GetInt32() == 1)
                {
                    Element(id).Flags.Add(flag);
                }
                else
                {
                    Element(id).Flags.Remove(flag);
                }

                break;
            default:
                throw new InvalidOperationException($"unknown operation {code}");
        }
    }

    private FakeElement Element(int id) => (FakeElement)_nodes[id];

    private void Insert(int id, FakeNode node, int parent, int before)
    {
        Assert.False(_nodes.ContainsKey(id), $"node {id} created twice");
        _nodes[id] = node;
        Place(node, parent, before);
    }

    private void Place(FakeNode node, int parent, int before)
    {
        FakeElement container = Element(parent);
        node.Parent = container;
        if (before == 0)
        {
            container.Children.Add(node);
            return;
        }

        int index = container.Children.IndexOf(_nodes[before]);
        Assert.True(index >= 0, $"node {before} is not a child of {parent}");
        container.Children.Insert(index, node);
    }
}

/// <summary>A node of <see cref="FakeDom"/>.</summary>
internal abstract class FakeNode
{
    public FakeElement? Parent { get; set; }

    public bool IsInside(FakeNode ancestor)
    {
        for (FakeNode? node = this; node is not null; node = node.Parent)
        {
            if (node == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    public abstract void Write(StringBuilder html);
}

/// <summary>An element: attributes in order, listeners by event name, prevent/stop flags.</summary>
internal sealed class FakeElement(string tag, bool svg) : FakeNode
{
    public string Tag { get; } = tag;

    public bool IsSvg { get; } = svg;

    public Dictionary<string, string> Attributes { get; } = [];

    public Dictionary<string, ulong> Listeners { get; } = [];

    public HashSet<string> Flags { get; } = [];

    public List<FakeNode> Children { get; } = [];

    public string InnerHtml
    {
        get
        {
            StringBuilder html = new();
            foreach (FakeNode child in Children)
            {
                child.Write(html);
            }

            return html.ToString();
        }
    }

    public string TextContent => string.Concat(Descendants().OfType<FakeText>().Select(t => t.Text));

    public IEnumerable<FakeNode> Descendants()
    {
        foreach (FakeNode child in Children)
        {
            yield return child;
            if (child is FakeElement element)
            {
                foreach (FakeNode descendant in element.Descendants())
                {
                    yield return descendant;
                }
            }
        }
    }

    public override void Write(StringBuilder html)
    {
        html.Append('<').Append(Tag);
        foreach ((string name, string value) in Attributes)
        {
            html.Append(' ').Append(name);
            if (value.Length > 0)
            {
                html.Append("=\"").Append(value).Append('"');
            }
        }

        html.Append('>').Append(InnerHtml).Append("</").Append(Tag).Append('>');
    }
}

/// <summary>A text node.</summary>
internal sealed class FakeText(string text) : FakeNode
{
    public string Text { get; set; } = text;

    public override void Write(StringBuilder html) => html.Append(Text);
}

/// <summary>Inserted markup, kept raw.</summary>
internal sealed class FakeMarkup(string markup) : FakeNode
{
    public override void Write(StringBuilder html) => html.Append(markup);
}
