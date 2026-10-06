using System;
using System.Collections.Generic;
using System.Globalization;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>
/// Debugger views for collections. Common BCL collections are read straight from their
/// private fields (no code runs in the debuggee); any other BCL enumerable is shown through
/// a Results View that enumerates it with func-eval.
/// </summary>
internal static class CollectionView
{
    private const int MaxItems = 5000;

    /// <summary>Element count for collections with a field-based view; null for other types.</summary>
    public static int? TryGetCount(ValueInfo value, TypeHandle handle) => handle.Name switch
    {
        "System.Collections.Generic.List" => ReadInt(value, handle, "_size"),
        "System.Collections.Generic.Dictionary" => DictionaryCount(value, handle),
        "System.Collections.Generic.HashSet" => DictionaryCount(value, handle),
        "System.Collections.Generic.Queue" or "System.Collections.Generic.Stack" => ReadInt(value, handle, "_size"),
        "System.Collections.Generic.LinkedList" => ReadInt(value, handle, "count"),
        _ => null,
    };

    public static IVariableContainer? TryCreate(ValueInspector inspector, ValueInfo value, TypeHandle handle, string? evaluateName, ICorDebugThread? thread, IValueSource source)
    {
        switch (handle.Name)
        {
            case "System.Collections.Generic.List" or "System.Collections.Generic.Stack" or "System.Collections.Generic.Queue"
                or "System.Collections.Generic.Dictionary" or "System.Collections.Generic.HashSet" or "System.Collections.Generic.LinkedList"
                or "System.Collections.Immutable.ImmutableArray":
                var count = TryGetCount(value, handle) ?? ImmutableArrayLength(value, handle) ?? 0;
                return new LazyContainer(() => Items(inspector, source, handle.Name, evaluateName, thread), count);
            default:
                // Other framework collections (ConcurrentDictionary, ImmutableList, SortedSet, ...):
                // enumerate them, with the raw fields one level down.
                if (thread is not null && IsFrameworkType(handle.Name) && inspector.IsEnumerable(value))
                {
                    return new LazyContainer(() => ResultsThenRaw(inspector, source, evaluateName, thread), 0);
                }

                return null;
        }
    }

    private static bool IsFrameworkType(string name) =>
        name.StartsWith("System.Collections.", StringComparison.Ordinal) || name.StartsWith("System.Linq.", StringComparison.Ordinal);

    private static IEnumerable<NamedValue> ResultsThenRaw(ValueInspector inspector, IValueSource source, string? evaluateName, ICorDebugThread thread)
    {
        foreach (var item in new ResultsViewContainer(inspector, source, thread).GetChildren(0, 0))
        {
            yield return item;
        }

        yield return RawView(inspector, source, evaluateName, thread);
    }

    private static NamedValue RawView(ValueInspector inspector, IValueSource source, string? evaluateName, ICorDebugThread? thread) =>
        new("Raw View")
        {
            Group = new ObjectContainer(inspector, source, evaluateName, thread),
            Text = string.Empty,
            Kind = VariableKind.Virtual,
        };

    private static List<NamedValue> Items(ValueInspector inspector, IValueSource source, string typeName, string? evaluateName, ICorDebugThread? thread)
    {
        if (source.Resolve() is not { } root)
        {
            return [];
        }

        // Re-analyzed on every expansion: the value captured at creation may be stale.
        var value = ValueInspector.Analyze(root);
        if (inspector.Resolve(value) is not { } handle)
        {
            return [];
        }

        var items = typeName switch
        {
            "System.Collections.Generic.List" => ArrayItems(value, handle, "_items", "_size", evaluateName),
            "System.Collections.Generic.Stack" => StackItems(value, handle),
            "System.Collections.Generic.Queue" => QueueItems(value, handle),
            "System.Collections.Generic.Dictionary" => DictionaryItems(inspector, value, handle, evaluateName),
            "System.Collections.Generic.LinkedList" => LinkedListItems(inspector, value, handle),
            "System.Collections.Immutable.ImmutableArray" => ImmutableArrayItems(value, handle, evaluateName),
            _ => HashSetItems(inspector, value, handle),
        } ?? [];

        items.Add(RawView(inspector, source, evaluateName, thread));
        return items;
    }

    private static List<NamedValue>? ArrayItems(ValueInfo value, TypeHandle handle, string itemsField, string sizeField, string? evaluateName)
    {
        if (ReadInt(value, handle, sizeField) is not { } size || BackingArray(value, handle, itemsField) is not { } items)
        {
            return null;
        }

        var result = new List<NamedValue>();
        for (var i = 0; i < size && i < MaxItems; i++)
        {
            if (items.GetElementAtPosition((uint)i, out var element) >= 0)
            {
                result.Add(Item(i, element, evaluateName));
            }
        }

        return result;
    }

