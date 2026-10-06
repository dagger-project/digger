using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Digger.Engine.Infrastructure;

namespace Digger.Engine.Symbols;

/// <summary>An instance or static field declared on a type.</summary>
public readonly record struct FieldInfo(uint Token, string Name, bool IsStatic, bool IsLiteral, bool IsBrowsable);

/// <summary>A method declared on a type.</summary>
public readonly record struct MethodInfo(uint Token, string Name, bool IsStatic, bool IsPublic, bool IsSpecialName, int ParameterCount);

/// <summary>A parameterless property getter declared on a type.</summary>
public readonly record struct PropertyInfo(string Name, uint GetterToken, bool IsStatic, bool IsBrowsable, bool IsPublic);

/// <summary>
/// Read-only view of a module's ECMA-335 metadata (and its portable PDB, when present),
/// opened directly from disk. Replaces IMetaDataImport, which is not needed for anything
/// System.Reflection.Metadata cannot answer.
/// </summary>
public sealed class ModuleMetadata : IDisposable
{
    private readonly PEReader _peReader;
    private readonly Dictionary<uint, string> _typeNames = [];
    private readonly Dictionary<uint, FieldInfo[]> _fields = [];
    private readonly Dictionary<uint, PropertyInfo[]> _properties = [];
    private readonly Dictionary<uint, string?> _debuggerDisplay = [];
    private readonly Dictionary<uint, MethodInfo[]> _methods = [];
    private readonly Dictionary<uint, bool> _enumerable = [];
    private Dictionary<string, uint>? _typesByName;

    private ModuleMetadata(string path, PEReader peReader, SymbolReader? symbols)
    {
        Path = path;
        _peReader = peReader;
        Reader = peReader.GetMetadataReader();
        Symbols = symbols;
        IsOptimized = DetectOptimized();
    }

    public string Path { get; }

    public MetadataReader Reader { get; }

    public SymbolReader? Symbols { get; private set; }

    /// <summary>The CodeView record that identifies this module's PDB on a symbol server.</summary>
    internal (string PdbName, string Key)? SymbolServerKey
    {
        get
        {
            foreach (var entry in _peReader.ReadDebugDirectory())
            {
                if (entry.Type != DebugDirectoryEntryType.CodeView)
                {
                    continue;
                }

                var codeView = _peReader.ReadCodeViewDebugDirectoryData(entry);
                var isPortable = entry.MinorVersion == 0x504D;
                var age = isPortable ? "FFFFFFFF" : codeView.Age.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
                var name = System.IO.Path.GetFileName(codeView.Path.Replace('\\', '/')).ToLowerInvariant();
                return (name, codeView.Guid.ToString("N") + age);
            }

            return null;
        }
    }

    /// <summary>Attaches symbols found after load (symbol server download).</summary>
    internal bool AttachSymbols(string pdbPath)
    {
        if (Symbols is not null)
        {
            return false;
        }

        Symbols = SymbolReader.TryOpenFile(pdbPath);
        return Symbols is not null;
    }

    /// <summary>True when the assembly was compiled with optimizations (no DebuggableAttribute.DisableOptimizations).</summary>
    public bool IsOptimized { get; }

    /// <summary>Entry point MethodDef token, or 0.</summary>
    public uint EntryPointToken
    {
        get
        {
            var corHeader = _peReader.PEHeaders.CorHeader;
            if (corHeader is null || (corHeader.Flags & CorFlags.NativeEntryPoint) != 0)
            {
                return 0;
            }

            var token = (uint)corHeader.EntryPointTokenOrRelativeVirtualAddress;
            return (token >> 24) == 0x06 ? token : 0;
        }
    }

