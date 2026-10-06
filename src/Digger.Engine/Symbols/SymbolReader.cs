using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Digger.Engine.Infrastructure;

namespace Digger.Engine.Symbols;

/// <summary>A source location for an IL offset. Lines and columns are 1-based.</summary>
public readonly record struct SourceLocation(string Document, int StartLine, int StartColumn, int EndLine, int EndColumn);

/// <summary>A place to set an IL breakpoint for a requested source line.</summary>
public readonly record struct BreakpointTarget(uint MethodToken, uint ILOffset);

/// <summary>Result of mapping a source line to IL.</summary>
public sealed record BreakpointResolution(SourceLocation Location, IReadOnlyList<BreakpointTarget> Targets);

/// <summary>An <c>await</c> in an async method: where it yields and where it resumes.</summary>
public readonly record struct AwaitPoint(uint YieldOffset, uint ResumeOffset, uint ResumeMethodToken);

/// <summary>A named IL local in scope at some offset.</summary>
public readonly record struct LocalInfo(int Index, string Name);

/// <summary>
/// Portable PDB reader: sequence points, local scopes and async state machine info.
/// Windows (native) PDBs are not supported.
/// </summary>
public sealed class SymbolReader : IDisposable
{
    private readonly MetadataReaderProvider _provider;
    private readonly MetadataReader _pdb;
    private readonly Dictionary<uint, SequencePoint[]> _sequencePoints = [];
    private Dictionary<DocumentHandle, List<(uint Method, SequencePoint Point)>>? _byDocument;
    private Dictionary<uint, uint>? _kickoffToMoveNext;
    private readonly Dictionary<uint, AwaitPoint[]> _awaitPoints = [];

    // Portable PDB CustomDebugInformation kinds.
    private static readonly Guid AsyncMethodSteppingInformation = new("54FD2AC5-E925-401A-9C2A-F94F171072F8");
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    private List<(string Pattern, string Url)>? _sourceLink;

    private SymbolReader(MetadataReaderProvider provider, string? pdbPath)
    {
        _provider = provider;
        _pdb = provider.GetMetadataReader();
        PdbPath = pdbPath;
    }

    /// <summary>Path of the PDB file, or null for embedded PDBs.</summary>
    public string? PdbPath { get; }

    internal static SymbolReader? TryOpen(PEReader peReader, string assemblyPath)
    {
        try
        {
            // Ownership of the provider moves to the reader.
#pragma warning disable CA2000
            if (peReader.TryOpenAssociatedPortablePdb(
                    assemblyPath,
                    static path => File.Exists(path) ? File.OpenRead(path) : null,
                    out var provider,
                    out var pdbPath))
            {
                return provider is null ? null : new SymbolReader(provider, pdbPath);
            }
#pragma warning restore CA2000
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot read symbols for '{assemblyPath}': {ex.Message}");
        }

        return null;
    }

    // ---- Documents --------------------------------------------------------------------

    /// <summary>Finds the PDB document that corresponds to a client-side path.</summary>
    public DocumentHandle FindDocument(string path)
    {
        var bestHandle = default(DocumentHandle);
        var bestScore = 0;
        foreach (var handle in _pdb.Documents)
        {
            var name = _pdb.GetString(_pdb.GetDocument(handle).Name);
            var score = SourcePathMatcher.Score(path, name);
            if (score > bestScore)
            {
                bestScore = score;
                bestHandle = handle;
            }
        }

        return bestHandle;
    }

    public bool ContainsDocument(string path) => !FindDocument(path).IsNil;

    // ---- Line -> IL --------------------------------------------------------------------

