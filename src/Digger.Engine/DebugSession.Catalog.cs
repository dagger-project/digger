using System;
using System.Collections.Generic;
using Digger.Engine.Inspection;
using Digger.Engine.Runtime;
using Digger.Interop.CorDebug;

namespace Digger.Engine;

/// <content>What the loaded modules define: functions, types and static variables (Delve's funcs, types and vars).</content>
public sealed partial class DebugSession
{
    /// <summary>Methods as <c>Namespace.Type.Method</c>, sorted; compiler-generated ones are left out.</summary>
    public List<string> FindFunctions(Func<string, bool> filter, bool userCodeOnly)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var module in CatalogModules(userCodeOnly))
        {
            var metadata = module.Metadata!;
            foreach (var type in metadata.GetTypeTokens())
            {
                var typeName = metadata.GetTypeName(type);
                foreach (var method in metadata.GetMethods(type))
                {
                    var name = typeName + "." + method.Name;
                    if (!method.Name.Contains('<', StringComparison.Ordinal) && filter(name))
                    {
                        _ = result.Add(name);
                    }
                }
            }
        }

        return [.. result];
    }

    /// <summary>Type names, sorted; compiler-generated ones are left out.</summary>
    public List<string> FindTypes(Func<string, bool> filter, bool userCodeOnly)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var module in CatalogModules(userCodeOnly))
        {
            foreach (var type in module.Metadata!.GetTypeTokens())
            {
                var name = module.Metadata.GetTypeName(type);
                if (filter(name))
                {
                    _ = result.Add(name);
                }
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Static fields of the types in user code, named <c>Type.field</c>, with their values.
    /// Statics of types that have not been initialized yet read as "not initialized".
    /// </summary>
    public List<VariableView> GetStaticVariables(Func<string, bool> filter, bool hex)
    {
        RequireStopped();
        var frames = GetFrames(_lastStoppedThreadId);
        var frame = frames.Count > 0 ? frames[0] : null;
        var context = new EvalContext(TryGetThread(_lastStoppedThreadId), frame);
        var result = new List<VariableView>();
        foreach (var module in CatalogModules(userCodeOnly: true))
        {
            var metadata = module.Metadata!;
            foreach (var type in metadata.GetTypeTokens())
            {
                if (metadata.IsGenericType(type))
                {
                    continue;
                }

                var typeName = metadata.GetTypeName(type);
                ICorDebugClass? cls = null;
                foreach (var field in metadata.GetFields(type))
                {
                    var (fieldName, kind) = ValueInspector.DisplayNameForField(field.Name);
                    var name = typeName + "." + fieldName;
                    if (!field.IsStatic || field.IsLiteral || fieldName is null || !filter(name))
                    {
                        continue;
                    }

                    if (cls is null && module.Module.GetClassFromToken(type, out cls) < 0)
                    {
                        break;
                    }

                    if (cls.GetStaticFieldValue(field.Token, frame?.Frame, out var value) < 0 || value is null)
                    {
                        result.Add(new VariableView(name, "<not initialized>") { IsReadOnly = true, Kind = VariableKind.Error });
                        continue;
                    }

                    var (view, display) = ToView(name, ValueInspector.Analyze(value), hex, context, name, kind);
                    result.Add(display is null ? view : view with { Value = EvaluateDisplay(display, view.Value) });
                }
            }
        }

        return result;
    }

    private IEnumerable<LoadedModule> CatalogModules(bool userCodeOnly)
    {
        foreach (var module in _modules.Snapshot())
        {
            if (module.Metadata is not null && (!userCodeOnly || module.IsUserCode))
            {
                yield return module;
            }
        }
    }
}
