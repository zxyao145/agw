using System;
using System.Collections.Generic;
using System.Text;

namespace DSystem.Shared;

public static class GuidExtensions
{
    public static string Normalize(this Guid id)
    {
        return id.ToString("D").ToUpperInvariant();
    }
}
