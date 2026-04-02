using System;
using System.Collections.Generic;

namespace Nebula.Postgres.Context.Entities;

public class Request
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Prompt { get; set; } = string.Empty;

    public string Classification { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<StoredCommand> Commands { get; set; } = new List<StoredCommand>();
}