    /// <summary>Top of the stack first, like the debugger proxy.</summary>
    private static List<NamedValue>? StackItems(ValueInfo value, TypeHandle handle)
    {
        if (ReadInt(value, handle, "_size") is not { } size || BackingArray(value, handle, "_array") is not { } items)
        {
            return null;
        }

        var result = new List<NamedValue>();
        for (var i = 0; i < size && i < MaxItems; i++)
        {
            if (items.GetElementAtPosition((uint)(size - 1 - i), out var element) >= 0)
            {
                result.Add(Item(i, element, evaluateName: null));
            }
        }

        return result;
    }

    /// <summary>Queue&lt;T&gt; is a ring buffer starting at <c>_head</c>.</summary>
    private static List<NamedValue>? QueueItems(ValueInfo value, TypeHandle handle)
    {
        if (ReadInt(value, handle, "_size") is not { } size
            || ReadInt(value, handle, "_head") is not { } head
            || BackingArray(value, handle, "_array") is not { } items
            || items.GetCount(out var capacity) < 0 || capacity == 0)
        {
            return null;
        }

        var result = new List<NamedValue>();
        for (var i = 0; i < size && i < MaxItems; i++)
        {
            if (items.GetElementAtPosition((uint)((head + i) % capacity), out var element) >= 0)
            {
                result.Add(Item(i, element, evaluateName: null));
            }
        }

        return result;
    }

    /// <summary>LinkedList&lt;T&gt; is circular: walk <c>count</c> nodes from <c>head</c>.</summary>
    private static List<NamedValue>? LinkedListItems(ValueInspector inspector, ValueInfo value, TypeHandle handle)
    {
        if (ReadInt(value, handle, "count") is not { } count || ValueInspector.GetFieldValue(value, handle, "head") is not { } headRaw)
        {
            return null;
        }

        var result = new List<NamedValue>();
        var node = ValueInspector.Analyze(headRaw);
        for (var i = 0; i < count && i < MaxItems && !node.IsNull; i++)
        {
            if (inspector.Resolve(node) is not { } nodeType)
            {
                break;
            }

            if (ValueInspector.GetFieldValue(node, nodeType, "item") is { } item)
            {
                result.Add(Item(i, item, evaluateName: null));
            }

            if (ValueInspector.GetFieldValue(node, nodeType, "next") is not { } next)
            {
                break;
            }

            node = ValueInspector.Analyze(next);
        }

        return result;
    }

    private static List<NamedValue>? ImmutableArrayItems(ValueInfo value, TypeHandle handle, string? evaluateName)
    {
        if (BackingArray(value, handle, "array") is not { } items || items.GetCount(out var count) < 0)
        {
            return null;
        }

        var result = new List<NamedValue>();
        for (var i = 0; i < count && i < MaxItems; i++)
        {
            if (items.GetElementAtPosition((uint)i, out var element) >= 0)
            {
                result.Add(Item(i, element, evaluateName));
            }
        }

        return result;
    }

    private static List<NamedValue>? DictionaryItems(ValueInspector inspector, ValueInfo value, TypeHandle handle, string? evaluateName)
    {
        var result = new List<NamedValue>();
        if (!ForEachEntry(inspector, value, handle, "next", (entry, entryType) =>
            {
                var key = ValueInspector.GetFieldValue(entry, entryType, "key");
                var item = ValueInspector.GetFieldValue(entry, entryType, "value");
                if (key is null)
                {
                    return;
                }

                var keyInfo = ValueInspector.Analyze(key);
                var keyText = inspector.Format(keyInfo);
                var evaluable = keyInfo.IsPrimitive || keyInfo.ElementType == CorElementType.String;
                result.Add(new NamedValue("[" + keyText + "]")
                {
                    Value = item,
                    EvaluateName = evaluateName is not null && evaluable ? evaluateName + "[" + keyText + "]" : null,
                });
            }))
        {
            return null;
        }

        return result;
    }

    private static List<NamedValue>? HashSetItems(ValueInspector inspector, ValueInfo value, TypeHandle handle)
    {
        var result = new List<NamedValue>();
        var index = 0;
        if (!ForEachEntry(inspector, value, handle, "Next", (entry, entryType) =>
            {
                if (ValueInspector.GetFieldValue(entry, entryType, "Value") is { } item)
                {
                    result.Add(Item(index++, item, evaluateName: null));
                }
            }))
        {
            return null;
        }

        return result;
    }

    /// <summary>Walks live entries of a Dictionary/HashSet (<c>next &gt;= -1</c> marks a used slot).</summary>
    private static bool ForEachEntry(ValueInspector inspector, ValueInfo value, TypeHandle handle, string nextField, Action<ValueInfo, TypeHandle> visit)
    {
        if (ReadInt(value, handle, "_count") is not { } count
            || ValueInspector.GetFieldValue(value, handle, "_entries") is not { } entriesRaw)
        {
            return false;
        }

        if (ValueInspector.Analyze(entriesRaw).Target is not ICorDebugArrayValue array)
        {
            return true; // never allocated: empty
        }

        TypeHandle? entryType = null;
        for (var i = 0; i < count && i < MaxItems; i++)
        {
            if (array.GetElementAtPosition((uint)i, out var entryRaw) < 0)
            {
                continue;
            }

            var entry = ValueInspector.Analyze(entryRaw);
            entryType ??= entry.Type is null ? null : inspector.Resolve(entry);
            if (entryType is null)
            {
                return false;
            }

            if (ValueInspector.GetFieldValue(entry, entryType, nextField) is { } nextRaw
                && ValueInspector.ReadPrimitive(nextRaw, CorElementType.I4) is int next && next >= -1)
            {
                visit(entry, entryType);
            }
        }

        return true;
    }