    /// <summary>Opens <paramref name="path"/>; returns null for missing files or non-managed images.</summary>
    public static ModuleMetadata? TryOpen(string path, bool loadSymbols)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        PEReader? peReader = null;
        try
        {
            peReader = new PEReader(File.OpenRead(path), PEStreamOptions.Default);
            if (!peReader.HasMetadata)
            {
                peReader.Dispose();
                return null;
            }

            var symbols = loadSymbols ? SymbolReader.TryOpen(peReader, path) : null;
            var result = new ModuleMetadata(path, peReader, symbols);
            peReader = null;
            return result;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot read metadata for '{path}': {ex.Message}");
            return null;
        }
        finally
        {
            peReader?.Dispose();
        }
    }

    // ---- Names ------------------------------------------------------------------------

    /// <summary>Full display name of a TypeDef, e.g. <c>MyApp.Outer.Inner</c> (generic arity stripped).</summary>
    public string GetTypeName(uint typeDefToken)
    {
        if (_typeNames.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        var handle = MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken));
        var definition = Reader.GetTypeDefinition(handle);
        var name = StripArity(Reader.GetString(definition.Name));
        string result;
        if (definition.IsNested)
        {
            result = GetTypeName((uint)MetadataTokens.GetToken(definition.GetDeclaringType())) + "." + name;
        }
        else
        {
            var ns = Reader.GetString(definition.Namespace);
            result = ns.Length == 0 ? name : ns + "." + name;
        }

        _typeNames[typeDefToken] = result;
        return result;
    }

    /// <summary>Name of the type that declares <paramref name="methodToken"/>.</summary>
    public uint GetDeclaringType(uint methodToken) =>
        (uint)MetadataTokens.GetToken(GetMethod(methodToken).GetDeclaringType());

    public string GetMethodName(uint methodToken) => Reader.GetString(GetMethod(methodToken).Name);

    /// <summary>
    /// Human readable frame name: async state machines are shown as their kickoff method and
    /// compiler-generated lambda/local-function names are untangled.
    /// </summary>
    public string GetMethodDisplayName(uint methodToken)
    {
        if (Symbols?.GetKickoffMethod(methodToken) is { } kickoff)
        {
            methodToken = kickoff;
        }

        var typeName = PrettifyTypeName(GetTypeName(GetDeclaringType(methodToken)));
        return typeName + "." + PrettifyMethodName(GetMethodName(methodToken));
    }

    public bool IsStatic(uint methodToken) => (GetMethod(methodToken).Attributes & MethodAttributes.Static) != 0;

    /// <summary>Number of declared parameters (excluding <c>this</c>).</summary>
    public int GetParameterCount(uint methodToken)
    {
        var blob = Reader.GetBlobReader(GetMethod(methodToken).Signature);
        var header = blob.ReadSignatureHeader();
        if (header.IsGeneric)
        {
            _ = blob.ReadCompressedInteger();
        }

        return blob.ReadCompressedInteger();
    }

    /// <summary>Parameter names indexed by position (0 = first declared parameter).</summary>
    public string[] GetParameterNames(uint methodToken)
    {
        var method = GetMethod(methodToken);
        var names = new string[GetParameterCount(methodToken)];
        foreach (var handle in method.GetParameters())
        {
            var parameter = Reader.GetParameter(handle);
            var index = parameter.SequenceNumber - 1;
            if (index >= 0 && index < names.Length)
            {
                names[index] = Reader.GetString(parameter.Name);
            }
        }

        for (var i = 0; i < names.Length; i++)
        {
            names[i] ??= "arg" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return names;
    }

    // ---- Members ----------------------------------------------------------------------

    public FieldInfo[] GetFields(uint typeDefToken)
    {
        if (_fields.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        var result = new List<FieldInfo>();
        foreach (var handle in type.GetFields())
        {
            var field = Reader.GetFieldDefinition(handle);
            var attributes = field.Attributes;
            result.Add(new FieldInfo(
                (uint)MetadataTokens.GetToken(handle),
                Reader.GetString(field.Name),
                (attributes & FieldAttributes.Static) != 0,
                (attributes & FieldAttributes.Literal) != 0,
                IsBrowsable(field.GetCustomAttributes())));
        }

        return _fields[typeDefToken] = [.. result];
    }

    public PropertyInfo[] GetProperties(uint typeDefToken)
    {
        if (_properties.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        var result = new List<PropertyInfo>();
        foreach (var handle in type.GetProperties())
        {
            var property = Reader.GetPropertyDefinition(handle);
            var getter = property.GetAccessors().Getter;
            if (getter.IsNil)
            {
                continue;
            }

            var getterToken = (uint)MetadataTokens.GetToken(getter);
            if (GetParameterCount(getterToken) != 0)
            {
                continue; // indexer
            }

            var getterAttributes = Reader.GetMethodDefinition(getter).Attributes;
            result.Add(new PropertyInfo(
                Reader.GetString(property.Name),
                getterToken,
                (getterAttributes & MethodAttributes.Static) != 0,
                IsBrowsable(property.GetCustomAttributes()),
                (getterAttributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public));
        }

        return _properties[typeDefToken] = [.. result];
    }

    public MethodInfo[] GetMethods(uint typeDefToken)
    {
        if (_methods.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        var result = new List<MethodInfo>();
        foreach (var handle in type.GetMethods())
        {
            var method = Reader.GetMethodDefinition(handle);
            var token = (uint)MetadataTokens.GetToken(handle);
            var attributes = method.Attributes;
            result.Add(new MethodInfo(
                token,
                Reader.GetString(method.Name),
                (attributes & MethodAttributes.Static) != 0,
                (attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
                (attributes & MethodAttributes.SpecialName) != 0,
                GetParameterCount(token)));
        }

        return _methods[typeDefToken] = [.. result];
    }

    /// <summary>Finds a method declared on <paramref name="typeDefToken"/> by name and arity.</summary>
    public uint FindMethod(uint typeDefToken, string name, int parameterCount)
    {
        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        foreach (var handle in type.GetMethods())
        {
            var method = Reader.GetMethodDefinition(handle);
            if (Reader.StringComparer.Equals(method.Name, name))
            {
                var token = (uint)MetadataTokens.GetToken(handle);
                if (GetParameterCount(token) == parameterCount)
                {
                    return token;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolves a function breakpoint name such as <c>Main</c>, <c>Program.Main</c> or
    /// <c>MyApp.Program.Main</c> to all matching MethodDef tokens.
    /// </summary>
    public List<uint> FindMethodsByName(string qualifiedName)
    {
        var results = new List<uint>();
        var paren = qualifiedName.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            qualifiedName = qualifiedName[..paren];
        }

        var dot = qualifiedName.LastIndexOf('.');
        var methodName = dot < 0 ? qualifiedName : qualifiedName[(dot + 1)..];
        var typeName = dot < 0 ? null : qualifiedName[..dot];

        foreach (var handle in Reader.MethodDefinitions)
        {
            var method = Reader.GetMethodDefinition(handle);
            if (!Reader.StringComparer.Equals(method.Name, methodName))
            {
                continue;
            }

            if (typeName is not null)
            {
                var declaring = GetTypeName((uint)MetadataTokens.GetToken(method.GetDeclaringType()));
                if (!declaring.Equals(typeName, StringComparison.Ordinal)
                    && !declaring.EndsWith("." + typeName, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            results.Add((uint)MetadataTokens.GetToken(handle));
        }

        return results;
    }

    /// <summary>Enum member names keyed by their underlying value.</summary>
    public List<(ulong Value, string Name)> GetEnumMembers(uint typeDefToken)
    {
        var result = new List<(ulong, string)>();
        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        foreach (var handle in type.GetFields())
        {
            var field = Reader.GetFieldDefinition(handle);
            if ((field.Attributes & FieldAttributes.Literal) == 0)
            {
                continue;
            }

            var constantHandle = field.GetDefaultValue();
            if (constantHandle.IsNil)
            {
                continue;
            }

            var constant = Reader.GetConstant(constantHandle);
            var blob = Reader.GetBlobReader(constant.Value);
            ulong value = constant.TypeCode switch
            {
                ConstantTypeCode.SByte => (ulong)blob.ReadSByte(),
                ConstantTypeCode.Byte => blob.ReadByte(),
                ConstantTypeCode.Int16 => (ulong)blob.ReadInt16(),
                ConstantTypeCode.UInt16 => blob.ReadUInt16(),
                ConstantTypeCode.Int32 => (ulong)blob.ReadInt32(),
                ConstantTypeCode.UInt32 => blob.ReadUInt32(),
                ConstantTypeCode.Int64 => (ulong)blob.ReadInt64(),
                ConstantTypeCode.UInt64 => blob.ReadUInt64(),
                ConstantTypeCode.Char => blob.ReadChar(),
                ConstantTypeCode.Boolean => blob.ReadBoolean() ? 1UL : 0UL,
                _ => 0,
            };
            result.Add((value, Reader.GetString(field.Name)));
        }

        return result;
    }

    /// <summary>The format string of <c>[DebuggerDisplay]</c> on the type itself, if any.</summary>
    public string? GetDebuggerDisplay(uint typeDefToken)
    {
        if (_debuggerDisplay.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        string? format = null;
        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        foreach (var handle in type.GetCustomAttributes())
        {
            var attribute = Reader.GetCustomAttribute(handle);
            if (!IsAttributeType(attribute, "System.Diagnostics", "DebuggerDisplayAttribute"))
            {
                continue;
            }

            var blob = Reader.GetBlobReader(attribute.Value);
            if (blob.Length > 2 && blob.ReadUInt16() == 1)
            {
                format = blob.ReadSerializedString();
            }

            break;
        }

        return _debuggerDisplay[typeDefToken] = format;
    }

    /// <summary>
    /// Whether the type declares <c>System.Collections.IEnumerable</c>. C# lists the whole
    /// interface closure on each type, so this also catches <c>IEnumerable&lt;T&gt;</c> implementers.
    /// </summary>
    public bool DeclaresEnumerable(uint typeDefToken)
    {
        if (_enumerable.TryGetValue(typeDefToken, out var cached))
        {
            return cached;
        }

        var result = false;
        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        foreach (var handle in type.GetInterfaceImplementations())
        {
            var implemented = Reader.GetInterfaceImplementation(handle).Interface;
            var (ns, name) = implemented.Kind switch
            {
                HandleKind.TypeReference => Reader.GetTypeReference((TypeReferenceHandle)implemented) is var r ? (r.Namespace, r.Name) : default,
                HandleKind.TypeDefinition => Reader.GetTypeDefinition((TypeDefinitionHandle)implemented) is var d ? (d.Namespace, d.Name) : default,
                _ => default((StringHandle, StringHandle)),
            };
            if (!name.IsNil && Reader.StringComparer.Equals(name, "IEnumerable") && Reader.StringComparer.Equals(ns, "System.Collections"))
            {
                result = true;
                break;
            }
        }

        return _enumerable[typeDefToken] = result;
    }

    /// <summary>
    /// Finds a TypeDef by its exact metadata name: <c>Namespace.Name</c>, with generic arity
    /// (<c>List`1</c>) and nested types as <c>Outer+Inner</c>.
    /// </summary>
    public uint FindType(string metadataName)
    {
        if (_typesByName is null)
        {
            _typesByName = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var handle in Reader.TypeDefinitions)
            {
                _ = _typesByName.TryAdd(MetadataName(handle), (uint)MetadataTokens.GetToken(handle));
            }
        }

        return _typesByName.GetValueOrDefault(metadataName);

        string MetadataName(TypeDefinitionHandle handle)
        {
            var definition = Reader.GetTypeDefinition(handle);
            var name = Reader.GetString(definition.Name);
            if (definition.IsNested)
            {
                return MetadataName(definition.GetDeclaringType()) + "+" + name;
            }

            var ns = Reader.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
    }

    public bool IsFlagsEnum(uint typeDefToken)
    {
        var type = Reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(RowOf(typeDefToken)));
        return HasAttribute(type.GetCustomAttributes(), "System", "FlagsAttribute");
    }

    // ---- Helpers ----------------------------------------------------------------------

    private MethodDefinition GetMethod(uint token) =>
        Reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(RowOf(token)));

    private static int RowOf(uint token) => (int)(token & 0x00FFFFFF);

    private bool IsBrowsable(CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var attribute = Reader.GetCustomAttribute(handle);
            if (!IsAttributeType(attribute, "System.Diagnostics", "DebuggerBrowsableAttribute"))
            {
                continue;
            }

            // DebuggerBrowsableState.Never == 0: blob is prolog (2 bytes) + int32.
            var blob = Reader.GetBlobReader(attribute.Value);
            if (blob.Length >= 6 && blob.ReadUInt16() == 1 && blob.ReadInt32() == 0)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasAttribute(CustomAttributeHandleCollection attributes, string ns, string name)
    {
        foreach (var handle in attributes)
        {
            if (IsAttributeType(Reader.GetCustomAttribute(handle), ns, name))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAttributeType(CustomAttribute attribute, string ns, string name)
    {
        EntityHandle typeHandle;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            typeHandle = Reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        }
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
        {
            typeHandle = Reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        }
        else
        {
            return false;
        }

        var (typeNamespace, typeName) = typeHandle.Kind switch
        {
            HandleKind.TypeReference => Reader.GetTypeReference((TypeReferenceHandle)typeHandle) is var r ? (r.Namespace, r.Name) : default,
            HandleKind.TypeDefinition => Reader.GetTypeDefinition((TypeDefinitionHandle)typeHandle) is var d ? (d.Namespace, d.Name) : default,
            _ => default((StringHandle, StringHandle)),
        };

        return !typeName.IsNil
            && Reader.StringComparer.Equals(typeName, name)
            && Reader.StringComparer.Equals(typeNamespace, ns);
    }

    private bool DetectOptimized()
    {
        // [assembly: Debuggable(DebuggingModes.DisableOptimizations ...)] marks debug builds.
        if (!Reader.IsAssembly)
        {
            return true;
        }

        foreach (var handle in Reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = Reader.GetCustomAttribute(handle);
            if (!IsAttributeType(attribute, "System.Diagnostics", "DebuggableAttribute"))
            {
                continue;
            }

            var blob = Reader.GetBlobReader(attribute.Value);
            if (blob.Length >= 6 && blob.ReadUInt16() == 1)
            {
                // Either (DebuggingModes) or (bool isJITTrackingEnabled, bool isJITOptimizerDisabled).
                if (blob.RemainingBytes >= 4 + 2)
                {
                    var modes = blob.ReadInt32();
                    return (modes & 0x100) == 0; // DisableOptimizations
                }

                if (blob.RemainingBytes >= 2 + 2)
                {
                    _ = blob.ReadBoolean();
                    return !blob.ReadBoolean();
                }
            }
        }

        return true;
    }

    internal static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>Removes closure/display-class segments (<c>&lt;&gt;c</c>, <c>&lt;&gt;c__DisplayClass0_0</c>, state machines).</summary>
    internal static string PrettifyTypeName(string typeName)
    {
        var parts = typeName.Split('.');
        var kept = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.StartsWith("<>c", StringComparison.Ordinal))
            {
                continue;
            }

            // <Main>d__0 (state machine) -> dropped; the kickoff method carries the name.
            if (part.StartsWith('<') && part.Contains(">d__", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(part);
        }

        return string.Join('.', kept);
    }

    /// <summary><c>&lt;Main&gt;b__0_0</c> → <c>Main.AnonymousMethod__0_0</c>, <c>&lt;Main&gt;g__Local|0_0</c> → <c>Main.Local</c>.</summary>
    internal static string PrettifyMethodName(string methodName)
    {
        if (!methodName.StartsWith('<'))
        {
            return methodName;
        }

        var close = methodName.IndexOf('>', StringComparison.Ordinal);
        if (close <= 1 || close + 2 > methodName.Length)
        {
            return methodName;
        }

        var owner = methodName[1..close];
        var kind = methodName[close + 1];
        var rest = methodName[(close + 2)..];
        return kind switch
        {
            'b' => $"{owner}.AnonymousMethod{rest}",
            'g' => $"{owner}.{(rest.StartsWith("__", StringComparison.Ordinal) ? rest[2..].Split('|')[0] : rest)}",
            _ => methodName,
        };
    }

    public void Dispose()
    {
        Symbols?.Dispose();
        _peReader.Dispose();
    }
}
