using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components.RenderTree;

// BL0006: RenderBatch, RenderTreeFrame and RenderTreeEdit are public, but Blazor warns they may change
// in a future release. Reading them is the whole point of the trial (ADR 0129, section 1); ADR 0133
// records the risk: each .NET upgrade re-runs the trial's tests before shipping.
#pragma warning disable BL0006

namespace Kazo.Os.Platform.RemoteRendering.Worker;

/// <summary>
/// Turns Blazor's <see cref="RenderBatch"/> into DOM operations of our own format (ADR 0133, section
/// 2), on the worker's side: the X11 client keeps the window's tree and sends drawing requests, the
/// page only executes them.
/// </summary>
/// <remarks>
/// <para>
/// A render batch is a diff against Blazor's <em>logical</em> tree, where a component is a node with
/// no DOM element of its own and a region is flattened into its parent. The page has no such tree:
/// this class keeps it (the mirror the browser renderer keeps in <c>LogicalElements</c>) and resolves
/// every edit to plain DOM operations on numbered nodes, so the page's module stays a small, dumb
/// executor and the logic stays in C# (AGENTS.md I.2). The walk mirrors the public contract of
/// <see cref="RenderTreeEdit"/> and <see cref="RenderTreeFrame"/>; no framework internals are used.
/// </para>
/// <para>
/// Operations are JSON arrays whose first item is the <see cref="DomOp"/> code; node 0 is the window's
/// content element on the page. A node inserted with <c>before = 0</c> is appended to its parent.
/// </para>
/// </remarks>
internal sealed class DomPatchWriter
{
    private const string PreventDefaultPrefix = "__internal_preventDefault_";
    private const string StopPropagationPrefix = "__internal_stopPropagation_";

    private readonly Container _root = new(null);
    private readonly Dictionary<int, ComponentNode> _components = [];
    private readonly Dictionary<ulong, HandlerInfo> _handlers = [];
    private int _nextNodeId = 1;

    /// <summary>Gets the number of DOM nodes the page holds for this window (diagnostics).</summary>
    public int NodeCount { get; private set; }

    /// <summary>Attaches the root component, whose content is the window's content element.</summary>
    /// <param name="componentId">The id <c>AssignRootComponentId</c> gave it.</param>
    public void AttachRoot(int componentId)
    {
        ComponentNode root = new(componentId, _root);
        _root.Children.Add(root);
        _components[componentId] = root;
    }

    /// <summary>Gets the component that owns the element of an event handler, for <c>EventFieldInfo</c>.</summary>
    /// <param name="handlerId">The handler id the page sent.</param>
    /// <param name="info">The handler's component and event name.</param>
    public bool TryGetHandler(ulong handlerId, out HandlerInfo info) => _handlers.TryGetValue(handlerId, out info);

    /// <summary>Writes the operations of one batch into the current JSON array of <paramref name="writer"/>.</summary>
    /// <param name="batch">The batch the renderer hands to <c>UpdateDisplayAsync</c>.</param>
    /// <param name="writer">The writer, inside an open array.</param>
    public void Write(in RenderBatch batch, Utf8JsonWriter writer)
    {
        Emitter emit = new(writer, this);
        ArrayRange<RenderTreeFrame> frames = batch.ReferenceFrames;
        for (int i = 0; i < batch.UpdatedComponents.Count; i++)
        {
            RenderTreeDiff diff = batch.UpdatedComponents.Array[i];
            if (!_components.TryGetValue(diff.ComponentId, out ComponentNode? component))
            {
                // A component disposed in the same batch, or never placed in the tree.
                continue;
            }

            ApplyEdits(emit, diff.ComponentId, component, diff.Edits, frames.Array);
        }

        for (int i = 0; i < batch.DisposedComponentIDs.Count; i++)
        {
            _components.Remove(batch.DisposedComponentIDs.Array[i]);
        }

        for (int i = 0; i < batch.DisposedEventHandlerIDs.Count; i++)
        {
            _handlers.Remove(batch.DisposedEventHandlerIDs.Array[i]);
        }
    }

