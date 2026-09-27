using System.IO.Compression;
using System.Text.Json;
using GpoCatalogue.Core.Decode;
using GpoCatalogue.Core.Model;

namespace GpoCatalogue.Core;

/// <summary>The policy catalogue embedded in the assembly, with the registry index built over it.</summary>
public sealed class CatalogStore
{
    public const int SupportedSchema = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly Dictionary<string, PolicyDef> _byUid;

    public CatalogStore(CatalogData data)
    {
        if (data.Schema != SupportedSchema)
            throw new InvalidDataException($"Catalogue schema {data.Schema} is not supported (expected {SupportedSchema}).");
        Data = data;
        _byUid = data.Policies.ToDictionary(p => p.Uid, StringComparer.Ordinal);
        Index = new RegIndex(data.Policies);
    }

    public CatalogData Data { get; }
    public Meta Meta => Data.Meta;
    public IReadOnlyList<PolicyDef> Policies => Data.Policies;
    public RegIndex Index { get; }

    public PolicyDef this[string uid] => _byUid[uid];

    public PolicyDef? Find(string uid) => _byUid.GetValueOrDefault(uid);

    public static CatalogStore LoadEmbedded()
    {
        using var resource = typeof(CatalogStore).Assembly.GetManifestResourceStream("desktop.json.gz")
            ?? throw new InvalidOperationException("The embedded catalogue desktop.json.gz is missing.");
        return Load(resource);
    }

    public static CatalogStore Load(Stream gzip)
    {
        using var json = new GZipStream(gzip, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<CatalogData>(json, JsonOptions)
            ?? throw new InvalidDataException("The catalogue is empty.");
        return new CatalogStore(data);
    }
}
