using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Auth;

[Table("api_token")]
[EntityTypeConfiguration(typeof(ApiTokenConfiguration))]
public class ApiToken : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;

    [JsonIgnore]
    public string SecretHash { get; set; } = string.Empty;

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();
}
