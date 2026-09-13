using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Greenlight.GalleryCapture;

/// <summary>
/// listing.json, as the schema next door defines it. Property names are spelled explicitly
/// because the schema's are hyphenated and <c>additionalProperties</c> is false — a rename
/// here that drifts from the schema is a listing CI rejects, so the two are kept in one
/// repository on purpose.
/// </summary>
internal sealed record Listing
{
    [JsonPropertyName("$schema")] public string Schema => "../../schema/listing.schema.json";

    [JsonPropertyName("slug")] public required string Slug { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("tagline")] public required string Tagline { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("author")] public required Person Author { get; init; }
    [JsonPropertyName("repository")] public required string Repository { get; init; }

    [JsonPropertyName("homepage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Homepage { get; init; }

    [JsonPropertyName("download")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Download { get; init; }

    [JsonPropertyName("license")] public required string License { get; init; }
    [JsonPropertyName("platforms")] public string[] Platforms { get; init; } = ["windows"];

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("integration")] public required string Integration { get; init; }
    [JsonPropertyName("greenlight-api")] public int GreenlightApi { get; init; } = 1;

    [JsonPropertyName("build-time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildTime { get; init; }

    [JsonPropertyName("assets")] public required Media Assets { get; init; }
    [JsonPropertyName("last-verified")] public required Verified LastVerified { get; init; }
    [JsonPropertyName("maintained")] public bool Maintained { get; init; } = true;

    internal sealed record Person
    {
        [JsonPropertyName("name")] public required string Name { get; init; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; init; }
    }

    internal sealed record Media
    {
        [JsonPropertyName("poster")] public required string Poster { get; init; }

        [JsonPropertyName("clip")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Clip { get; init; }

        [JsonPropertyName("attestation")] public required string Attestation { get; init; }

        [JsonPropertyName("attestation-note")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AttestationNote { get; init; }
    }

    internal sealed record Verified
    {
        [JsonPropertyName("date")] public required string Date { get; init; }
        [JsonPropertyName("greenlight")] public required string Greenlight { get; init; }

        [JsonPropertyName("sdk")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Sdk { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options) + Environment.NewLine;

    /// <summary>
    /// Turns a name into the slug the directory and the gallery URL will use. Lowercase,
    /// hyphenated, no run of separators — the same shape the schema's pattern demands, so
    /// nobody meets that pattern as an error message.
    /// </summary>
    public static string Slugify(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) && character < 128) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }
}