    /// <summary>
    /// Maps a 1-based source line to IL. Prefers a sequence point that spans the line,
    /// otherwise binds to the next line that has code (like Visual Studio does).
    /// </summary>
    public BreakpointResolution? ResolveBreakpoint(string path, int line)
    {
        var document = FindDocument(path);
        if (document.IsNil)
        {
            return null;
        }

        var points = GetDocumentIndex().GetValueOrDefault(document);
        if (points is null || points.Count == 0)
        {
            return null;
        }

        // 1. A statement that starts on the requested line (this includes statements inside
        //    a lambda that is itself part of a larger multi-line statement).
        var targetLine = int.MaxValue;
        foreach (var (_, point) in points)
        {
            if (point.StartLine == line)
            {
                targetLine = line;
                break;
            }
        }

        // 2. The innermost multi-line statement that spans the requested line.
        if (targetLine == int.MaxValue)
        {
            SequencePoint? spanning = null;
            foreach (var (_, point) in points)
            {
                if (point.StartLine < line && line <= point.EndLine
                    && (spanning is null || point.EndLine - point.StartLine < spanning.Value.EndLine - spanning.Value.StartLine))
                {
                    spanning = point;
                }
            }

            targetLine = spanning?.StartLine ?? int.MaxValue;
        }

        // 3. Otherwise the next line that has code.
        if (targetLine == int.MaxValue)
        {
            foreach (var (_, point) in points)
            {
                if (point.StartLine >= line && point.StartLine < targetLine)
                {
                    targetLine = point.StartLine;
                }
            }
        }

        if (targetLine == int.MaxValue)
        {
            return null;
        }

        // Among sequence points on the target line, the left-most statement wins; this
        // binds to the enclosing statement rather than a lambda body on the same line.
        var minColumn = int.MaxValue;
        foreach (var (_, point) in points)
        {
            if (point.StartLine == targetLine && point.StartColumn < minColumn)
            {
                minColumn = point.StartColumn;
            }
        }

        var targets = new List<BreakpointTarget>();
        SequencePoint chosen = default;
        foreach (var (method, point) in points)
        {
            if (point.StartLine != targetLine || point.StartColumn != minColumn)
            {
                continue;
            }

            var existing = targets.FindIndex(t => t.MethodToken == method);
            if (existing < 0)
            {
                targets.Add(new BreakpointTarget(method, (uint)point.Offset));
                chosen = point;
            }
            else if (point.Offset < targets[existing].ILOffset)
            {
                targets[existing] = new BreakpointTarget(method, (uint)point.Offset);
            }
        }

        var documentName = _pdb.GetString(_pdb.GetDocument(document).Name);
        return new BreakpointResolution(
            new SourceLocation(documentName, chosen.StartLine, chosen.StartColumn, chosen.EndLine, chosen.EndColumn),
            targets);
    }

    private Dictionary<DocumentHandle, List<(uint, SequencePoint)>> GetDocumentIndex()
    {
        if (_byDocument is not null)
        {
            return _byDocument;
        }

        var index = new Dictionary<DocumentHandle, List<(uint, SequencePoint)>>();
        foreach (var handle in _pdb.MethodDebugInformation)
        {
            var info = _pdb.GetMethodDebugInformation(handle);
            if (info.SequencePointsBlob.IsNil)
            {
                continue;
            }

            var token = (uint)MetadataTokens.GetToken(handle.ToDefinitionHandle());
            foreach (var point in info.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                if (!index.TryGetValue(point.Document, out var list))
                {
                    index[point.Document] = list = [];
                }

                list.Add((token, point));
            }
        }

        return _byDocument = index;
    }

    // ---- IL -> line --------------------------------------------------------------------

    /// <summary>All sequence points (including hidden ones) of a method, ordered by IL offset.</summary>
    public SequencePoint[] GetSequencePoints(uint methodToken)
    {
        if (_sequencePoints.TryGetValue(methodToken, out var cached))
        {
            return cached;
        }

        var points = new List<SequencePoint>();
        var handle = MetadataTokens.MethodDebugInformationHandle((int)(methodToken & 0x00FFFFFF));
        try
        {
            var info = _pdb.GetMethodDebugInformation(handle);
            if (!info.SequencePointsBlob.IsNil)
            {
                points.AddRange(info.GetSequencePoints());
            }
        }
        catch (BadImageFormatException)
        {
            // Method has no debug information row.
        }

        points.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        return _sequencePoints[methodToken] = [.. points];
    }

    public bool HasSequencePoints(uint methodToken) => GetSequencePoints(methodToken).Length > 0;

    /// <summary>The sequence point that covers <paramref name="ilOffset"/>, if any (may be hidden).</summary>
    public SequencePoint? FindSequencePoint(uint methodToken, uint ilOffset)
    {
        var points = GetSequencePoints(methodToken);
        SequencePoint? match = null;
        foreach (var point in points)
        {
            if (point.Offset > ilOffset)
            {
                break;
            }

            match = point;
        }

        return match;
    }

    /// <summary>The source location of the nearest non-hidden sequence point at or before the offset.</summary>
    public SourceLocation? GetLocation(uint methodToken, uint ilOffset)
    {
        var points = GetSequencePoints(methodToken);
        SequencePoint? match = null;
        foreach (var point in points)
        {
            if (point.Offset > ilOffset)
            {
                break;
            }

            if (!point.IsHidden)
            {
                match = point;
            }
        }

        // An offset in the method prologue maps to the first statement.
        if (match is null)
        {
            foreach (var point in points)
            {
                if (!point.IsHidden)
                {
                    match = point;
                    break;
                }
            }
        }

        if (match is not { } p)
        {
            return null;
        }

        return new SourceLocation(
            _pdb.GetString(_pdb.GetDocument(p.Document).Name), p.StartLine, p.StartColumn, p.EndLine, p.EndColumn);
    }

