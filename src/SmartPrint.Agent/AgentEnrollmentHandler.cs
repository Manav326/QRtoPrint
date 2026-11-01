namespace SmartPrint.Agent;

public interface IAgentEnrollmentHandler
{
    Task<AgentCredentials> EnrollAsync(
        string apiBaseUrl,
        string enrollmentCode,
        CancellationToken cancellationToken);
}

public sealed class AgentEnrollmentHandler(AgentCredentialStore credentialStore) : IAgentEnrollmentHandler
{
    public Task<AgentCredentials> EnrollAsync(
        string apiBaseUrl,
        string enrollmentCode,
        CancellationToken cancellationToken) =>
        AgentPairing.EnrollAsync(apiBaseUrl, enrollmentCode, cancellationToken, credentialStore);
}
