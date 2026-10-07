using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>
/// Turns ICorDebug values into display strings and child lists. Stateless apart from caches
/// that are valid for the lifetime of the debuggee.
/// </summary>
public sealed partial class ValueInspector(ModuleRegistry modules, FuncEvaluator evaluator)
{
    private const int MaxDisplayStringLength = 8192;
    private const int MaxEvaluatedProperties = 40;

    /// <summary>Evaluate property getters when expanding objects.</summary>
    public bool EvaluateProperties { get; set; } = true;

    /// <summary>Offer properties as "click to evaluate" nodes instead of running every getter.</summary>
    public bool LazyProperties { get; set; }

    internal FuncEvaluator Evaluator => evaluator;

    // ---- Analysis ---------------------------------------------------------------------

    /// <summary>Follows references and unboxes until a concrete value is reached.</summary>
    public static ValueInfo Analyze(ICorDebugValue raw)
    {
        var current = raw;
        ICorDebugValue? heap = null;
        var isNull = false;
        for (var depth = 0; depth < 8; depth++)
        {
            if (current is ICorDebugReferenceValue reference)
            {
                if (reference.IsNull(out var referenceIsNull) < 0 || referenceIsNull
                    || reference.Dereference(out var target) < 0)
                {
                    isNull = true;
                    break;
                }

                current = target;
                heap ??= target;
                continue;
            }

            if (current is ICorDebugBoxValue box && box.GetObject(out var unboxed) >= 0)
            {
                current = unboxed;
                continue;
            }

            break;
        }

        var typeSource = isNull ? raw : current;
        _ = typeSource.GetType(out var elementType);
        ICorDebugType? exactType = null;
        if (typeSource is ICorDebugValue2 value2 && value2.GetExactType(out var type) >= 0)
        {
            exactType = type;
        }

        // A boxed primitive unboxes to a value-class object; report it as the primitive.
        if (!isNull && elementType is CorElementType.ValueType or CorElementType.Class && exactType is not null
            && exactType.GetType(out var exactElementType) >= 0
            && exactElementType is >= CorElementType.Boolean and <= CorElementType.R8 or CorElementType.I or CorElementType.U)
        {
            elementType = exactElementType;
        }

        return new ValueInfo(raw, heap, isNull ? null : current, isNull, elementType, exactType);
    }

    /// <summary>Resolves the TypeDef for class and value types.</summary>
    internal TypeHandle? Resolve(ValueInfo value)
    {
        if (!value.HandleResolved)
        {
            value.Handle = value.Type is null ? null : Resolve(value.Type);
            value.HandleResolved = true;
        }

        return value.Handle;
    }

    internal TypeHandle? Resolve(ICorDebugType type) => Resolve(type, includeBuiltIn: false);

    /// <summary>With <paramref name="includeBuiltIn"/>, also string and object (whose methods can be called).</summary>
    internal TypeHandle? Resolve(ICorDebugType type, bool includeBuiltIn)
    {
        if (type.GetType(out var elementType) < 0
            || !(elementType is CorElementType.Class or CorElementType.ValueType
                || (includeBuiltIn && elementType is CorElementType.String or CorElementType.Object))
            || type.GetClass(out var cls) < 0
            || cls.GetModule(out var corModule) < 0
            || cls.GetToken(out var token) < 0
            || modules.Find(corModule) is not { Metadata: not null } module)
        {
            return null;
        }

        return new TypeHandle(type, cls, module, token, module.Metadata.GetTypeName(token));
    }

    /// <summary>The type and its base types, most derived first, stopping before System.Object/ValueType/Enum.</summary>
    internal List<TypeHandle> GetTypeChain(ICorDebugType? type) => GetTypeChain(type, includeBuiltIn: false);

    /// <summary>With <paramref name="includeBuiltIn"/>, a string's chain is System.String (for calling its methods).</summary>
    internal List<TypeHandle> GetTypeChain(ICorDebugType? type, bool includeBuiltIn)
    {
        var chain = new List<TypeHandle>();
        for (var current = type; current is not null && chain.Count < 32;)
        {
            if (Resolve(current, includeBuiltIn) is not { } handle
                || handle.Name is "System.Object" or "System.ValueType" or "System.Enum")
            {
                break;
            }

            chain.Add(handle);
            if (current.GetBase(out var baseType) < 0)
            {
                break;
            }

            current = baseType;
        }

        return chain;
    }

    // ---- Type names -------------------------------------------------------------------

    public string GetTypeName(ValueInfo value) =>
        value.Type is null ? ElementTypeName(value.ElementType) : GetTypeName(value.Type);

