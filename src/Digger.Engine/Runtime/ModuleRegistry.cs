using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Digger.Engine.Symbols;
using Digger.Interop.CorDebug;

namespace Digger.Engine.Runtime;

/// <summary>A module loaded in the debuggee together with its metadata and symbols.</summary>
public sealed class LoadedModule : IDisposable
{
    internal LoadedModule(ICorDebugModule module, ulong baseAddress, string path, ModuleMetadata? metadata)
    {
        Module = module;
        BaseAddress = baseAddress;
        Path = path;
        Metadata = metadata;
        Name = System.IO.Path.GetFileName(path);
        Id = baseAddress.ToString("X", CultureInfo.InvariantCulture);
    }

    public ICorDebugModule Module { get; }

    public ulong BaseAddress { get; }

    public string Id { get; }

    public string Name { get; }

    public string Path { get; }

    public ModuleMetadata? Metadata { get; }

    public SymbolReader? Symbols => Metadata?.Symbols;

    /// <summary>"Just My Code": modules with symbols and no optimizations.</summary>
    public bool IsUserCode { get; internal set; }

    public void Dispose() => Metadata?.Dispose();
}

/// <summary>
/// Loaded modules keyed by base address. Thread-safe: modules can be registered from the
/// callback thread while the engine thread is blocked in a function evaluation.
/// </summary>
public sealed class ModuleRegistry : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, LoadedModule> _modules = [];

    public LoadedModule? Find(ulong baseAddress)
    {
        lock (_gate)
        {
            return _modules.GetValueOrDefault(baseAddress);
        }
    }

    public LoadedModule? Find(ICorDebugModule module) =>
        module.GetBaseAddress(out var address) >= 0 ? Find(address) : null;

    public LoadedModule? FindByPath(string path)
    {
        lock (_gate)
        {
            foreach (var module in _modules.Values)
            {
                if (string.Equals(module.Path, path, StringComparison.Ordinal))
                {
                    return module;
                }
            }

            return null;
        }
    }

    public List<LoadedModule> Snapshot()
    {
        lock (_gate)
        {
            return [.. _modules.Values];
        }
    }

    internal LoadedModule Register(ICorDebugModule module, bool justMyCode)
    {
        _ = module.GetBaseAddress(out var baseAddress);
        lock (_gate)
        {
            if (_modules.TryGetValue(baseAddress, out var existing))
            {
                return existing;
            }
        }

        var path = module.GetNameString() ?? string.Empty;
        _ = module.IsDynamic(out var isDynamic);
        _ = module.IsInMemory(out var isInMemory);
        var metadata = isDynamic || isInMemory || !File.Exists(path)
            ? null
            : ModuleMetadata.TryOpen(path, loadSymbols: true);

        var loaded = new LoadedModule(module, baseAddress, path, metadata)
        {
            IsUserCode = metadata?.Symbols is not null && (!justMyCode || !metadata.IsOptimized),
        };

        lock (_gate)
        {
            if (_modules.TryGetValue(baseAddress, out var raced))
            {
                loaded.Dispose();
                return raced;
            }

            _modules[baseAddress] = loaded;
        }

        return loaded;
    }

    internal LoadedModule? Unregister(ICorDebugModule module)
    {
        _ = module.GetBaseAddress(out var baseAddress);
        lock (_gate)
        {
            return _modules.Remove(baseAddress, out var removed) ? removed : null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var module in _modules.Values)
            {
                module.Dispose();
            }

            _modules.Clear();
        }
    }
}