    private void ApplyEdits(Emitter emit, int componentId, Container parent, ArrayBuilderSegment<RenderTreeEdit> edits, RenderTreeFrame[] frames)
    {
        int depth = 0;
        int childIndexAtDepth = 0;
        List<(int From, int To)>? permutation = null;
        RenderTreeEdit[] array = edits.Array;
        int end = edits.Offset + edits.Count;
        for (int e = edits.Offset; e < end; e++)
        {
            RenderTreeEdit edit = array[e];
            int index = childIndexAtDepth + edit.SiblingIndex;
            switch (edit.Type)
            {
                case RenderTreeEditType.PrependFrame:
                    InsertFrame(emit, componentId, parent, index, frames, edit.ReferenceFrameIndex);
                    break;
                case RenderTreeEditType.RemoveFrame:
                    RemoveChild(emit, parent, index);
                    break;
                case RenderTreeEditType.SetAttribute:
                    ApplyAttribute(emit, componentId, ElementAt(parent, index), frames[edit.ReferenceFrameIndex]);
                    break;
                case RenderTreeEditType.RemoveAttribute:
                    RemoveAttribute(emit, ElementAt(parent, index), edit.RemovedAttributeName!);
                    break;
                case RenderTreeEditType.UpdateText:
                    emit.SetText(((TextNode)parent.Children[index]).Id, frames[edit.ReferenceFrameIndex].TextContent);
                    break;
                case RenderTreeEditType.UpdateMarkup:
                    RemoveChild(emit, parent, index);
                    InsertMarkup(emit, parent, index, frames[edit.ReferenceFrameIndex].MarkupContent);
                    break;
                case RenderTreeEditType.StepIn:
                    parent = (Container)parent.Children[index];
                    depth++;
                    childIndexAtDepth = 0;
                    break;
                case RenderTreeEditType.StepOut:
                    parent = parent.Parent!;
                    depth--;
                    childIndexAtDepth = 0;
                    break;
                case RenderTreeEditType.PermutationListEntry:
                    (permutation ??= []).Add((index, childIndexAtDepth + edit.MoveToSiblingIndex));
                    break;
                case RenderTreeEditType.PermutationListEnd:
                    Permute(emit, parent, permutation!);
                    permutation = null;
                    break;
            }
        }
    }

    /// <summary>Inserts one frame (and its subtree); returns how many logical children it added.</summary>
    private int InsertFrame(Emitter emit, int componentId, Container parent, int index, RenderTreeFrame[] frames, int frameIndex)
    {
        ref RenderTreeFrame frame = ref frames[frameIndex];
        switch (frame.FrameType)
        {
            case RenderTreeFrameType.Element:
                InsertElement(emit, componentId, parent, index, frames, frameIndex);
                return 1;
            case RenderTreeFrameType.Text:
                TextNode text = new(_nextNodeId++, parent);
                parent.Children.Insert(index, text);
                emit.CreateText(text, frame.TextContent);
                return 1;
            case RenderTreeFrameType.Markup:
                InsertMarkup(emit, parent, index, frame.MarkupContent);
                return 1;
            case RenderTreeFrameType.Component:
                ComponentNode component = new(frame.ComponentId, parent);
                parent.Children.Insert(index, component);
                _components[frame.ComponentId] = component;
                return 1;
            case RenderTreeFrameType.Region:
                return InsertFrameRange(emit, componentId, parent, index, frames, frameIndex + 1, frameIndex + frame.RegionSubtreeLength);
            case RenderTreeFrameType.Attribute:
                throw new InvalidOperationException("Attribute frames must lead their element.");
            default:
                // Element and component reference captures, named events, render modes: nothing in the DOM.
                return 0;
        }
    }

    private int InsertFrameRange(Emitter emit, int componentId, Container parent, int index, RenderTreeFrame[] frames, int start, int endExclusive)
    {
        int first = index;
        for (int i = start; i < endExclusive; i++)
        {
            ref RenderTreeFrame frame = ref frames[i];
            index += InsertFrame(emit, componentId, parent, index, frames, i);
            i += frame.FrameType switch
            {
                RenderTreeFrameType.Element => frame.ElementSubtreeLength - 1,
                RenderTreeFrameType.Component => frame.ComponentSubtreeLength - 1,
                RenderTreeFrameType.Region => frame.RegionSubtreeLength - 1,
                _ => 0
            };
        }

        return index - first;
    }

