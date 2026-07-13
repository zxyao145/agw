using System.ComponentModel.DataAnnotations;

using Agw.Shared.Configuration;

namespace Agw.Setup.Contracts;

public class SetupRequest
{
    [Required]
    [Display(Name = "数据库类型")]
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    [Required]
    [Display(Name = "连接字符串")]
    public string ConnectionString { get; set; } = string.Empty;

    [Required]
    [StringLength(256, MinimumLength = 12)]
    [DataType(DataType.Password)]
    [Display(Name = "管理员密码")]
    public string AdminPassword { get; set; } = string.Empty;

    [Display(Name = "Setup Code")]
    public string? SetupCode { get; set; }
}
