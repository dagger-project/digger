using System;
using System.Collections.Generic;
using System.Globalization;
using Digger.Engine.Inspection;
using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Evaluation;

/// <summary>Name lookup for an evaluation: a stack frame's locals, arguments and <c>this</c>.</summary>
public interface IEvaluationScope
{
    /// <summary>Thread used for func-evals (property getters, method calls).</summary>
    ICorDebugThread Thread { get; }

    /// <summary>A local, argument, hoisted/captured variable, or <c>this</c>; null when unknown.</summary>
    ICorDebugValue? Lookup(string name);

    /// <summary>The type whose code is running (the user's type, for lambdas and async methods), for static names.</summary>
    StaticType? DeclaringType => null;
}

/// <summary>The "value" of a call to a method that returns <c>void</c>.</summary>
public sealed class VoidResult
{
    public static VoidResult Instance { get; } = new();

    private VoidResult()
    {
    }

    public override string ToString() => "void";
}

/// <summary>Result of evaluating an expression: a debuggee value, a local constant, or an error.</summary>
public sealed record EvaluationOutcome(ValueInfo? Value, object? Constant, string? Error)
{
    public bool IsError => Error is not null;

    public static EvaluationOutcome Fail(string error) => new(null, null, error);
}

/// <summary>
/// Interprets <see cref="Expr"/> trees. Debuggee values are read in place; members that
/// are not fields are resolved with func-eval. Arithmetic happens locally on copies.
/// </summary>
public sealed class ExpressionEvaluator(ValueInspector inspector)
{
    public EvaluationOutcome Evaluate(string text, IEvaluationScope scope)
    {
        try
        {
            var result = Eval(ExpressionParser.Parse(text), scope);
            if (result.Type is { } type)
            {
                return EvaluationOutcome.Fail($"'{type.Name}' is a type, not a value.");
            }

            return result.Debuggee is not null
                ? new EvaluationOutcome(result.Debuggee, null, null)
                : new EvaluationOutcome(null, result.Constant, null);
        }
        catch (ExpressionException ex)
        {
            return EvaluationOutcome.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException or IndexOutOfRangeException)
        {
            return EvaluationOutcome.Fail(ex.Message);
        }
    }

