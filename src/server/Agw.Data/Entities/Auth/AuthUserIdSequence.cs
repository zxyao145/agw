using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Auth;

[Table("auth_user_id_sequence")]
[EntityTypeConfiguration(typeof(AuthUserIdSequenceConfiguration))]
public sealed class AuthUserIdSequence
{
    public int Id { get; set; }
    public long NextId { get; set; }
}
