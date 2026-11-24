using System.Text.Json;

namespace FModelCnPatcher;

public sealed class LangPack
{
    public Dictionary<string, string> BamlStrings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BamlTagStrings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BamlSetterStrings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> EnumDescriptions { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CodeStrings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SnooperStrings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> StatusLabels { get; init; } = new(StringComparer.Ordinal);

    public static LangPack Load(string path)
    {
        var json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<LangPack>(json, opts)
               ?? throw new InvalidDataException($"无法解析语言包文件: {path}");
    }
}