    /// <summary>Evaluates a breakpoint condition; errors count as "true" so the user sees them.</summary>
    public bool EvaluateCondition(string text, IEvaluationScope scope, out string? error)
    {
        error = null;
        try
        {
            return ToBool(Eval(ExpressionParser.Parse(text), scope));
        }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException or OverflowException or FormatException or IndexOutOfRangeException)
        {
            error = ex.Message;
            return true;
        }
    }

    // ---- Operands ---------------------------------------------------------------------

    /// <summary>A debuggee value, a local constant (null, bool, char, numbers, string), or a type (for its statics).</summary>
    private readonly record struct Operand(ValueInfo? Debuggee, object? Constant, StaticType? Type = null)
    {
        public static Operand Of(StaticType type) => new(null, null, type);

        public static Operand Of(object? constant) => new(null, constant);

        public static Operand Of(ICorDebugValue value) => new(ValueInspector.Analyze(value), null);
    }

    private Operand Eval(Expr expression, IEvaluationScope scope) => expression switch
    {
        LiteralExpr literal => Operand.Of(literal.Value),
        NameExpr name => EvalName(name.Name, scope),
        MemberExpr member => ResolveQualifiedType(member, scope) is { } type
            ? Operand.Of(type)
            : EvalMember(Eval(member.Target, scope), member.Name, scope),
        IndexExpr index => EvalIndex(index, scope),
        CallExpr call => EvalCall(call, scope),
        UnaryExpr unary => EvalUnary(unary.Operator, Eval(unary.Operand, scope)),
        BinaryExpr binary => EvalBinary(binary, scope),
        ConditionalExpr conditional => ToBool(Eval(conditional.Condition, scope))
            ? Eval(conditional.WhenTrue, scope)
            : Eval(conditional.WhenFalse, scope),
        _ => throw new ExpressionException("Unsupported expression."),
    };

    private Operand EvalName(string name, IEvaluationScope scope)
    {
        if (scope.Lookup(name) is { } value)
        {
            return Operand.Of(value);
        }

        // Implicit 'this' member.
        if (name != "this" && scope.Lookup("this") is { } self)
        {
            var thisInfo = ValueInspector.Analyze(self);
            if (TryMember(thisInfo, name, scope, out var member))
            {
                return member;
            }
        }

        // Static member of the running code's type (or of a type it is nested in), then a type name.
        for (var type = scope.DeclaringType; type is not null; type = Enclosing(type))
        {
            if (TryStaticMember(type, name, scope, out var member))
            {
                return member;
            }
        }

        if (inspector.ResolveTypeName(name, scope.DeclaringType) is { } named)
        {
            return Operand.Of(named);
        }

        throw new ExpressionException($"The name '{name}' does not exist in the current context.");
    }

    private StaticType? Enclosing(StaticType type) =>
        type.Module.Metadata!.GetEnclosingType(type.Token) is not 0 and var outer
            ? inspector.FindType(type.Module.Metadata.GetMetadataName(outer))
            : null;

    /// <summary>
    /// <c>System.IO.Path</c> or <c>MyApp.Program</c>: a dotted name whose first part is not a
    /// variable may be a namespace-qualified type.
    /// </summary>
    private StaticType? ResolveQualifiedType(MemberExpr member, IEvaluationScope scope)
    {
        var parts = new List<string> { member.Name };
        Expr current = member.Target;
        while (current is MemberExpr inner)
        {
            parts.Add(inner.Name);
            current = inner.Target;
        }

        if (current is not NameExpr root || root.Name == "this" || scope.Lookup(root.Name) is not null)
        {
            return null;
        }

        parts.Add(root.Name);
        parts.Reverse();
        return inspector.FindType(string.Join('.', parts));
    }

    private bool TryStaticMember(StaticType type, string name, IEvaluationScope scope, out Operand result)
    {
        if (ValueInspector.GetStaticField(type, name, scope.Thread) is { } field)
        {
            result = Operand.Of(field);
            return true;
        }

        if (inspector.InvokeStaticProperty(scope.Thread, type, name) is { } property)
        {
            result = FromEval(property, name);
            return true;
        }

        if (ValueInspector.FindNestedType(type, name) is { } nested)
        {
            result = Operand.Of(nested);
            return true;
        }

        result = default;
        return false;
    }

    private Operand EvalMember(Operand target, string name, IEvaluationScope scope)
    {
        if (target.Type is { } type)
        {
            return TryStaticMember(type, name, scope, out var member)
                ? member
                : throw new ExpressionException($"'{type.Name}' does not contain a static member named '{name}'.");
        }

        if (target.Debuggee is null)
        {
            return target.Constant switch
            {
                string s when name == "Length" => Operand.Of(s.Length),
                null => throw new ExpressionException("Object reference not set to an instance of an object."),
                _ => throw new ExpressionException($"Member '{name}' is not available on a constant."),
            };
        }

        var info = target.Debuggee;
        if (info.IsNull)
        {
            throw new ExpressionException($"Cannot read '{name}': the value is null.");
        }

        return TryMember(info, name, scope, out var result)
            ? result
            : throw new ExpressionException($"'{inspector.GetTypeName(info)}' does not contain a member named '{name}'.");
    }

    private bool TryMember(ValueInfo info, string name, IEvaluationScope scope, out Operand result)
    {
        result = default;
        if (info.IsNull)
        {
            return false;
        }

        if (info.Target is ICorDebugArrayValue array && name is "Length" or "LongLength" or "Count")
        {
            _ = array.GetCount(out var count);
            result = Operand.Of((int)count);
            return true;
        }

        if (info.Target is ICorDebugStringValue text && name == "Length")
        {
            _ = text.GetLength(out var length);
            result = Operand.Of((int)length);
            return true;
        }

        if (inspector.FindField(info, name) is { } field)
        {
            result = Operand.Of(field);
            return true;
        }

        if (inspector.InvokeProperty(scope.Thread, info, name) is { } call)
        {
            result = FromEval(call, name);
            return true;
        }

        return false;
    }

    private Operand EvalIndex(IndexExpr expression, IEvaluationScope scope)
    {
        // The index goes first: evaluating it may run a getter, which would invalidate an
        // already-read target value.
        var index = Eval(expression.Index, scope);
        var position = ToConstant(index);
        var target = Eval(expression.Target, scope);
        if (target.Debuggee is null)
        {
            return target.Constant is string s && position is IConvertible
                ? Operand.Of(s[Convert.ToInt32(position, CultureInfo.InvariantCulture)])
                : throw new ExpressionException("Cannot index into this value.");
        }

        var info = target.Debuggee;
        if (info.IsNull)
        {
            throw new ExpressionException("Cannot index into a null value.");
        }

        if (info.Target is ICorDebugArrayValue array && IsInteger(position))
        {
            var i = Convert.ToUInt32(position, CultureInfo.InvariantCulture);
            return array.GetElementAtPosition(i, out var element) >= 0
                ? Operand.Of(element)
                : throw new ExpressionException("Index was outside the bounds of the array.");
        }

        if (info.Target is ICorDebugStringValue text && IsInteger(position))
        {
            var value = text.GetStringValue() ?? string.Empty;
            var i = Convert.ToInt32(position, CultureInfo.InvariantCulture);
            return i >= 0 && i < value.Length
                ? Operand.Of(value[i])
                : throw new ExpressionException("Index was outside the bounds of the string.");
        }

        var argument = ToDebuggee(index, scope);
        var call = inspector.InvokeMethod(scope.Thread, info, "get_Item", [argument])
            ?? throw new ExpressionException($"'{inspector.GetTypeName(info)}' has no indexer.");
        return FromEval(call, "indexer");
    }

    private Operand EvalCall(CallExpr call, IEvaluationScope scope)
    {
        // Arguments first, then the target, so no func-eval runs between reading the
        // target and calling the method on it.
        var args = new List<ICorDebugValue>(call.Arguments.Count);
        foreach (var argument in call.Arguments)
        {
            args.Add(ToDebuggee(Eval(argument, scope), scope));
        }

        // A bare call: an instance method of 'this', else a static method of the running code's type.
        if (call.Target is NameExpr { Name: "this" })
        {
            if (scope.Lookup("this") is { } self && inspector.InvokeMethod(scope.Thread, ValueInspector.Analyze(self), call.Method, args) is { } own)
            {
                return FromEval(own, call.Method);
            }

            for (var type = scope.DeclaringType; type is not null; type = Enclosing(type))
            {
                if (inspector.InvokeStaticMethod(scope.Thread, type, call.Method, args) is { } called)
                {
                    return FromEval(called, call.Method);
                }
            }

            throw new ExpressionException($"The name '{call.Method}' does not exist in the current context (or takes a different number of arguments).");
        }

        var target = Eval(call.Target, scope);
        if (target.Type is { } staticType)
        {
            var staticCall = inspector.InvokeStaticMethod(scope.Thread, staticType, call.Method, args)
                ?? throw new ExpressionException($"'{staticType.Name}' has no static method '{call.Method}' taking {args.Count} argument(s).");
            return FromEval(staticCall, call.Method);
        }

        if (target.Debuggee is not { IsNull: false } info)
        {
            if (target.Constant is { } constant && call.Method == "ToString" && call.Arguments.Count == 0)
            {
                return Operand.Of(ValueInspector.FormatPrimitive(constant, hex: false).Trim('"'));
            }

            throw new ExpressionException($"Cannot call '{call.Method}' on a null or constant value.");
        }

        var result = inspector.InvokeMethod(scope.Thread, info, call.Method, args)
            ?? throw new ExpressionException($"'{inspector.GetTypeName(info)}' has no method '{call.Method}' taking {args.Count} argument(s).");
        return FromEval(result, call.Method);
    }

    private Operand FromEval(EvalResult result, string what)
    {
        if (result.Error is not null)
        {
            throw new ExpressionException(result.Error);
        }

        if (result.IsException)
        {
            var type = result.Value is null ? "an exception" : inspector.GetTypeName(ValueInspector.Analyze(result.Value));
            throw new ExpressionException($"'{what}' threw {type}.");
        }

        // A finished evaluation with no value at all called a void method (a null reference
        // still comes back as a value).
        return result.Value is null ? Operand.Of(VoidResult.Instance) : Operand.Of(result.Value);
    }

    /// <summary>Materializes a constant in the debuggee so it can be passed as an argument.</summary>
    private unsafe ICorDebugValue ToDebuggee(Operand operand, IEvaluationScope scope)
    {
        if (operand.Debuggee is not null)
        {
            return operand.Debuggee.Raw;
        }

        switch (operand.Constant)
        {
            case string s:
                var created = inspector.Evaluator.NewString(scope.Thread, s);
                return created.Value ?? throw new ExpressionException(created.Error ?? "Could not create string.");
            case null:
                throw new ExpressionException("Passing null as an argument is not supported.");
            default:
                var (elementType, bits) = Encode(operand.Constant);
                var value = FuncEvaluator.CreatePrimitive(scope.Thread, elementType) as ICorDebugGenericValue
                    ?? throw new ExpressionException("Could not create argument value.");
                return value.SetValue(&bits) >= 0 ? value : throw new ExpressionException("Could not set argument value.");
        }
    }

    private static (CorElementType, ulong) Encode(object value) => value switch
    {
        bool b => (CorElementType.Boolean, b ? 1UL : 0UL),
        char c => (CorElementType.Char, c),
        int i => (CorElementType.I4, unchecked((uint)i)),
        long l => (CorElementType.I8, unchecked((ulong)l)),
        ulong u => (CorElementType.U8, u),
        float f => (CorElementType.R4, BitConverter.SingleToUInt32Bits(f)),
        double d => (CorElementType.R8, BitConverter.DoubleToUInt64Bits(d)),
        _ => throw new ExpressionException($"Cannot pass a {value.GetType().Name} to the debuggee."),
    };

    /// <summary>Reads a debuggee value into a local constant (primitives, strings, enums, null).</summary>
    private object? ToConstant(Operand operand)
    {
        if (operand.Type is { } type)
        {
            throw new ExpressionException($"'{type.Name}' is a type, not a value.");
        }

        if (operand.Constant is VoidResult)
        {
            throw new ExpressionException("The method returns no value.");
        }

        if (operand.Debuggee is not { } info)
        {
            return operand.Constant;
        }

        if (info.IsNull)
        {
            return null;
        }

        if (info.IsPrimitive)
        {
            return ValueInspector.ReadPrimitive(info.Target!, info.ElementType);
        }

        if (info.Target is ICorDebugStringValue text)
        {
            return text.GetStringValue();
        }

        if (inspector.TryReadBoxedPrimitive(info) is { } boxed)
        {
            return boxed;
        }

        // Enums compare by their underlying value.
        if (inspector.FindField(info, "value__") is { } underlying)
        {
            var raw = ValueInspector.Analyze(underlying);
            return ValueInspector.ReadPrimitive(raw.Target!, raw.ElementType);
        }

        // Other objects: identity (for ==) and display text (for string concatenation).
        var address = info.Target!.GetAddress(out var a) >= 0 ? a : 0;
        return new ObjectReference(address, inspector.Format(info));
    }

    /// <summary>A debuggee object reduced to what operators need.</summary>
    private sealed record ObjectReference(ulong Address, string Text);

    // ---- Operators --------------------------------------------------------------------

    private Operand EvalUnary(string op, Operand operand)
    {
        var value = ToConstant(operand);
        return op switch
        {
            "!" => Operand.Of(!ToBool(value)),
            "+" => Operand.Of(value),
            "-" => Operand.Of(Negate(value)),
            "~" when IsInteger(value) => Operand.Of(~Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            _ => throw new ExpressionException($"Operator '{op}' cannot be applied to this operand."),
        };
    }

    private Operand EvalBinary(BinaryExpr binary, IEvaluationScope scope)
    {
        switch (binary.Operator)
        {
            case "&&":
                return Operand.Of(ToBool(Eval(binary.Left, scope)) && ToBool(Eval(binary.Right, scope)));
            case "||":
                return Operand.Of(ToBool(Eval(binary.Left, scope)) || ToBool(Eval(binary.Right, scope)));
            case "??":
                var left = Eval(binary.Left, scope);
                return IsNull(left) ? Eval(binary.Right, scope) : left;
        }

        // Read each side into a local constant before evaluating the other side.
        var a = ToConstant(Eval(binary.Left, scope));
        var b = ToConstant(Eval(binary.Right, scope));

        if (binary.Operator is "==" or "!=")
        {
            var equal = AreEqual(a, b);
            return Operand.Of(binary.Operator == "==" ? equal : !equal);
        }

        if (binary.Operator == "+" && (a is string || b is string))
        {
            return Operand.Of(Stringify(a) + Stringify(b));
        }

        if (a is bool ba && b is bool bb)
        {
            return binary.Operator switch
            {
                "&" => Operand.Of(ba & bb),
                "|" => Operand.Of(ba | bb),
                "^" => Operand.Of(ba ^ bb),
                _ => throw new ExpressionException($"Operator '{binary.Operator}' cannot be applied to bool operands."),
            };
        }

        return Operand.Of(Arithmetic(binary.Operator, a, b));
    }

    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        ObjectReference reference => reference.Text,
        _ => ValueInspector.FormatPrimitive(value, hex: false),
    };

    private static bool IsNull(Operand operand) =>
        operand.Debuggee?.IsNull ?? operand.Constant is null;

    private static bool AreEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is ObjectReference ra && b is ObjectReference rb)
        {
            return ra.Address != 0 && ra.Address == rb.Address;
        }

        if (a is string sa && b is string sb)
        {
            return string.Equals(sa, sb, StringComparison.Ordinal);
        }

        if (IsNumeric(a) && IsNumeric(b))
        {
            return Compare(a, b) == 0;
        }

        return a.Equals(b);
    }

    private static object Arithmetic(string op, object? a, object? b)
    {
        if (!IsNumeric(a) || !IsNumeric(b))
        {
            throw new ExpressionException($"Operator '{op}' needs numeric operands.");
        }

        if (op is "<" or ">" or "<=" or ">=")
        {
            var comparison = Compare(a!, b!);
            return op switch
            {
                "<" => comparison < 0,
                ">" => comparison > 0,
                "<=" => comparison <= 0,
                _ => comparison >= 0,
            };
        }

        var culture = CultureInfo.InvariantCulture;
        if (a is decimal || b is decimal)
        {
            var x = Convert.ToDecimal(a, culture);
            var y = Convert.ToDecimal(b, culture);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => y == 0 ? throw new ExpressionException("Attempted to divide by zero.") : x / y,
                "%" => y == 0 ? throw new ExpressionException("Attempted to divide by zero.") : x % y,
                _ => throw new ExpressionException($"Operator '{op}' is not supported for decimal."),
            };
        }

        if (a is double or float || b is double or float)
        {
            var x = Convert.ToDouble(a, culture);
            var y = Convert.ToDouble(b, culture);
            return op switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => x / y,
                "%" => x % y,
                _ => throw new ExpressionException($"Operator '{op}' is not supported for floating point."),
            };
        }

        var l = Convert.ToInt64(a, culture);
        var r = Convert.ToInt64(b, culture);
        object result = op switch
        {
            "+" => l + r,
            "-" => l - r,
            "*" => l * r,
            "/" => r == 0 ? throw new ExpressionException("Attempted to divide by zero.") : l / r,
            "%" => r == 0 ? throw new ExpressionException("Attempted to divide by zero.") : l % r,
            "&" => l & r,
            "|" => l | r,
            "^" => l ^ r,
            "<<" => l << (int)r,
            ">>" => l >> (int)r,
            _ => throw new ExpressionException($"Unknown operator '{op}'."),
        };

        // Keep int results looking like C# ints.
        return result is long value && value is >= int.MinValue and <= int.MaxValue && a is not long && b is not long
            ? (int)value
            : result;
    }

    private static int Compare(object a, object b)
    {
        var culture = CultureInfo.InvariantCulture;
        if (a is double or float or decimal || b is double or float or decimal)
        {
            return Convert.ToDouble(a, culture).CompareTo(Convert.ToDouble(b, culture));
        }

        if (a is ulong || b is ulong)
        {
            return Convert.ToUInt64(a, culture).CompareTo(Convert.ToUInt64(b, culture));
        }

        return Convert.ToInt64(a, culture).CompareTo(Convert.ToInt64(b, culture));
    }

    private static object Negate(object? value) => value switch
    {
        int i => -i,
        long l => -l,
        float f => -f,
        double d => -d,
        decimal m => -m,
        sbyte or short or byte or ushort or uint => -Convert.ToInt64(value, CultureInfo.InvariantCulture),
        _ => throw new ExpressionException("Operator '-' needs a numeric operand."),
    };

    private bool ToBool(Operand operand) => ToBool(ToConstant(operand));

    private static bool ToBool(object? value) => value switch
    {
        bool b => b,
        null => throw new ExpressionException("Cannot convert null to bool."),
        _ => throw new ExpressionException($"Cannot convert '{value}' to bool."),
    };

    private static bool IsNumeric(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal or char or nint or nuint;

    private static bool IsInteger(object? value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or char;
}