    private static NamedValue Item(int index, ICorDebugValue element, string? evaluateName)
    {
        var text = index.ToString(CultureInfo.InvariantCulture);
        return new NamedValue("[" + text + "]")
        {
            Value = element,
            EvaluateName = evaluateName is null ? null : evaluateName + "[" + text + "]",
        };
    }

    private static ICorDebugArrayValue? BackingArray(ValueInfo value, TypeHandle handle, string field) =>
        ValueInspector.GetFieldValue(value, handle, field) is { } raw && ValueInspector.Analyze(raw).Target is ICorDebugArrayValue array
            ? array
            : null;

    private static int? ImmutableArrayLength(ValueInfo value, TypeHandle handle) =>
        handle.Name == "System.Collections.Immutable.ImmutableArray" && BackingArray(value, handle, "array") is { } array
            && array.GetCount(out var count) >= 0
            ? (int)count
            : null;

    private static int? DictionaryCount(ValueInfo value, TypeHandle handle) =>
        ReadInt(value, handle, "_count") - (ReadInt(value, handle, "_freeCount") ?? 0);

    private static int? ReadInt(ValueInfo value, TypeHandle handle, string field) =>
        ValueInspector.GetFieldValue(value, handle, field) is { } raw
            && ValueInspector.ReadPrimitive(raw, CorElementType.I4) is int result
            ? result
            : null;
}

/// <summary>
/// "Results View": enumerates any IEnumerable by calling
/// <c>IEnumerable.GetEnumerator</c> / <c>IEnumerator.MoveNext</c> / <c>Current</c> in the
/// debuggee. Enumerating can have side effects (iterators are consumed), as in Visual Studio.
/// </summary>
internal sealed class ResultsViewContainer(ValueInspector inspector, IValueSource source, ICorDebugThread thread) : IVariableContainer
{
    private const int MaxItems = 1000;

    public int IndexedCount => 0;

    public IEnumerable<NamedValue> GetChildren(int start, int count)
    {
        var getEnumerator = inspector.GetCoreLibFunction("System.Collections.IEnumerable", "GetEnumerator", 0);
        var moveNext = inspector.GetCoreLibFunction("System.Collections.IEnumerator", "MoveNext", 0);
        var current = inspector.GetCoreLibFunction("System.Collections.IEnumerator", "get_Current", 0);
        if (getEnumerator is null || moveNext is null || current is null || source.Resolve() is not { } root)
        {
            yield return new NamedValue("Error") { Text = "<enumeration is not available>", Kind = VariableKind.Error };
            yield break;
        }

        var evaluator = inspector.Evaluator;
        var created = evaluator.Call(thread, getEnumerator, [], [ValueInspector.Analyze(root).ThisArgument]);
        if (Failure(created) is { } failure)
        {
            yield return failure;
            yield break;
        }

        // Pin the enumerator: every MoveNext/Current is a func-eval that would invalidate it.
        if (ValueInspector.Analyze(created.Value!).Heap is not ICorDebugHeapValue2 heap
            || heap.CreateHandle(CorDebugHandleType.Strong, out var enumerator) < 0)
        {
            yield return new NamedValue("Error") { Text = "<enumerator could not be pinned>", Kind = VariableKind.Error };
            yield break;
        }

        try
        {
            for (var index = 0; ; index++)
            {
                if (index == MaxItems)
                {
                    yield return new NamedValue("...") { Text = $"<only the first {MaxItems} items are shown>", Kind = VariableKind.Virtual };
                    yield break;
                }

                var moved = evaluator.Call(thread, moveNext, [], [enumerator]);
                if (Failure(moved) is { } moveFailure)
                {
                    yield return moveFailure;
                    yield break;
                }

                if (ValueInspector.ReadPrimitive(moved.Value!, CorElementType.Boolean) is not true)
                {
                    if (index == 0)
                    {
                        yield return new NamedValue("Empty") { Text = "Enumeration yielded no results", Kind = VariableKind.Virtual };
                    }

                    yield break;
                }

                var item = evaluator.Call(thread, current, [], [enumerator]);
                yield return Failure(item)
                    ?? new NamedValue("[" + index.ToString(CultureInfo.InvariantCulture) + "]") { Value = item.Value };
            }
        }
        finally
        {
            _ = enumerator.Dispose();
        }
    }

    private NamedValue? Failure(Runtime.EvalResult result) =>
        result.Succeeded && result.Value is not null
            ? null
            : inspector.DescribeEvalResult("Error", result, evaluateName: null, VariableKind.Error);
}
