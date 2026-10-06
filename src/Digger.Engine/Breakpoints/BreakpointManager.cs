using System;
using System.Collections.Generic;
using System.Globalization;
using Digger.Engine.Infrastructure;
using Digger.Engine.Runtime;
using Digger.Engine.Symbols;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Breakpoints;

public enum BreakpointKind
{
    Source,
    Function,

    /// <summary>Engine-owned one-shot breakpoint (stop at entry, async step resume).</summary>
    Internal,
}

/// <summary>A source line breakpoint as requested by the client.</summary>
public sealed record SourceBreakpointSpec(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

/// <summary>A function breakpoint as requested by the client.</summary>
public sealed record FunctionBreakpointSpec(string Name, string? Condition = null, string? HitCondition = null);

/// <summary>A user (or internal) breakpoint and the IL locations it is bound to.</summary>
public sealed class UserBreakpoint
{
    internal UserBreakpoint(int id, BreakpointKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public int Id { get; }

    public BreakpointKind Kind { get; }

    public string? Path { get; init; }

    public int Line { get; set; }

    public string? FunctionName { get; init; }

    public string? Condition { get; set; }

    public string? HitCondition { get; set; }

    public string? LogMessage { get; set; }

    public int HitCount { get; set; }

    public bool IsVerified => Bound.Count > 0;

    public string? Message { get; set; }

    /// <summary>Where the breakpoint actually landed (may differ from the requested line).</summary>
    public SourceLocation? Location { get; set; }

    internal List<BoundBreakpoint> Bound { get; } = [];
}

internal readonly record struct BreakpointKey(ulong ModuleBase, uint MethodToken, uint ILOffset);

internal sealed record BoundBreakpoint(ICorDebugFunctionBreakpoint Native, BreakpointKey Key, LoadedModule Module);

/// <summary>
/// Owns all breakpoints: resolves source lines and function names to IL offsets in every
/// loaded module, keeps unbound breakpoints pending until a matching module loads, and
/// maps native breakpoint hits back to user breakpoints.
/// </summary>
public sealed class BreakpointManager(ModuleRegistry modules)
{
    private readonly Dictionary<string, List<UserBreakpoint>> _sourceBreakpoints = new(StringComparer.Ordinal);
    private readonly List<UserBreakpoint> _functionBreakpoints = [];
    private readonly List<UserBreakpoint> _internalBreakpoints = [];
    private readonly Dictionary<BreakpointKey, List<UserBreakpoint>> _byLocation = [];
    private int _nextId = 1;

    /// <summary>Replaces all breakpoints in <paramref name="path"/>. Unchanged lines keep their id and hit count.</summary>
    public List<UserBreakpoint> SetSourceBreakpoints(string path, IReadOnlyList<SourceBreakpointSpec> specs)
    {
        var previous = _sourceBreakpoints.GetValueOrDefault(path) ?? [];
        var result = new List<UserBreakpoint>(specs.Count);
        foreach (var spec in specs)
        {
            var existing = previous.Find(b => b.Line == spec.Line && !result.Contains(b));
            if (existing is not null)
            {
                existing.Condition = spec.Condition;
                existing.HitCondition = spec.HitCondition;
                existing.LogMessage = spec.LogMessage;
                result.Add(existing);
                continue;
            }

            var breakpoint = new UserBreakpoint(_nextId++, BreakpointKind.Source)
            {
                Path = path,
                Line = spec.Line,
                Condition = spec.Condition,
                HitCondition = spec.HitCondition,
                LogMessage = spec.LogMessage,
            };
            foreach (var module in modules.Snapshot())
            {
                TryBind(breakpoint, module);
            }

            if (!breakpoint.IsVerified)
            {
                breakpoint.Message = "The breakpoint is pending: no loaded module contains this source file yet.";
            }

            result.Add(breakpoint);
        }

        foreach (var removed in previous)
        {
            if (!result.Contains(removed))
            {
                Unbind(removed);
            }
        }

        _sourceBreakpoints[path] = result;
        return result;
    }

    public List<UserBreakpoint> SetFunctionBreakpoints(IReadOnlyList<FunctionBreakpointSpec> specs)
    {
        foreach (var old in _functionBreakpoints)
        {
            Unbind(old);
        }

        _functionBreakpoints.Clear();
        foreach (var spec in specs)
        {
            var breakpoint = new UserBreakpoint(_nextId++, BreakpointKind.Function)
            {
                FunctionName = spec.Name,
                Condition = spec.Condition,
                HitCondition = spec.HitCondition,
            };
            foreach (var module in modules.Snapshot())
            {
                TryBind(breakpoint, module);
            }

            if (!breakpoint.IsVerified)
            {
                breakpoint.Message = $"No loaded method matches '{spec.Name}' yet.";
            }

            _functionBreakpoints.Add(breakpoint);
        }

        return [.. _functionBreakpoints];
    }

    /// <summary>Binds pending breakpoints to a newly loaded module; returns those whose state changed.</summary>
    public List<UserBreakpoint> OnModuleLoaded(LoadedModule module)
    {
        var changed = new List<UserBreakpoint>();
        if (module.Metadata is null)
        {
            return changed;
        }

        foreach (var list in _sourceBreakpoints.Values)
        {
            foreach (var breakpoint in list)
            {
                var wasVerified = breakpoint.IsVerified;
                if (TryBind(breakpoint, module) && !wasVerified)
                {
                    breakpoint.Message = null;
                    changed.Add(breakpoint);
                }
            }
        }

        foreach (var breakpoint in _functionBreakpoints)
        {
            var wasVerified = breakpoint.IsVerified;
            if (TryBind(breakpoint, module) && !wasVerified)
            {
                breakpoint.Message = null;
                changed.Add(breakpoint);
            }
        }

        return changed;
    }

    public List<UserBreakpoint> OnModuleUnloaded(LoadedModule module)
    {
        var changed = new List<UserBreakpoint>();
        foreach (var breakpoint in AllBreakpoints())
        {
            if (breakpoint.Bound.RemoveAll(b => b.Module == module) > 0 && !breakpoint.IsVerified)
            {
                changed.Add(breakpoint);
            }
        }

        var stale = new List<BreakpointKey>();
        foreach (var key in _byLocation.Keys)
        {
            if (key.ModuleBase == module.BaseAddress)
            {
                stale.Add(key);
            }
        }

        foreach (var key in stale)
        {
            _ = _byLocation.Remove(key);
        }

        return changed;
    }

    /// <summary>Creates an internal one-shot breakpoint (e.g. stop-at-entry).</summary>
    public UserBreakpoint? AddInternal(LoadedModule module, uint methodToken, uint ilOffset)
    {
        var breakpoint = new UserBreakpoint(0, BreakpointKind.Internal);
        if (!Bind(breakpoint, module, methodToken, ilOffset))
        {
            return null;
        }

        _internalBreakpoints.Add(breakpoint);
        return breakpoint;
    }

    public void Remove(UserBreakpoint breakpoint)
    {
        Unbind(breakpoint);
        _ = _internalBreakpoints.Remove(breakpoint);
    }

    /// <summary>User breakpoints at the location of a native breakpoint hit.</summary>
    public IReadOnlyList<UserBreakpoint> Match(ICorDebugBreakpoint native)
    {
        if (native is not ICorDebugFunctionBreakpoint functionBreakpoint
            || functionBreakpoint.GetFunction(out var function) < 0
            || functionBreakpoint.GetOffset(out var offset) < 0
            || function.GetToken(out var token) < 0
            || function.GetModule(out var module) < 0
            || module.GetBaseAddress(out var baseAddress) < 0)
        {
            return [];
        }

        return _byLocation.GetValueOrDefault(new BreakpointKey(baseAddress, token, offset)) ?? (IReadOnlyList<UserBreakpoint>)[];
    }

    /// <summary>Deactivates every native breakpoint (used before detaching).</summary>
    public void DeactivateAll()
    {
        foreach (var breakpoint in AllBreakpoints())
        {
            foreach (var bound in breakpoint.Bound)
            {
                _ = bound.Native.Activate(false);
            }
        }
    }

    private IEnumerable<UserBreakpoint> AllBreakpoints()
    {
        foreach (var list in _sourceBreakpoints.Values)
        {
            foreach (var breakpoint in list)
            {
                yield return breakpoint;
            }
        }

        foreach (var breakpoint in _functionBreakpoints)
        {
            yield return breakpoint;
        }

        foreach (var breakpoint in _internalBreakpoints)
        {
            yield return breakpoint;
        }
    }

    private bool TryBind(UserBreakpoint breakpoint, LoadedModule module)
    {
        if (breakpoint.Bound.Exists(b => b.Module == module))
        {
            return false; // already bound here
        }

        var bound = false;
        if (breakpoint.Kind == BreakpointKind.Source && module.Symbols is { } symbols && breakpoint.Path is { } path)
        {
            if (symbols.ResolveBreakpoint(path, breakpoint.Line) is not { } resolution)
            {
                return false;
            }

            foreach (var target in resolution.Targets)
            {
                bound |= Bind(breakpoint, module, target.MethodToken, target.ILOffset);
            }

            if (bound)
            {
                breakpoint.Location = resolution.Location;
            }
        }
        else if (breakpoint.Kind == BreakpointKind.Function && module.Metadata is { } metadata && breakpoint.FunctionName is { } name)
        {
            foreach (var token in metadata.FindMethodsByName(name))
            {
                // Async methods: break in the state machine body, not the stub.
                var bodyToken = module.Symbols?.GetMoveNextMethod(token) ?? token;
                var offset = module.Symbols?.GetFirstUserOffset(bodyToken) ?? 0;
                if (Bind(breakpoint, module, bodyToken, offset))
                {
                    bound = true;
                    breakpoint.Location ??= module.Symbols?.GetLocation(bodyToken, offset);
                }
            }
        }

        return bound;
    }

    private bool Bind(UserBreakpoint breakpoint, LoadedModule module, uint methodToken, uint ilOffset)
    {
        if (module.Module.GetFunctionFromToken(methodToken, out var function) < 0
            || function.GetILCode(out var code) < 0)
        {
            return false;
        }

        var hr = code.CreateBreakpoint(ilOffset, out var native);
        if (hr < 0)
        {
            Log.Warn(string.Create(CultureInfo.InvariantCulture, $"CreateBreakpoint(0x{methodToken:X8}+{ilOffset}) failed: 0x{hr:X8}"));
            return false;
        }

        _ = native.Activate(true);
        var key = new BreakpointKey(module.BaseAddress, methodToken, ilOffset);
        breakpoint.Bound.Add(new BoundBreakpoint(native, key, module));
        if (!_byLocation.TryGetValue(key, out var list))
        {
            _byLocation[key] = list = [];
        }

        list.Add(breakpoint);
        return true;
    }

    private void Unbind(UserBreakpoint breakpoint)
    {
        foreach (var bound in breakpoint.Bound)
        {
            _ = bound.Native.Activate(false);
            if (_byLocation.TryGetValue(bound.Key, out var list))
            {
                _ = list.Remove(breakpoint);
                if (list.Count == 0)
                {
                    _ = _byLocation.Remove(bound.Key);
                }
            }
        }

        breakpoint.Bound.Clear();
    }
}
