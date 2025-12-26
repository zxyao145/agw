using System;
using System.Collections.Generic;
using System.Text;

namespace DSystem.Domain.Models;

public class ModelProviderApiKeyDto
{
    public Guid Id { get; set; }
    public Guid ModelProviderId { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool Enable { get; set; } = true;

    public string ModelName { get; set; } = string.Empty;
    public string ProviderIdName { get; set; } = string.Empty;
}
