using System;
using System.Collections.Generic;

namespace DSystem.Domain.Entities;

public class Provider : BaseEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Endpoint { get; set; } = string.Empty;

    public ICollection<ModelProvider> Models { get; set; } = new List<ModelProvider>();
}
