using System.Text.Json;
using System.Text.Json.Serialization;

namespace Overlord.Infrastructure.Ficsit;

// Wire shapes for the ficsit.app GraphQL API. Kept internal and separate from the Core catalogue
// types so an API change only touches this file and the mapping in FicsitApiClient.

internal sealed record GraphQlRequest<TVariables>(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] TVariables Variables);

internal sealed record PageVariables(
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit);

internal sealed record VersionVariables(
    [property: JsonPropertyName("modReference")] string ModReference,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit);

internal sealed record GraphQlResponse<T>(
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQlError>? Errors) where T : class;

internal sealed record GraphQlError([property: JsonPropertyName("message")] string Message);

internal sealed record ModsData([property: JsonPropertyName("getMods")] ModsPageDto GetMods);

internal sealed record ModsPageDto(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("mods")] IReadOnlyList<ModDto> Mods);

internal sealed record ModDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mod_reference")] string ModReference,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("short_description")] string? ShortDescription,
    [property: JsonPropertyName("full_description")] string? FullDescription,
    [property: JsonPropertyName("logo")] string? Logo,
    [property: JsonPropertyName("downloads")] int Downloads,
    [property: JsonPropertyName("popularity")] int Popularity,
    [property: JsonPropertyName("hotness")] int Hotness,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("last_version_date")] DateTimeOffset? LastVersionDate,
    [property: JsonPropertyName("tags")] IReadOnlyList<TagDto>? Tags,
    [property: JsonPropertyName("authors")] IReadOnlyList<AuthorDto>? Authors,
    [property: JsonPropertyName("compatibility")] CompatibilityInfoDto? Compatibility);

internal sealed record TagDto([property: JsonPropertyName("name")] string Name);

internal sealed record AuthorDto([property: JsonPropertyName("user")] UserDto? User);

internal sealed record UserDto([property: JsonPropertyName("username")] string Username);

internal sealed record CompatibilityInfoDto(
    [property: JsonPropertyName("EA")] CompatibilityDto? EA,
    [property: JsonPropertyName("EXP")] CompatibilityDto? EXP);

internal sealed record CompatibilityDto([property: JsonPropertyName("state")] string? State);

internal sealed record ModVersionsData([property: JsonPropertyName("getModByReference")] ModVersionsDto? Mod);

internal sealed record ModVersionsDto([property: JsonPropertyName("versions")] IReadOnlyList<VersionDto> Versions);

internal sealed record VersionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("game_version")] string GameVersion,
    [property: JsonPropertyName("stability")] string Stability,
    [property: JsonPropertyName("required_on_remote")] bool RequiredOnRemote,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("targets")] IReadOnlyList<TargetDto>? Targets,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<DependencyDto>? Dependencies);

internal sealed record TargetDto(
    [property: JsonPropertyName("targetName")] string TargetName,
    [property: JsonPropertyName("link")] string Link,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("hash")] string? Hash);

internal sealed record DependencyDto(
    [property: JsonPropertyName("mod_reference")] string ModReference,
    [property: JsonPropertyName("condition")] string Condition,
    [property: JsonPropertyName("optional")] bool Optional);

// Source-generated serializers: no runtime reflection, so this works under trimming and Native AOT.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(GraphQlRequest<PageVariables>))]
[JsonSerializable(typeof(GraphQlRequest<VersionVariables>))]
[JsonSerializable(typeof(GraphQlResponse<ModsData>))]
[JsonSerializable(typeof(GraphQlResponse<ModVersionsData>))]
internal sealed partial class FicsitJsonContext : JsonSerializerContext;
