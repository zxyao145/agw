using System.ComponentModel.DataAnnotations;

namespace Agw.Setup.Contracts;

public class SetupRequest
{
    [Required]
    [StringLength(256, MinimumLength = 8)]
    [DataType(DataType.Password)]
    [Display(Name = "Administrator password")]
    public string AdminPassword { get; set; } = string.Empty;

    [Display(Name = "Setup Code")]
    public string? SetupCode { get; set; }
}
