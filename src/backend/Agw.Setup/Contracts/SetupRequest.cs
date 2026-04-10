using System.ComponentModel.DataAnnotations;

namespace Agw.Setup.Contracts;

public class SetupRequest
{
    [Required]
    [Display(Name = "数据库类型")]
    public string Provider { get; set; } = "sqlite";

    [Required]
    [Display(Name = "连接字符串")]
    public string ConnectionString { get; set; } = string.Empty;

    [Display(Name = "API Key（可选）")]
    public string? ApiKey { get; set; }
}