    /// <summary>The visible sequence points of a method with their source locations.</summary>
    public List<(uint Offset, SourceLocation Location)> GetVisiblePoints(uint methodToken)
    {
        var result = new List<(uint, SourceLocation)>();
        foreach (var point in GetSequencePoints(methodToken))
        {
            if (!point.IsHidden)
            {
                result.Add(((uint)point.Offset, new SourceLocation(
                    _pdb.GetString(_pdb.GetDocument(point.Document).Name), point.StartLine, point.StartColumn, point.EndLine, point.EndColumn)));
            }
        }

        return result;
    }

    public bool IsHiddenOffset(uint methodToken, uint ilOffset) =>
        FindSequencePoint(methodToken, ilOffset) is not { } point || point.IsHidden;

    /// <summary>
    /// The IL range of the statement containing <paramref name="ilOffset"/>: from its sequence
    /// point to the next non-hidden one. Hidden code in between is stepped over.
    /// </summary>
    public (uint Start, uint End) GetStepRange(uint methodToken, uint ilOffset, uint methodSize)
    {
        var points = GetSequencePoints(methodToken);
        uint start = 0;
        var end = methodSize;
        var found = false;
        foreach (var point in points)
        {
            var offset = (uint)point.Offset;
            if (offset <= ilOffset)
            {
                start = offset;
                found = true;
            }
            else if (found && !point.IsHidden && offset > start)
            {
                end = offset;
                break;
            }
        }

        return (start, end);
    }

    /// <summary>First non-hidden IL offset of a method, or null when it has none.</summary>
    public uint? GetFirstUserOffset(uint methodToken)
    {
        foreach (var point in GetSequencePoints(methodToken))
        {
            if (!point.IsHidden)
            {
                return (uint)point.Offset;
            }
        }

        return null;
    }

    /// <summary>
    /// First IL offset of a real statement: skips hidden points and the opening brace,
    /// which debug builds give its own sequence point.
    /// </summary>
    public uint? GetFirstStatementOffset(uint methodToken)
    {
        uint? first = null;
        foreach (var point in GetSequencePoints(methodToken))
        {
            if (point.IsHidden)
            {
                continue;
            }

            first ??= (uint)point.Offset;
            var isBrace = point.StartLine == point.EndLine && point.EndColumn - point.StartColumn == 1;
            if (!isBrace)
            {
                return (uint)point.Offset;
            }
        }

        return first;
    }

    // ---- Locals -----------------------------------------------------------------------

    /// <summary>Named locals in scope at the offset; inner scopes shadow outer ones.</summary>
    public List<LocalInfo> GetLocals(uint methodToken, uint ilOffset)
    {
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        var method = MetadataTokens.MethodDefinitionHandle((int)(methodToken & 0x00FFFFFF));
        foreach (var scopeHandle in _pdb.GetLocalScopes(method))
        {
            var scope = _pdb.GetLocalScope(scopeHandle);
            if (ilOffset < scope.StartOffset || ilOffset >= scope.EndOffset)
            {
                continue;
            }

            foreach (var variableHandle in scope.GetLocalVariables())
            {
                var variable = _pdb.GetLocalVariable(variableHandle);
                if ((variable.Attributes & LocalVariableAttributes.DebuggerHidden) != 0)
                {
                    continue;
                }

                var name = _pdb.GetString(variable.Name);
                if (!byName.ContainsKey(name))
                {
                    order.Add(name);
                }

                byName[name] = variable.Index;
            }
        }

        var result = new List<LocalInfo>(order.Count);
        foreach (var name in order)
        {
            result.Add(new LocalInfo(byName[name], name));
        }

        return result;
    }

    // ---- Async state machines ---------------------------------------------------------