    private void InsertElement(Emitter emit, int componentId, Container parent, int index, RenderTreeFrame[] frames, int frameIndex)
    {
        ref RenderTreeFrame frame = ref frames[frameIndex];
        string tag = frame.ElementName;
        bool svg = string.Equals(tag, "svg", StringComparison.OrdinalIgnoreCase)
            || (parent is ElementNode { IsSvg: true } owner && !string.Equals(owner.Tag, "foreignObject", StringComparison.Ordinal));
        ElementNode element = new(_nextNodeId++, parent, tag, svg);
        parent.Children.Insert(index, element);
        emit.CreateElement(element);

        int end = frameIndex + frame.ElementSubtreeLength;
        for (int d = frameIndex + 1; d < end; d++)
        {
            ref RenderTreeFrame child = ref frames[d];
            if (child.FrameType == RenderTreeFrameType.Attribute)
            {
                ApplyAttribute(emit, componentId, element, child);
                continue;
            }

            InsertFrameRange(emit, componentId, element, 0, frames, d, end);
            break;
        }
    }

    private void InsertMarkup(Emitter emit, Container parent, int index, string markup)
    {
        MarkupNode node = new(_nextNodeId++, parent);
        parent.Children.Insert(index, node);
        emit.CreateMarkup(node, markup);
    }

    private void ApplyAttribute(Emitter emit, int componentId, ElementNode element, in RenderTreeFrame frame)
    {
        string name = frame.AttributeName;
        if (frame.AttributeEventHandlerId != 0)
        {
            string eventName = name.StartsWith("on", StringComparison.Ordinal) ? name[2..] : name;
            element.Events.Add(eventName);
            _handlers[frame.AttributeEventHandlerId] = new HandlerInfo(componentId, eventName);
            emit.SetListener(element.Id, eventName, frame.AttributeEventHandlerId);
            return;
        }

        if (name.StartsWith(PreventDefaultPrefix, StringComparison.Ordinal))
        {
            emit.SetEventFlag(element.Id, "p", name[(PreventDefaultPrefix.Length + 2)..], IsTrue(frame.AttributeValue));
            return;
        }

        if (name.StartsWith(StopPropagationPrefix, StringComparison.Ordinal))
        {
            emit.SetEventFlag(element.Id, "s", name[(StopPropagationPrefix.Length + 2)..], IsTrue(frame.AttributeValue));
            return;
        }

        if (ToAttributeText(frame.AttributeValue) is { } value)
        {
            emit.SetAttribute(element.Id, name, value);
        }
        else
        {
            emit.RemoveAttribute(element.Id, name);
        }
    }

    private static void RemoveAttribute(Emitter emit, ElementNode element, string name)
    {
        string eventName = name.StartsWith("on", StringComparison.Ordinal) ? name[2..] : name;
        if (element.Events.Remove(eventName))
        {
            emit.SetListener(element.Id, eventName, 0);
            return;
        }

        if (name.StartsWith(PreventDefaultPrefix, StringComparison.Ordinal))
        {
            emit.SetEventFlag(element.Id, "p", name[(PreventDefaultPrefix.Length + 2)..], false);
            return;
        }

        if (name.StartsWith(StopPropagationPrefix, StringComparison.Ordinal))
        {
            emit.SetEventFlag(element.Id, "s", name[(StopPropagationPrefix.Length + 2)..], false);
            return;
        }

        emit.RemoveAttribute(element.Id, name);
    }

    private static bool IsTrue(object? value) => value is not (null or false);

