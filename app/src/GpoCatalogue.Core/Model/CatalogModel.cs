using System.Text.Json.Serialization;

namespace GpoCatalogue.Core.Model;

// Shape of data/desktop.json.gz as written by pipeline/desktop.py. Property names map to snake_case.

public sealed class CatalogData
{
    public int Schema { get; set; }
    public Meta Meta { get; set; } = new();
    public List<PolicyDef> Policies { get; set; } = [];
}

public sealed class Meta
{
    public string Generated { get; set; } = "";
    public string Model { get; set; } = "";
    public string QuestionsSha { get; set; } = "";
    public MetaCounts Counts { get; set; } = new();
}

public sealed class MetaCounts
{
    public int Policies { get; set; }
    public Dictionary<string, int> BySource { get; set; } = [];
    public int WithMl { get; set; }
    public int InRtlfb { get; set; }
    public int InSb { get; set; }
    public List<int> RtlfbEntriesMapped { get; set; } = [];
    public List<int> SbEntriesMapped { get; set; } = [];
}

public sealed class PolicyDef
{
    public string Uid { get; set; } = "";
    public string Name { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string Admx { get; set; } = "";
    public string Source { get; set; } = "";
    [JsonPropertyName("class")] public string Class { get; set; } = "Machine";
    public string CategoryPath { get; set; } = "";
    public string SupportedOn { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string RegistryKey { get; set; } = "";
    public string? ValueName { get; set; }
    public List<string> RegistryValues { get; set; } = [];
    public List<OptionInfo> Options { get; set; } = [];
    public ValueSpec? EnabledValue { get; set; }
    public ValueSpec? DisabledValue { get; set; }
    public List<ListItemDef> EnabledList { get; set; } = [];
    public List<ListItemDef> DisabledList { get; set; } = [];
    public List<ElementDef> Elements { get; set; } = [];
    public List<PresItem> Pres { get; set; } = [];
    public string? ClientExtension { get; set; }
    public MlScores? Ml { get; set; }
    public List<BaselineApplication> Rtlfb { get; set; } = [];
    public List<BaselineApplication> Sb { get; set; } = [];
    public List<string> Flags { get; set; } = [];

    [JsonIgnore] public IReadOnlyList<Scope> Scopes => Class switch
    {
        "Both" => [Scope.Machine, Scope.User],
        "User" => [Scope.User],
        _ => [Scope.Machine],
    };

    public ElementDef? Element(string id) =>
        Elements.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Uid;
}

/// <summary>Display-only description of an option, as shown on the site.</summary>
public sealed class OptionInfo
{
    public string Label { get; set; } = "";
    public string Type { get; set; } = "";
    public List<string>? Choices { get; set; }
    public List<long?>? Range { get; set; }
}

public sealed class ValueSpec
{
    public const string Decimal = "decimal", LongDecimal = "longDecimal", String = "string", Delete = "delete";

    public string Kind { get; set; } = Decimal;
    public long? Num { get; set; }
    public string? Str { get; set; }

    [JsonIgnore] public bool IsDelete => Kind == Delete;
    [JsonIgnore] public bool IsNumber => Kind is Decimal or LongDecimal;

    public static ValueSpec Dword(long value) => new() { Kind = Decimal, Num = value };
    public static ValueSpec Deleted() => new() { Kind = Delete };
}

public sealed class ListItemDef
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public ValueSpec Spec { get; set; } = new();
}

public sealed class EnumItemDef
{
    public string Display { get; set; } = "";
    public ValueSpec? Spec { get; set; }
    public List<ListItemDef> ValueList { get; set; } = [];
}

public sealed class ElementDef
{
    public const string Enum = "enum", Boolean = "boolean", Decimal = "decimal", LongDecimal = "longDecimal",
        Text = "text", MultiText = "multiText", List = "list";

    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Key { get; set; } = "";
    public string? ValueName { get; set; }
    public string Label { get; set; } = "";
    public bool Required { get; set; }
    public List<EnumItemDef> Items { get; set; } = [];
    public ValueSpec? TrueSpec { get; set; }
    public ValueSpec? FalseSpec { get; set; }
    public List<ListItemDef> TrueList { get; set; } = [];
    public List<ListItemDef> FalseList { get; set; } = [];
    public long? MinValue { get; set; }
    public long? MaxValue { get; set; }
    public bool StoreAsText { get; set; }
    public int? MaxLength { get; set; }
    public int? MaxStrings { get; set; }
    public bool Expandable { get; set; }
    /// <summary>null and "" differ: null names each value after its data, "" numbers them 1..n.</summary>
    public string? ValuePrefix { get; set; }
    public bool Additive { get; set; }
    public bool ExplicitValue { get; set; }
    public ElementUi? Ui { get; set; }

    // The ADMX schema defaults apply when the template gives no bounds.
    [JsonIgnore] public long Min => MinValue ?? 0;
    [JsonIgnore] public long Max => MaxValue ?? 9999;
    [JsonIgnore] public int TextLimit => MaxLength ?? 1023;
    [JsonIgnore] public bool IsList => Type == List;
}

/// <summary>The dialog control gpedit shows for an element, with the value it pre-fills.</summary>
public sealed class ElementUi
{
    public string Ctl { get; set; } = "";
    public long? DefNum { get; set; }
    public bool DefBool { get; set; }
    public string? DefStr { get; set; }
    public long? SpinStep { get; set; }
    public bool NoSort { get; set; }
    public List<string>? Suggestions { get; set; }
}

public sealed class PresItem
{
    public string? Text { get; set; }
    [JsonPropertyName("ref")] public string? Ref { get; set; }
}

public sealed class MlScores
{
    public double Privacy { get; set; }
    public double Security { get; set; }
    public double UxDegrade { get; set; }
    public string PrivacyDir { get; set; } = "";
    public double PrivacyDirConf { get; set; }
    public string SecurityDir { get; set; } = "";
    public double SecurityDirConf { get; set; }
}

public sealed class BaselineApplication
{
    public string Gpo { get; set; } = "";
    public string Scope { get; set; } = "Machine";
    public string State { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<List<string>> Values { get; set; } = [];
    public List<string> Raw { get; set; } = [];
    public List<string> Flags { get; set; } = [];
    public List<RawEntry> Entries { get; set; } = [];

    [JsonIgnore] public Scope ScopeValue => Scope == "User" ? Model.Scope.User : Model.Scope.Machine;
}

public sealed class RawEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public uint Type { get; set; }
    public string Hex { get; set; } = "";
}

public enum Scope
{
    Machine,
    User,
}

public enum PolicyState
{
    NotConfigured,
    Enabled,
    Disabled,
}

public static class ScopeExtensions
{
    public static string Hive(this Scope scope) => scope == Scope.Machine ? "HKLM" : "HKCU";

    /// <summary>The name gpedit uses for the section.</summary>
    public static string Display(this Scope scope) => scope == Scope.Machine ? "Computer" : "User";

    public static string Display(this PolicyState state) => state switch
    {
        PolicyState.Enabled => "Enabled",
        PolicyState.Disabled => "Disabled",
        _ => "Not configured",
    };
}