    public string GetTypeName(ICorDebugType type)
    {
        if (type.GetType(out var elementType) < 0)
        {
            return "?";
        }

        switch (elementType)
        {
            case CorElementType.SzArray:
                return type.GetFirstTypeParameter(out var element) >= 0 ? GetTypeName(element) + "[]" : "?[]";
            case CorElementType.Array:
                _ = type.GetRank(out var rank);
                var arrayElement = type.GetFirstTypeParameter(out var multiElement) >= 0 ? GetTypeName(multiElement) : "?";
                return arrayElement + "[" + new string(',', (int)Math.Max(rank, 1) - 1) + "]";
            case CorElementType.Ptr:
                return (type.GetFirstTypeParameter(out var pointee) >= 0 ? GetTypeName(pointee) : "void") + "*";
            case CorElementType.ByRef:
                return (type.GetFirstTypeParameter(out var referent) >= 0 ? GetTypeName(referent) : "?") + "&";
            case CorElementType.Class or CorElementType.ValueType:
                var handle = Resolve(type);
                var name = handle?.Name ?? "?";
                var typeArgs = type.GetTypeParameters();
                if (typeArgs.Count == 0)
                {
                    return name;
                }

                if (name == "System.Nullable" && typeArgs.Count == 1)
                {
                    return GetTypeName(typeArgs[0]) + "?";
                }

                var builder = new StringBuilder(name).Append('<');
                for (var i = 0; i < typeArgs.Count; i++)
                {
                    _ = builder.Append(i == 0 ? string.Empty : ", ").Append(GetTypeName(typeArgs[i]));
                }

                return builder.Append('>').ToString();
            default:
                return ElementTypeName(elementType);
        }
    }

    internal static string ElementTypeName(CorElementType elementType) => elementType switch
    {
        CorElementType.Void => "void",
        CorElementType.Boolean => "bool",
        CorElementType.Char => "char",
        CorElementType.I1 => "sbyte",
        CorElementType.U1 => "byte",
        CorElementType.I2 => "short",
        CorElementType.U2 => "ushort",
        CorElementType.I4 => "int",
        CorElementType.U4 => "uint",
        CorElementType.I8 => "long",
        CorElementType.U8 => "ulong",
        CorElementType.R4 => "float",
        CorElementType.R8 => "double",
        CorElementType.I => "nint",
        CorElementType.U => "nuint",
        CorElementType.String => "string",
        CorElementType.Object => "object",
        _ => elementType.ToString(),
    };

    // ---- Primitives -------------------------------------------------------------------

    /// <summary>Reads a primitive into a boxed .NET value of the matching type.</summary>
    public static unsafe object? ReadPrimitive(ICorDebugValue value, CorElementType elementType)
    {
        if (value is not ICorDebugGenericValue generic)
        {
            return null;
        }

        ulong buffer = 0;
        if (generic.GetValue(&buffer) < 0)
        {
            return null;
        }

        var bytes = (byte*)&buffer;
        return elementType switch
        {
            CorElementType.Boolean => *bytes != 0,
            CorElementType.Char => *(char*)bytes,
            CorElementType.I1 => *(sbyte*)bytes,
            CorElementType.U1 => *bytes,
            CorElementType.I2 => *(short*)bytes,
            CorElementType.U2 => *(ushort*)bytes,
            CorElementType.I4 => *(int*)bytes,
            CorElementType.U4 => *(uint*)bytes,
            CorElementType.I8 => *(long*)bytes,
            CorElementType.U8 => buffer,
            CorElementType.R4 => *(float*)bytes,
            CorElementType.R8 => *(double*)bytes,
            CorElementType.I => (nint)(*(long*)bytes),
            CorElementType.U => (nuint)buffer,
            _ => null,
        };
    }

