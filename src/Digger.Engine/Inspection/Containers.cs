using System;
using System.Collections.Generic;
using System.Globalization;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>Fields and properties of a class or struct instance.</summary>
internal sealed class ObjectContainer(ValueInspector inspector, IValueSource source, string? evaluateName, ICorDebugThread? thread) : IVariableContainer
{
    public int IndexedCount => 0;

    public bool SortByName => true;

    public IEnumerable<NamedValue> GetChildren(int start, int count) =>
        inspector.GetObjectMembers(source, evaluateName, thread);
}

/// <summary>Static fields of a type chain.</summary>
internal sealed class StaticMembersContainer(List<TypeHandle> chain) : IVariableContainer
{
    public int IndexedCount => 0;

    public bool SortByName => true;

    public IEnumerable<NamedValue> GetChildren(int start, int count) => ValueInspector.GetStaticMembers(chain);
}

/// <summary>Array elements, paged so huge arrays stay cheap.</summary>
internal sealed class ArrayContainer(IValueSource source, int length, string? evaluateName) : IVariableContainer
{
    private const int DefaultPage = 1000;

    public int IndexedCount => length;

    public IEnumerable<NamedValue> GetChildren(int start, int count)
    {
        if (source.Resolve() is not { } root || ValueInspector.Analyze(root).Target is not ICorDebugArrayValue array)
        {
            yield break;
        }

        var dimensions = GetDimensions(array);
        start = Math.Clamp(start, 0, length);
        var end = count > 0 ? Math.Min(length, start + count) : Math.Min(length, start + DefaultPage);
        for (var i = start; i < end; i++)
        {
            var index = FormatIndex(dimensions, i);
            yield return array.GetElementAtPosition((uint)i, out var element) >= 0
                ? new NamedValue("[" + index + "]") { Value = element, EvaluateName = evaluateName is null ? null : evaluateName + "[" + index + "]" }
                : new NamedValue("[" + index + "]") { Text = "<unavailable>", Kind = VariableKind.Error };
        }
    }

    private static unsafe uint[] GetDimensions(ICorDebugArrayValue array)
    {
        if (array.GetRank(out var rank) < 0 || rank <= 1)
        {
            return [];
        }

        var dimensions = new uint[rank];
        fixed (uint* dims = dimensions)
        {
            return array.GetDimensions(rank, dims) >= 0 ? dimensions : [];
        }
    }

    private static string FormatIndex(uint[] dimensions, int position)
    {
        if (dimensions.Length <= 1)
        {
            return position.ToString(CultureInfo.InvariantCulture);
        }

        // Row-major: the last dimension varies fastest.
        var parts = new string[dimensions.Length];
        var remaining = position;
        for (var d = dimensions.Length - 1; d >= 0; d--)
        {
            var size = (int)Math.Max(dimensions[d], 1);
            parts[d] = (remaining % size).ToString(CultureInfo.InvariantCulture);
            remaining /= size;
        }

        return string.Join(",", parts);
    }
}

/// <summary>Children produced on demand by a delegate (collection views).</summary>
internal sealed class LazyContainer(Func<IEnumerable<NamedValue>> children, int indexedCount) : IVariableContainer
{
    public int IndexedCount => indexedCount;

    public IEnumerable<NamedValue> GetChildren(int start, int count)
    {
        var index = 0;
        var end = count > 0 ? start + count : int.MaxValue;
        foreach (var child in children())
        {
            // Paging applies to indexed items; trailing groups (Raw View) are always returned.
            if (child.Group is not null || (index >= start && index < end))
            {
                yield return child;
            }

            if (child.Group is null)
            {
                index++;
            }
        }
    }
}

/// <summary>A property whose getter runs only when the user asks for it.</summary>
internal sealed class LazyPropertyContainer(ValueInspector inspector, IValueSource source, TypeHandle level, Symbols.PropertyInfo property, string? evaluateName, ICorDebugThread thread) : IVariableContainer
{
    public int IndexedCount => 0;

    public IEnumerable<NamedValue> GetChildren(int start, int count)
    {
        if (source.Resolve() is not { } root)
        {
            yield break;
        }

        var result = inspector.CallMethod(thread, ValueInspector.Analyze(root), level, property.GetterToken, []);
        yield return inspector.DescribeEvalResult(property.Name, result, evaluateName, VariableKind.Property);
    }
}
