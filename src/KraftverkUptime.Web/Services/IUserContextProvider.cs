namespace KraftverkUptime.Web.Services;

/// <summary>
/// Front-end brukerkontekst. V1: anonym stub. V2: lest fra MSAL access token.
/// </summary>
public interface IUserContextProvider
{
    string UserId { get; }
    string OrgId { get; }
    IReadOnlyList<string> Roles { get; }
}

public sealed class AnonymousUserContextProvider : IUserContextProvider
{
    public string UserId => "anon";
    public string OrgId => "dev";
    public IReadOnlyList<string> Roles { get; } = new[] { "PlantReader" };
}