    /// <summary>
    /// An element attribute's text: a string as is, <see langword="true"/> as a present empty
    /// attribute, <see langword="false"/> or <see langword="null"/> as no attribute, anything else in
    /// the invariant culture.
    /// </summary>
    internal static string? ToAttributeText(object? value) => value switch
    {
        null or false => null,
        true => string.Empty,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static ElementNode ElementAt(Container parent, int index) =>
        parent.Children[index] as ElementNode
        ?? throw new InvalidOperationException("An attribute edit does not target an element.");

    private void RemoveChild(Emitter emit, Container parent, int index)
    {
        Node node = parent.Children[index];
        parent.Children.RemoveAt(index);
        foreach (int id in TopDomNodes(node))
        {
            emit.Remove(id);
        }

        Forget(node);
    }

    /// <summary>Drops the components below a removed node from the map (their disposal may come later).</summary>
    private void Forget(Node node)
    {
        if (node is ComponentNode component)
        {
            _components.Remove(component.ComponentId);
        }

        if (node is Container container)
        {
            foreach (Node child in container.Children)
            {
                Forget(child);
            }
        }
    }

    /// <summary>
    /// Applies a keyed move: the logical list first, then the DOM of the moved range from right to
    /// left, each node before its successor, which is already in place.
    /// </summary>
    private void Permute(Emitter emit, Container parent, List<(int From, int To)> moves)
    {
        Node[] before = [.. parent.Children];
        int low = int.MaxValue;
        int high = int.MinValue;
        foreach ((int from, int to) in moves)
        {
            parent.Children[to] = before[from];
            low = Math.Min(low, Math.Min(from, to));
            high = Math.Max(high, Math.Max(from, to));
        }

        for (int i = high; i >= low; i--)
        {
            Node node = parent.Children[i];
            int next = NextDomNode(node);
            int domParent = DomParentId(parent);
            foreach (int id in TopDomNodes(node))
            {
                emit.Move(id, domParent, next);
            }
        }
    }

    /// <summary>The page node a logical child of <paramref name="container"/> is inserted in.</summary>
    private static int DomParentId(Container? container)
    {
        for (Container? current = container; current is not null; current = current.Parent)
        {
            if (current is ElementNode element)
            {
                return element.Id;
            }
        }

        return 0;
    }

    /// <summary>The first page node after <paramref name="node"/> in document order within its DOM parent, or 0.</summary>
    private static int NextDomNode(Node node)
    {
        for (Node current = node; current.Parent is { } parent; current = parent)
        {
            int index = parent.Children.IndexOf(current);
            for (int i = index + 1; i < parent.Children.Count; i++)
            {
                if (FirstDomNode(parent.Children[i]) is int id)
                {
                    return id;
                }
            }

            if (parent is ElementNode)
            {
                return 0;
            }
        }

        return 0;
    }

    private static int? FirstDomNode(Node node)
    {
        if (node is DomNode dom)
        {
            return dom.Id;
        }

        if (node is Container container)
        {
            foreach (Node child in container.Children)
            {
                if (FirstDomNode(child) is int id)
                {
                    return id;
                }
            }
        }

        return null;
    }

    private static IEnumerable<int> TopDomNodes(Node node)
    {
        if (node is DomNode dom)
        {
            yield return dom.Id;
            yield break;
        }

        if (node is Container container)
        {
            foreach (Node child in container.Children)
            {
                foreach (int id in TopDomNodes(child))
                {
                    yield return id;
                }
            }
        }
    }

    /// <summary>The component and event name behind an event handler id.</summary>
    internal readonly record struct HandlerInfo(int ComponentId, string EventName);

    /// <summary>Operation codes of the wire format (ADR 0133, section 2).</summary>
    internal static class DomOp
    {
        /// <summary><c>[1, id, parent, before, tag, svg]</c>: create an element.</summary>
        public const int CreateElement = 1;

        /// <summary><c>[2, id, parent, before, text]</c>: create a text node.</summary>
        public const int CreateText = 2;

        /// <summary><c>[3, id, parent, before, html]</c>: insert trusted markup (<c>MarkupString</c>).</summary>
        public const int CreateMarkup = 3;

        /// <summary><c>[4, id]</c>: remove a node.</summary>
        public const int Remove = 4;

        /// <summary><c>[5, id, name, value]</c>: set an attribute (the property for <c>value</c> and <c>checked</c>).</summary>
        public const int SetAttribute = 5;

        /// <summary><c>[6, id, name]</c>: remove an attribute.</summary>
        public const int RemoveAttribute = 6;

        /// <summary><c>[7, id, text]</c>: change a text node.</summary>
        public const int SetText = 7;

        /// <summary><c>[8, id, event, handler]</c>: listen to an event (handler 0 stops listening).</summary>
        public const int SetListener = 8;

        /// <summary><c>[9, id, parent, before]</c>: move a node.</summary>
        public const int Move = 9;

        /// <summary><c>[10, id, flag, event, on]</c>: <c>p</c> preventDefault or <c>s</c> stopPropagation.</summary>
        public const int SetEventFlag = 10;
    }

    /// <summary>Writes the operations, resolving where each new node goes.</summary>
    private readonly struct Emitter(Utf8JsonWriter writer, DomPatchWriter owner)
    {
        public void CreateElement(ElementNode element)
        {
            Start(DomOp.CreateElement, element.Id);
            writer.WriteNumberValue(DomParentId(element.Parent));
            writer.WriteNumberValue(NextDomNode(element));
            writer.WriteStringValue(element.Tag);
            writer.WriteNumberValue(element.IsSvg ? 1 : 0);
            writer.WriteEndArray();
            owner.NodeCount++;
        }

        public void CreateText(TextNode text, string content)
        {
            Start(DomOp.CreateText, text.Id);
            writer.WriteNumberValue(DomParentId(text.Parent));
            writer.WriteNumberValue(NextDomNode(text));
            writer.WriteStringValue(content);
            writer.WriteEndArray();
            owner.NodeCount++;
        }

        public void CreateMarkup(MarkupNode markup, string html)
        {
            Start(DomOp.CreateMarkup, markup.Id);
            writer.WriteNumberValue(DomParentId(markup.Parent));
            writer.WriteNumberValue(NextDomNode(markup));
            writer.WriteStringValue(html);
            writer.WriteEndArray();
            owner.NodeCount++;
        }

        public void Remove(int id)
        {
            Start(DomOp.Remove, id);
            writer.WriteEndArray();
            owner.NodeCount--;
        }

        public void SetAttribute(int id, string name, string value)
        {
            Start(DomOp.SetAttribute, id);
            writer.WriteStringValue(name);
            writer.WriteStringValue(value);
            writer.WriteEndArray();
        }

        public void RemoveAttribute(int id, string name)
        {
            Start(DomOp.RemoveAttribute, id);
            writer.WriteStringValue(name);
            writer.WriteEndArray();
        }

        public void SetText(int id, string text)
        {
            Start(DomOp.SetText, id);
            writer.WriteStringValue(text);
            writer.WriteEndArray();
        }

        public void SetListener(int id, string eventName, ulong handlerId)
        {
            Start(DomOp.SetListener, id);
            writer.WriteStringValue(eventName);
            writer.WriteNumberValue(handlerId);
            writer.WriteEndArray();
        }

        public void Move(int id, int parent, int before)
        {
            Start(DomOp.Move, id);
            writer.WriteNumberValue(parent);
            writer.WriteNumberValue(before);
            writer.WriteEndArray();
        }

        public void SetEventFlag(int id, string flag, string eventName, bool on)
        {
            Start(DomOp.SetEventFlag, id);
            writer.WriteStringValue(flag);
            writer.WriteStringValue(eventName);
            writer.WriteNumberValue(on ? 1 : 0);
            writer.WriteEndArray();
        }

        private void Start(int op, int id)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(op);
            writer.WriteNumberValue(id);
        }
    }

    /// <summary>A node of the logical tree.</summary>
    private abstract class Node(Container? parent)
    {
        public Container? Parent { get; } = parent;
    }

    /// <summary>A node with logical children: the root, a component, an element.</summary>
    private class Container(Container? parent) : Node(parent)
    {
        public List<Node> Children { get; } = [];
    }

    /// <summary>A node that exists on the page.</summary>
    private interface DomNode
    {
        int Id { get; }
    }

    private sealed class ComponentNode(int componentId, Container parent) : Container(parent)
    {
        public int ComponentId { get; } = componentId;
    }

    private sealed class ElementNode(int id, Container parent, string tag, bool isSvg) : Container(parent), DomNode
    {
        public int Id { get; } = id;

        public string Tag { get; } = tag;

        public bool IsSvg { get; } = isSvg;

        /// <summary>The events this element listens to, to tell a handler from an attribute on removal.</summary>
        public HashSet<string> Events { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TextNode(int id, Container parent) : Node(parent), DomNode
    {
        public int Id { get; } = id;
    }

    private sealed class MarkupNode(int id, Container parent) : Node(parent), DomNode
    {
        public int Id { get; } = id;
    }
}