    /// <summary>For a state machine MoveNext, the user-visible async/iterator method.</summary>
    public uint? GetKickoffMethod(uint moveNextToken)
    {
        try
        {
            var handle = MetadataTokens.MethodDebugInformationHandle((int)(moveNextToken & 0x00FFFFFF));
            var kickoff = _pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
            return kickoff.IsNil ? null : (uint)MetadataTokens.GetToken(kickoff);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>For an async/iterator method, the MoveNext method that holds its body.</summary>
    public uint? GetMoveNextMethod(uint kickoffToken)
    {
        if (_kickoffToMoveNext is null)
        {
            _kickoffToMoveNext = [];
            foreach (var handle in _pdb.MethodDebugInformation)
            {
                var kickoff = _pdb.GetMethodDebugInformation(handle).GetStateMachineKickoffMethod();
                if (!kickoff.IsNil)
                {
                    _kickoffToMoveNext[(uint)MetadataTokens.GetToken(kickoff)] =
                        (uint)MetadataTokens.GetToken(handle.ToDefinitionHandle());
                }
            }
        }

        return _kickoffToMoveNext.TryGetValue(kickoffToken, out var moveNext) ? moveNext : null;
    }

    /// <summary>The <c>await</c> points of an async state machine's MoveNext (empty otherwise).</summary>
    public AwaitPoint[] GetAwaitPoints(uint moveNextToken)
    {
        if (_awaitPoints.TryGetValue(moveNextToken, out var cached))
        {
            return cached;
        }

        var result = new List<AwaitPoint>();
        var method = MetadataTokens.MethodDefinitionHandle((int)(moveNextToken & 0x00FFFFFF));
        foreach (var handle in _pdb.GetCustomDebugInformation(method))
        {
            var information = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(information.Kind) != AsyncMethodSteppingInformation)
            {
                continue;
            }

            // Blob: catch handler offset (u32), then (yield u32, resume u32, method compressed)*.
            var blob = _pdb.GetBlobReader(information.Value);
            _ = blob.ReadUInt32();
            while (blob.RemainingBytes > 0)
            {
                var yieldOffset = blob.ReadUInt32();
                var resumeOffset = blob.ReadUInt32();
                var resumeMethod = (uint)blob.ReadCompressedInteger();
                result.Add(new AwaitPoint(yieldOffset, resumeOffset, 0x06000000 | resumeMethod));
            }
        }

        return _awaitPoints[moveNextToken] = [.. result];
    }

    // ---- Sources ----------------------------------------------------------------------

    /// <summary>The text of a source file embedded in the PDB (<c>EmbedAllSources</c>), if any.</summary>
    public byte[]? GetEmbeddedSource(string documentName)
    {
        foreach (var handle in _pdb.Documents)
        {
            if (_pdb.GetString(_pdb.GetDocument(handle).Name) != documentName)
            {
                continue;
            }

            foreach (var cdiHandle in _pdb.GetCustomDebugInformation(handle))
            {
                var information = _pdb.GetCustomDebugInformation(cdiHandle);
                if (_pdb.GetGuid(information.Kind) != EmbeddedSourceKind)
                {
                    continue;
                }

                // Blob: int32 format (0 = raw, otherwise the uncompressed size of deflated data).
                var blob = _pdb.GetBlobBytes(information.Value);
                var format = BitConverter.ToInt32(blob, 0);
                if (format == 0)
                {
                    return blob[4..];
                }

                using var input = new MemoryStream(blob, 4, blob.Length - 4);
                using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
                var text = new byte[format];
                deflate.ReadExactly(text);
                return text;
            }
        }

        return null;
    }

    /// <summary>The Source Link URL of a document (e.g. a raw.githubusercontent.com URL), if mapped.</summary>
    public Uri? GetSourceLinkUrl(string documentName)
    {
        _sourceLink ??= ReadSourceLink();
        string? best = null;
        var bestLength = -1;
        foreach (var (pattern, url) in _sourceLink)
        {
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (prefix.Length > bestLength && documentName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    bestLength = prefix.Length;
                    best = url.Replace("*", documentName[prefix.Length..].Replace('\\', '/'), StringComparison.Ordinal);
                }
            }
            else if (string.Equals(pattern, documentName, StringComparison.OrdinalIgnoreCase))
            {
                best = url;
                break;
            }
        }

        return best is not null && Uri.TryCreate(best, UriKind.Absolute, out var uri) ? uri : null;
    }

    private List<(string, string)> ReadSourceLink()
    {
        var result = new List<(string, string)>();
        foreach (var handle in _pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var information = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(information.Kind) != SourceLinkKind)
            {
                continue;
            }

            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(_pdb.GetBlobBytes(information.Value));
                if (json.RootElement.TryGetProperty("documents", out var documents))
                {
                    foreach (var entry in documents.EnumerateObject())
                    {
                        if (entry.Value.GetString() is { } url)
                        {
                            result.Add((entry.Name, url));
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                Log.Warn($"Invalid Source Link data: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Opens a standalone portable PDB file (e.g. one downloaded from a symbol server).</summary>
    internal static SymbolReader? TryOpenFile(string pdbPath)
    {
        try
        {
            // The provider owns the stream, and the reader owns the provider.
#pragma warning disable CA2000
            var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
            return new SymbolReader(provider, pdbPath);
#pragma warning restore CA2000
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            Log.Warn($"Cannot read '{pdbPath}': {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _provider.Dispose();
}
