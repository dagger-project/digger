using System;
using System.Collections.Generic;
using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Inspection;

/// <summary>A (non-generic) type named in an expression, whose static members can be used.</summary>
public sealed record StaticType(LoadedModule Module, uint Token, ICorDebugClass Class)
{
    public string Name => Module.Metadata!.GetTypeName(Token);

    internal string MetadataName => Module.Metadata!.GetMetadataName(Token);
}

/// <content>Types by name and their static members: <c>Program.Add(1, 2)</c>, <c>DateTime.Now</c>.</content>
public sealed partial class ValueInspector
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["object"] = "System.Object",
        ["string"] = "System.String",
        ["bool"] = "System.Boolean",
        ["char"] = "System.Char",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["decimal"] = "System.Decimal",
    };

    /// <summary>
    /// Resolves a type name as C# code in <paramref name="context"/> would: nested in the
    /// context type (or its outer types), in its namespace or a parent one, global, or in
    /// <c>System</c>. Only loaded assemblies are searched.
    /// </summary>
    public StaticType? ResolveTypeName(string name, StaticType? context)
    {
        if (Keywords.TryGetValue(name, out var keyword))
        {
            return FindType(keyword);
        }

        if (context is not null)
        {
            var metadata = context.Module.Metadata!;
            for (var token = context.Token; token != 0; token = metadata.GetEnclosingType(token))
            {
                if (FindTypeIn(context.Module, metadata.GetMetadataName(token) + "+" + name) is { } nested)
                {
                    return nested;
                }
            }

            var ns = metadata.GetNamespace(context.Token);
            while (ns.Length > 0)
            {
                if (FindType(ns + "." + name) is { } inNamespace)
                {
                    return inNamespace;
                }

                var dot = ns.LastIndexOf('.');
                ns = dot < 0 ? "" : ns[..dot];
            }
        }

        return FindType(name) ?? FindType("System." + name);
    }

    /// <summary>A type nested in <paramref name="outer"/>.</summary>
    public static StaticType? FindNestedType(StaticType outer, string name) => FindTypeIn(outer.Module, outer.MetadataName + "+" + name);

    /// <summary>A type by metadata name (<c>Namespace.Outer+Inner</c>) in any loaded module.</summary>
    public StaticType? FindType(string metadataName)
    {
        foreach (var module in modules.Snapshot())
        {
            if (FindTypeIn(module, metadataName) is { } type)
            {
                return type;
            }
        }

        return null;
    }

    private static StaticType? FindTypeIn(LoadedModule module, string metadataName)
    {
        if (module.Metadata is not { } metadata || metadata.FindType(metadataName) is not (not 0 and var token)
            || metadata.IsGenericType(token) || module.Module.GetClassFromToken(token, out var cls) < 0)
        {
            return null;
        }

        return new StaticType(module, token, cls);
    }

    /// <summary>A static field (or auto-property backing field) of the type.</summary>
    public static ICorDebugValue? GetStaticField(StaticType type, string name, ICorDebugThread thread)
    {
        var backingField = "<" + name + ">k__BackingField";
        foreach (var field in type.Module.Metadata!.GetFields(type.Token))
        {
            if (field.IsStatic && (field.Name == name || field.Name == backingField))
            {
                // Literals (const) have no storage; the frame matters only for thread statics.
                _ = thread.GetActiveFrame(out var frame);
                return !field.IsLiteral && type.Class.GetStaticFieldValue(field.Token, frame, out var value) >= 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>Calls a static property getter; null when the type has no such property.</summary>
    public EvalResult? InvokeStaticProperty(ICorDebugThread thread, StaticType type, string name)
    {
        foreach (var property in type.Module.Metadata!.GetProperties(type.Token))
        {
            if (property.IsStatic && property.Name == name)
            {
                return CallStatic(thread, type, property.GetterToken, []);
            }
        }

        return null;
    }

    /// <summary>Calls a static method by name and arity; null when the type has none.</summary>
    public EvalResult? InvokeStaticMethod(ICorDebugThread thread, StaticType type, string name, IReadOnlyList<ICorDebugValue> args)
    {
        var token = PickOverload(type.Module.Metadata!, type.Token, name, args, isStatic: true);
        return token == 0 ? null : CallStatic(thread, type, token, args);
    }

    /// <summary>
    /// The overload of <paramref name="name"/> whose parameters best fit the arguments: exact
    /// element types beat object/reference parameters; a primitive of another type does not
    /// fit (func-eval does not convert). Falls back to the first one with the right arity.
    /// </summary>
    internal static uint PickOverload(Symbols.ModuleMetadata metadata, uint typeToken, string name, IReadOnlyList<ICorDebugValue> args, bool isStatic)
    {
        uint best = 0;
        uint first = 0;
        var bestScore = -1;
        foreach (var method in metadata.GetMethods(typeToken))
        {
            if (method.IsStatic != isStatic || method.Name != name || method.ParameterCount != args.Count)
            {
                continue;
            }

            first = first == 0 ? method.Token : first;
            var score = Fit(metadata.GetParameterElementTypes(method.Token), args);
            if (score > bestScore)
            {
                (best, bestScore) = (method.Token, score);
            }
        }

        return best != 0 ? best : args.Count == 0 ? first : 0;
    }

    private static int Fit(byte[] parameters, IReadOnlyList<ICorDebugValue> args)
    {
        const byte String = 0x0E;
        const byte Object = 0x1C;
        const byte TypeParameter = 0x13; // the class's T: Dictionary<string, int>.get_Item(TKey)
        const byte SzArray = 0x1D;
        const byte MethodTypeParameter = 0x1E;
        var score = 0;
        for (var i = 0; i < parameters.Length && i < args.Count; i++)
        {
            var parameter = parameters[i];
            var argument = (byte)Analyze(args[i]).ElementType;
            if (parameter == argument)
            {
                score += 3;
            }
            else if (parameter is Object or TypeParameter)
            {
                score += 1;
            }
            else if (IsPrimitiveOrString(parameter) || IsPrimitiveOrString(argument) || parameter is SzArray or MethodTypeParameter)
            {
                return -1; // no conversions, no arrays built from arguments, no generic methods
            }
            else
            {
                score += 1; // classes, structs, generics: let the runtime check
            }
        }

        return score;

        static bool IsPrimitiveOrString(byte type) => type is (>= 0x02 and <= String) or 0x18 or 0x19;
    }

    private EvalResult CallStatic(ICorDebugThread thread, StaticType type, uint methodToken, IReadOnlyList<ICorDebugValue> args) =>
        type.Module.Module.GetFunctionFromToken(methodToken, out var function) < 0
            ? EvalResult.Failure("Method not found in the debuggee.")
            : evaluator.Call(thread, function, [], args);
}