    public static string FormatPrimitive(object? value, bool hex) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        char c => hex ? $"0x{(int)c:X4} '{EscapeChar(c)}'" : $"{(int)c} '{EscapeChar(c)}'",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        string s => Quote(s),
        sbyte or short or int or long or nint when hex => "0x" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture),
        byte or ushort or uint or ulong or nuint when hex => "0x" + Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString("X", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            _ = builder.Append(EscapeChar(c, inString: true));
        }

        return builder.Append('"').ToString();
    }

    private static string EscapeChar(char c, bool inString = false) => c switch
    {
        '\0' => "\\0",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\\' => "\\\\",
        '"' when inString => "\\\"",
        '\'' when !inString => "\\'",
        _ when char.IsControl(c) => $"\\u{(int)c:X4}",
        _ => c.ToString(),
    };

    // ---- Formatting -------------------------------------------------------------------

    /// <summary>The one-line display string for a value.</summary>
    public string Format(ValueInfo value, bool hex = false)
    {
        if (value.IsNull)
        {
            return "null";
        }

        var target = value.Target!;
        switch (value.ElementType)
        {
            case var _ when value.IsPrimitive:
                return FormatPrimitive(ReadPrimitive(target, value.ElementType), hex);
            case CorElementType.String:
                var text = (target as ICorDebugStringValue)?.GetStringValue(MaxDisplayStringLength) ?? "<unreadable>";
                return Quote(text);
            case CorElementType.SzArray or CorElementType.Array:
                return FormatArray(value);
            case CorElementType.Ptr or CorElementType.FnPtr:
                return target.GetAddress(out var address) >= 0 ? $"0x{address:X}" : "<pointer>";
            default:
                return FormatObject(value, hex);
        }
    }

    private string FormatArray(ValueInfo value)
    {
        if (value.Target is not ICorDebugArrayValue array || array.GetCount(out var count) < 0)
        {
            return "{array}";
        }

        var elementName = value.Type is not null && value.Type.GetFirstTypeParameter(out var elementType) >= 0
            ? GetTypeName(elementType)
            : "?";
        return string.Create(CultureInfo.InvariantCulture, $"{elementName}[{FormatDimensions(array, count)}]");
    }

    private static unsafe string FormatDimensions(ICorDebugArrayValue array, uint count)
    {
        if (array.GetRank(out var rank) < 0 || rank <= 1)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        var dims = stackalloc uint[(int)rank];
        if (array.GetDimensions(rank, dims) < 0)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        var parts = new string[rank];
        for (var i = 0; i < rank; i++)
        {
            parts[i] = dims[i].ToString(CultureInfo.InvariantCulture);
        }

        return string.Join(", ", parts);
    }

    private string FormatObject(ValueInfo value, bool hex)
    {
        var handle = Resolve(value);
        if (handle is null)
        {
            return "{" + GetTypeName(value) + "}";
        }

        if (TryFormatWellKnown(value, handle, hex) is { } wellKnown)
        {
            return wellKnown;
        }

        if (IsEnum(handle) && TryFormatEnum(value, handle) is { } enumText)
        {
            return enumText;
        }

        if (CollectionView.TryGetCount(value, handle) is { } count)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Count = {count}");
        }

        return "{" + GetTypeName(value) + "}";
    }

    private bool IsEnum(TypeHandle handle) =>
        handle.Type.GetBase(out var baseType) >= 0 && baseType is not null && Resolve(baseType)?.Name == "System.Enum";

    private static string? TryFormatEnum(ValueInfo value, TypeHandle handle)
    {
        if (GetFieldValue(value, handle, "value__") is not { } raw)
        {
            return null;
        }

        var underlying = Analyze(raw);
        var number = ReadPrimitive(underlying.Target ?? raw, underlying.ElementType);
        if (number is null)
        {
            return null;
        }

        var bits = number switch
        {
            sbyte v => unchecked((ulong)v),
            short v => unchecked((ulong)v),
            int v => unchecked((ulong)v),
            long v => unchecked((ulong)v),
            _ => Convert.ToUInt64(number, CultureInfo.InvariantCulture),
        };
        var metadata = handle.Module.Metadata!;
        var members = metadata.GetEnumMembers(handle.Token);
        foreach (var (memberValue, name) in members)
        {
            if (memberValue == bits)
            {
                return name;
            }
        }

        if (metadata.IsFlagsEnum(handle.Token) && bits != 0)
        {
            var names = new List<string>();
            var remaining = bits;
            foreach (var (memberValue, name) in members)
            {
                if (memberValue != 0 && (remaining & memberValue) == memberValue)
                {
                    names.Add(name);
                    remaining &= ~memberValue;
                }
            }

            if (remaining == 0 && names.Count > 0)
            {
                return string.Join(" | ", names);
            }
        }

        return FormatPrimitive(number, hex: false);
    }

    /// <summary>System.Decimal, DateTime, TimeSpan, Guid and Nullable&lt;T&gt; read straight from their fields.</summary>
    private string? TryFormatWellKnown(ValueInfo value, TypeHandle handle, bool hex)
    {
        if (TryReadBoxedPrimitive(value) is { } boxed)
        {
            return FormatPrimitive(boxed, hex);
        }

        switch (handle.Name)
        {
            case "System.Decimal" when ReadDecimal(value, handle) is { } m:
                return m.ToString(CultureInfo.InvariantCulture);
            case "System.DateTime" when ReadField<ulong>(value, handle, "_dateData") is { } data:
                var ticks = (long)(data & 0x3FFFFFFFFFFFFFFF);
                var kindBits = (int)(data >> 62);
                var kind = kindBits >= 2 ? DateTimeKind.Local : (DateTimeKind)kindBits;
                return ticks is >= 0 and <= 3155378975999999999
                    ? new DateTime(ticks, kind).ToString("O", CultureInfo.InvariantCulture)
                    : null;
            case "System.TimeSpan" when ReadField<long>(value, handle, "_ticks") is { } spanTicks:
                return new TimeSpan(spanTicks).ToString("c", CultureInfo.InvariantCulture);
            case "System.Guid":
                return ReadGuid(value, handle)?.ToString("D", CultureInfo.InvariantCulture);
            case "System.Collections.Generic.KeyValuePair"
                when GetFieldValue(value, handle, "key") is { } key && GetFieldValue(value, handle, "value") is { } item:
                return "[" + Format(Analyze(key), hex) + ", " + Format(Analyze(item), hex) + "]";
            case "System.Collections.Immutable.ImmutableArray" when GetFieldValue(value, handle, "array") is { } backing:
                var array = Analyze(backing);
                return array.Target is ICorDebugArrayValue items && items.GetCount(out var length) >= 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Length = {length}")
                    : "Uninitialized";
            case "System.Nullable":
                if (GetFieldValue(value, handle, "hasValue") is { } hasValueRaw
                    && ReadPrimitive(hasValueRaw, CorElementType.Boolean) is bool hasValue)
                {
                    return hasValue && GetFieldValue(value, handle, "value") is { } inner
                        ? Format(Analyze(inner), hex)
                        : "null";
                }

                return null;
            default:
                return null;
        }
    }

    private static readonly Dictionary<string, CorElementType> BoxablePrimitives = new(StringComparer.Ordinal)
    {
        ["System.Boolean"] = CorElementType.Boolean,
        ["System.Char"] = CorElementType.Char,
        ["System.SByte"] = CorElementType.I1,
        ["System.Byte"] = CorElementType.U1,
        ["System.Int16"] = CorElementType.I2,
        ["System.UInt16"] = CorElementType.U2,
        ["System.Int32"] = CorElementType.I4,
        ["System.UInt32"] = CorElementType.U4,
        ["System.Int64"] = CorElementType.I8,
        ["System.UInt64"] = CorElementType.U8,
        ["System.Single"] = CorElementType.R4,
        ["System.Double"] = CorElementType.R8,
        ["System.IntPtr"] = CorElementType.I,
        ["System.UIntPtr"] = CorElementType.U,
    };

    /// <summary>A primitive that arrived boxed (e.g. <c>object o = 5</c>, <c>IEnumerator.Current</c>).</summary>
    internal object? TryReadBoxedPrimitive(ValueInfo value)
    {
        if (value.IsNull || value.IsPrimitive || Resolve(value) is not { } handle
            || !BoxablePrimitives.TryGetValue(handle.Name, out var elementType))
        {
            return null;
        }

        if (ReadPrimitive(value.Target!, elementType) is { } direct)
        {
            return direct;
        }

        return GetFieldValue(value, handle, "m_value") is { } field ? ReadPrimitive(field, elementType) : null;
    }

    private static decimal? ReadDecimal(ValueInfo value, TypeHandle handle)
    {
        if (ReadField<int>(value, handle, "_flags") is not { } flags
            || ReadField<uint>(value, handle, "_hi32") is not { } hi
            || ReadField<ulong>(value, handle, "_lo64") is not { } lo)
        {
            return null;
        }

        var scale = (byte)((flags >> 16) & 0xFF);
        return scale > 28 ? null : new decimal((int)(uint)lo, (int)(uint)(lo >> 32), (int)hi, flags < 0, scale);
    }

    private static Guid? ReadGuid(ValueInfo value, TypeHandle handle)
    {
        if (ReadField<int>(value, handle, "_a") is not { } a
            || ReadField<short>(value, handle, "_b") is not { } b
            || ReadField<short>(value, handle, "_c") is not { } c)
        {
            return null;
        }

        Span<byte> tail = stackalloc byte[8];
        ReadOnlySpan<string> names = ["_d", "_e", "_f", "_g", "_h", "_i", "_j", "_k"];
        for (var i = 0; i < names.Length; i++)
        {
            if (ReadField<byte>(value, handle, names[i]) is not { } part)
            {
                return null;
            }

            tail[i] = part;
        }

        return new Guid(a, b, c, tail[0], tail[1], tail[2], tail[3], tail[4], tail[5], tail[6], tail[7]);
    }

    private static T? ReadField<T>(ValueInfo value, TypeHandle handle, string name)
        where T : struct
    {
        if (GetFieldValue(value, handle, name) is not { } field)
        {
            return null;
        }

        var info = Analyze(field);
        return ReadPrimitive(info.Target ?? field, info.ElementType) is T typed ? typed : null;
    }

    // ---- Members ----------------------------------------------------------------------

    /// <summary>Reads an instance field declared on exactly <paramref name="declaringType"/>.</summary>
    internal static ICorDebugValue? GetFieldValue(ValueInfo value, TypeHandle declaringType, string name)
    {
        if (value.Target is not ICorDebugObjectValue obj)
        {
            return null;
        }

        foreach (var field in declaringType.Module.Metadata!.GetFields(declaringType.Token))
        {
            if (!field.IsStatic && field.Name == name)
            {
                return obj.GetFieldValue(declaringType.Class, field.Token, out var result) >= 0 ? result : null;
            }
        }

        return null;
    }

    /// <summary>Finds an instance field by name (or auto-property backing field) anywhere in the type chain.</summary>
    public ICorDebugValue? FindField(ValueInfo value, string name)
    {
        if (value.Target is not ICorDebugObjectValue obj)
        {
            return null;
        }

        var backingField = "<" + name + ">k__BackingField";
        foreach (var level in GetTypeChain(value.Type))
        {
            foreach (var field in level.Module.Metadata!.GetFields(level.Token))
            {
                if (!field.IsStatic && (field.Name == name || field.Name == backingField)
                    && obj.GetFieldValue(level.Class, field.Token, out var result) >= 0)
                {
                    return result;
                }
            }
        }

        return null;
    }

    /// <summary>Invokes a parameterless property getter by name (instance properties only).</summary>
    public EvalResult? InvokeProperty(ICorDebugThread thread, ValueInfo value, string name)
    {
        foreach (var level in GetTypeChain(value.Type, includeBuiltIn: true))
        {
            foreach (var property in level.Module.Metadata!.GetProperties(level.Token))
            {
                if (!property.IsStatic && property.Name == name)
                {
                    return CallMethod(thread, value, level, property.GetterToken, []);
                }
            }
        }

        return null;
    }

    /// <summary>Invokes an instance method by name and arity.</summary>
    public EvalResult? InvokeMethod(ICorDebugThread thread, ValueInfo value, string name, IReadOnlyList<ICorDebugValue> args)
    {
        foreach (var level in GetTypeChain(value.Type, includeBuiltIn: true))
        {
            var token = PickOverload(level.Module.Metadata!, level.Token, name, args, isStatic: false);
            if (token != 0)
            {
                return CallMethod(thread, value, level, token, args);
            }
        }

        return null;
    }

    internal EvalResult CallMethod(ICorDebugThread thread, ValueInfo value, TypeHandle level, uint methodToken, IReadOnlyList<ICorDebugValue> args)
    {
        if (level.Module.Module.GetFunctionFromToken(methodToken, out var function) < 0)
        {
            return EvalResult.Failure("Method not found in the debuggee.");
        }

        var allArgs = new List<ICorDebugValue>(args.Count + 1) { value.ThisArgument };
        allArgs.AddRange(args);
        return evaluator.Call(thread, function, level.Type.GetTypeParameters(), allArgs);
    }

    /// <summary>Calls <c>ToString()</c> and reads the result; null when unavailable.</summary>
    public string? InvokeToString(ICorDebugThread thread, ValueInfo value)
    {
        var result = InvokeMethod(thread, value, "ToString", []);
        if (result is not { Succeeded: true, Value: { } returned })
        {
            return null;
        }

        var info = Analyze(returned);
        return info.Target is ICorDebugStringValue s ? s.GetStringValue(MaxDisplayStringLength) : null;
    }

    // ---- [DebuggerDisplay] -------------------------------------------------------------

    /// <summary>The <c>[DebuggerDisplay]</c> format for the value's type (or a base type).</summary>
    public string? GetDisplayFormat(ValueInfo value)
    {
        if (value.IsNull || value.Target is not ICorDebugObjectValue || Resolve(value) is not { } handle)
        {
            return null;
        }

        // Built-in formats are exact and cost no func-eval; they win over the attribute.
        if (IsEnum(handle) || BoxablePrimitives.ContainsKey(handle.Name) || CollectionView.TryGetCount(value, handle) is not null
            || handle.Name is "System.Decimal" or "System.DateTime" or "System.TimeSpan" or "System.Guid" or "System.Nullable"
                or "System.Collections.Generic.KeyValuePair" or "System.Collections.Immutable.ImmutableArray")
        {
            return null;
        }

        foreach (var level in GetTypeChain(value.Type))
        {
            if (level.Module.Metadata!.GetDebuggerDisplay(level.Token) is { } format)
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>
    /// Expands a <c>[DebuggerDisplay]</c> format: each <c>{expression[,nq|,h|,d]}</c> hole is
    /// evaluated with the object as <c>this</c>. Runs code in the debuggee, so callers must not
    /// rely on values read before this call.
    /// </summary>
    public string FormatDisplay(string format, IValueSource source, ICorDebugThread thread, bool hex)
    {
        var scope = new ObjectScope(thread, source);
        var expressions = new Evaluation.ExpressionEvaluator(this);
        var builder = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                _ = builder.Append(format[++i]);
                continue;
            }

            if (c != '{')
            {
                _ = builder.Append(c);
                continue;
            }

            var depth = 1;
            var end = i + 1;
            for (; end < format.Length && depth > 0; end++)
            {
                depth += format[end] switch
                {
                    '{' => 1,
                    '}' => -1,
                    _ => 0,
                };
            }

            if (depth != 0)
            {
                _ = builder.Append(format, i, format.Length - i);
                break;
            }

            var (expression, noQuotes, holeHex) = ParseFormatSpecifiers(format[(i + 1)..(end - 1)], hex);
            i = end - 1;
            var outcome = expressions.Evaluate(expression, scope);
            _ = builder.Append(outcome switch
            {
                { Error: { } error } => "<" + error + ">",
                { Value: { } result } => noQuotes && result.Target is ICorDebugStringValue text
                    ? text.GetStringValue(MaxDisplayStringLength)
                    : Format(result, holeHex),
                _ => noQuotes && outcome.Constant is string constant ? constant : FormatPrimitive(outcome.Constant, holeHex),
            });
        }

        return builder.ToString();
    }

    private static (string Expression, bool NoQuotes, bool Hex) ParseFormatSpecifiers(string hole, bool hex)
    {
        var comma = hole.LastIndexOf(',');
        if (comma < 0)
        {
            return (hole.Trim(), false, hex);
        }

        var noQuotes = false;
        foreach (var specifier in hole[(comma + 1)..].Split(',', StringSplitOptions.TrimEntries))
        {
            switch (specifier)
            {
                case "nq":
                    noQuotes = true;
                    break;
                case "h":
                    hex = true;
                    break;
                case "d":
                    hex = false;
                    break;
                case "raw" or "nse" or "ac" or "results":
                    break;
                default:
                    return (hole.Trim(), false, hex); // a comma inside the expression
            }
        }

        return (hole[..comma].Trim(), noQuotes, hex);
    }

    /// <summary>Name lookup where the only name is <c>this</c>; members resolve through it.</summary>
    private sealed class ObjectScope(ICorDebugThread thread, IValueSource source) : Evaluation.IEvaluationScope
    {
        public ICorDebugThread Thread => thread;

        public ICorDebugValue? Lookup(string name) => name == "this" ? source.Resolve() : null;
    }

    // ---- Framework methods ------------------------------------------------------------

    private readonly Dictionary<(string, string, int), ICorDebugFunction?> _coreLibFunctions = [];

    /// <summary>A method of System.Private.CoreLib, e.g. <c>System.Collections.IEnumerator.MoveNext</c>.</summary>
    internal ICorDebugFunction? GetCoreLibFunction(string typeName, string methodName, int parameterCount)
    {
        var key = (typeName, methodName, parameterCount);
        if (_coreLibFunctions.TryGetValue(key, out var cached) && cached is not null)
        {
            return cached;
        }

        ICorDebugFunction? function = null;
        if (FindCoreLib() is { Metadata: { } metadata } coreLib
            && metadata.FindType(typeName) is var type and not 0
            && metadata.FindMethod(type, methodName, parameterCount) is var token and not 0
            && coreLib.Module.GetFunctionFromToken(token, out var found) >= 0)
        {
            function = found;
        }

        return _coreLibFunctions[key] = function;
    }

    internal LoadedModule? FindCoreLib()
    {
        foreach (var module in modules.Snapshot())
        {
            if (module.Name.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }

        return null;
    }

    /// <summary>Whether a value implements <c>IEnumerable</c> (strings and arrays excluded).</summary>
    internal bool IsEnumerable(ValueInfo value)
    {
        foreach (var level in GetTypeChain(value.Type))
        {
            if (level.Module.Metadata!.DeclaresEnumerable(level.Token))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Children ---------------------------------------------------------------------

    /// <summary>A container for the value's children, or null if it has none.</summary>
    /// <param name="value">The value, analyzed from <paramref name="source"/>.</param>
    /// <param name="evaluateName">Expression for the value, used to build children's expressions.</param>
    /// <param name="thread">Thread for func-evals of property getters.</param>
    /// <param name="source">How to re-obtain the value later (after func-evals invalidate it).</param>
    public IVariableContainer? CreateContainer(ValueInfo value, string? evaluateName, ICorDebugThread? thread, IValueSource source)
    {
        if (value.IsNull || value.IsPrimitive || value.ElementType is CorElementType.String or CorElementType.Ptr or CorElementType.FnPtr)
        {
            return null;
        }

        if (value.ElementType is CorElementType.SzArray or CorElementType.Array)
        {
            return value.Target is ICorDebugArrayValue array && array.GetCount(out var count) >= 0 && count > 0
                ? new ArrayContainer(source, (int)count, evaluateName)
                : null;
        }

        var handle = Resolve(value);
        if (handle is null || IsEnum(handle) || BoxablePrimitives.ContainsKey(handle.Name)
            || handle.Name is "System.Decimal" or "System.DateTime" or "System.TimeSpan" or "System.Guid")
        {
            return null;
        }

        if (handle.Name == "System.Nullable")
        {
            return GetFieldValue(value, handle, "hasValue") is { } hasValue
                && ReadPrimitive(hasValue, CorElementType.Boolean) is true
                && GetFieldValue(value, handle, "value") is { } inner
                    ? CreateContainer(Analyze(inner), evaluateName is null ? null : evaluateName + ".Value", thread, new FixedValueSource(inner))
                    : null;
        }

        return CollectionView.TryCreate(this, value, handle, evaluateName, thread, source)
            ?? new ObjectContainer(this, source, evaluateName, thread);
    }

    /// <summary>
    /// Instance fields, then evaluated properties, of an object. Lazy on purpose: the caller
    /// formats each child before the next property getter runs, because a func-eval can
    /// invalidate values obtained before it.
    /// </summary>
    internal IEnumerable<NamedValue> GetObjectMembers(IValueSource source, string? evaluateName, ICorDebugThread? thread)
    {
        if (source.Resolve() is not { } root)
        {
            yield break;
        }

        var value = Analyze(root);
        if (value.Target is not ICorDebugObjectValue obj)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasStatics = false;
        var chain = GetTypeChain(value.Type);
        foreach (var level in chain)
        {
            foreach (var field in level.Module.Metadata!.GetFields(level.Token))
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                if (field.IsStatic)
                {
                    hasStatics |= field.IsBrowsable;
                    continue;
                }

                // Roslyn marks auto-property backing fields [DebuggerBrowsable(Never)]; they are
                // shown under the property's name instead of being hidden.
                var (name, kind) = DisplayNameForField(field.Name);
                if (name is null || (!field.IsBrowsable && kind != VariableKind.Property) || !seen.Add(name))
                {
                    continue;
                }

                yield return obj.GetFieldValue(level.Class, field.Token, out var fieldValue) >= 0
                    ? new NamedValue(name) { Value = fieldValue, Kind = kind, EvaluateName = Member(evaluateName, name) }
                    : new NamedValue(name) { Text = "<unavailable>", Kind = VariableKind.Error };
            }
        }

        if (EvaluateProperties && thread is not null)
        {
            var evaluated = 0;
            foreach (var level in chain)
            {
                foreach (var property in level.Module.Metadata!.GetProperties(level.Token))
                {
                    if (property.IsStatic || !property.IsPublic || !property.IsBrowsable || !seen.Add(property.Name)
                        || evaluated++ >= MaxEvaluatedProperties)
                    {
                        continue;
                    }

                    if (LazyProperties)
                    {
                        yield return new NamedValue(property.Name)
                        {
                            Group = new LazyPropertyContainer(this, source, level, property, Member(evaluateName, property.Name), thread),
                            Text = "(evaluate)",
                            Kind = VariableKind.Property,
                            IsLazy = true,
                        };
                        continue;
                    }

                    // The previous getter may have invalidated 'this'; re-resolve it.
                    if (evaluated > 1 && source.Resolve() is { } fresh)
                    {
                        value = Analyze(fresh);
                    }

                    var call = CallMethod(thread, value, level, property.GetterToken, []);
                    yield return DescribeEvalResult(property.Name, call, Member(evaluateName, property.Name), VariableKind.Property);
                }
            }
        }

        if (thread is not null && IsEnumerable(value))
        {
            yield return new NamedValue("Results View")
            {
                Group = new ResultsViewContainer(this, source, thread),
                Text = "Expanding the Results View will enumerate the IEnumerable",
                Kind = VariableKind.Virtual,
            };
        }

        if (hasStatics && value.Type is not null)
        {
            yield return new NamedValue("Static members")
            {
                Group = new StaticMembersContainer(chain),
                Text = string.Empty,
                Kind = VariableKind.Class,
            };
        }
    }

    internal static IEnumerable<NamedValue> GetStaticMembers(List<TypeHandle> chain)
    {
        foreach (var level in chain)
        {
            foreach (var field in level.Module.Metadata!.GetFields(level.Token))
            {
                if (!field.IsStatic || field.IsLiteral)
                {
                    continue;
                }

                var (name, kind) = DisplayNameForField(field.Name);
                if (name is null || (!field.IsBrowsable && kind != VariableKind.Property))
                {
                    continue;
                }

                yield return level.Type.GetStaticFieldValue(field.Token, null, out var fieldValue) >= 0
                    ? new NamedValue(name) { Value = fieldValue, Kind = kind, EvaluateName = level.Name + "." + name }
                    : new NamedValue(name) { Text = "<unavailable>", Kind = VariableKind.Error };
            }
        }
    }

    internal NamedValue DescribeEvalResult(string name, EvalResult result, string? evaluateName, VariableKind kind)
    {
        if (result.Error is not null)
        {
            return new NamedValue(name) { Text = "<" + result.Error + ">", Kind = VariableKind.Error };
        }

        if (result.IsException)
        {
            var exceptionType = result.Value is null ? "an exception" : GetTypeName(Analyze(result.Value));
            return new NamedValue(name) { Text = "<threw " + exceptionType + ">", Kind = VariableKind.Error };
        }

        return new NamedValue(name) { Value = result.Value, Kind = kind, EvaluateName = evaluateName };
    }

    /// <summary>Maps compiler field names to what the user wrote; null hides the field.</summary>
    internal static (string? Name, VariableKind Kind) DisplayNameForField(string fieldName)
    {
        if (!fieldName.StartsWith('<'))
        {
            return (fieldName, VariableKind.Data);
        }

        const string BackingSuffix = ">k__BackingField";
        if (fieldName.EndsWith(BackingSuffix, StringComparison.Ordinal))
        {
            return (fieldName[1..^BackingSuffix.Length], VariableKind.Property);
        }

        return (null, VariableKind.Data);
    }

    internal static string? Member(string? parent, string name) => parent is null ? null : parent + "." + name;

    // ---- Assignment -------------------------------------------------------------------

    /// <summary>Writes a primitive, null, or (via func-eval) a string into <paramref name="target"/>.</summary>
    public unsafe string? TryAssign(ICorDebugValue target, string text, ICorDebugThread? thread)
    {
        text = text.Trim();
        if (target is ICorDebugReferenceValue reference)
        {
            if (text == "null")
            {
                return reference.SetValue(0) >= 0 ? null : "Assignment failed.";
            }

            _ = target.GetType(out var referenceType);
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"' && thread is not null
                && (referenceType is CorElementType.String or CorElementType.Class or CorElementType.Object))
            {
                var created = evaluator.NewString(thread, Unquote(text));
                if (created is not { Succeeded: true, Value: ICorDebugReferenceValue newString }
                    || newString.GetValue(out var address) < 0)
                {
                    return created.Error ?? "Could not create the string.";
                }

                return reference.SetValue(address) >= 0 ? null : "Assignment failed.";
            }

            var info = Analyze(target);
            return info.Target is not null && info.Target is not ICorDebugReferenceValue
                ? TryAssign(info.Target, text, thread)
                : "Only primitives, strings and null can be assigned.";
        }

        if (target is not ICorDebugGenericValue generic || target.GetType(out var elementType) < 0)
        {
            return "Only primitives, strings and null can be assigned.";
        }

        if (!TryParsePrimitive(text, elementType, out var bits))
        {
            return $"'{text}' is not a valid {ElementTypeName(elementType)}.";
        }

        return generic.SetValue(&bits) >= 0 ? null : "Assignment failed.";
    }

    private static string Unquote(string text) =>
        System.Text.RegularExpressions.Regex.Unescape(text[1..^1]);

    internal static bool TryParsePrimitive(string text, CorElementType elementType, out ulong bits)
    {
        bits = 0;
        var style = NumberStyles.Integer;
        var span = text.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            style = NumberStyles.HexNumber;
        }

        var culture = CultureInfo.InvariantCulture;
        switch (elementType)
        {
            case CorElementType.Boolean when bool.TryParse(text, out var b):
                bits = b ? 1UL : 0UL;
                return true;
            case CorElementType.Char when text.Length == 3 && text[0] == '\'' && text[2] == '\'':
                bits = text[1];
                return true;
            case CorElementType.Char when ushort.TryParse(span, style, culture, out var c):
                bits = c;
                return true;
            case CorElementType.I1 when sbyte.TryParse(span, style, culture, out var v):
                bits = unchecked((byte)v);
                return true;
            case CorElementType.U1 when byte.TryParse(span, style, culture, out var v):
                bits = v;
                return true;
            case CorElementType.I2 when short.TryParse(span, style, culture, out var v):
                bits = unchecked((ushort)v);
                return true;
            case CorElementType.U2 when ushort.TryParse(span, style, culture, out var v):
                bits = v;
                return true;
            case CorElementType.I4 when int.TryParse(span, style, culture, out var v):
                bits = unchecked((uint)v);
                return true;
            case CorElementType.U4 when uint.TryParse(span, style, culture, out var v):
                bits = v;
                return true;
            case CorElementType.I8 or CorElementType.I when long.TryParse(span, style, culture, out var v):
                bits = unchecked((ulong)v);
                return true;
            case CorElementType.U8 or CorElementType.U when ulong.TryParse(span, style, culture, out var v):
                bits = v;
                return true;
            case CorElementType.R4 when float.TryParse(text, NumberStyles.Float, culture, out var v):
                bits = BitConverter.SingleToUInt32Bits(v);
                return true;
            case CorElementType.R8 when double.TryParse(text, NumberStyles.Float, culture, out var v):
                bits = BitConverter.DoubleToUInt64Bits(v);
                return true;
            default:
                return false;
        }
    }
}
