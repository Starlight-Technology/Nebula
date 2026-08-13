namespace Nebula.Postgres.Context.Entities;

public sealed class FetchedPageCache
{
    public string Url { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    public string HtmlHash { get; set; } = string.Empty;

    public DateTimeOffset RetrievedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
