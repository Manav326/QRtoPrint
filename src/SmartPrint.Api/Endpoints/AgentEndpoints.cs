using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    public static RouteGroupBuilder MapAgentEndpoints(
        this RouteGroupBuilder agentApi,
        string signingKeyText,
        string issuer,
        string audience,
        string[] corsOrigins)
    {
        MapAgentEnrollmentEndpoint(agentApi);
        MapAgentJobEndpoints(agentApi);
        MapAgentLocalUploadEndpoints(agentApi);
        agentApi.MapAgentLocalPhotoRevisionEndpoints(signingKeyText, issuer, audience);
        MapAgentJobLifecycleEndpoints(agentApi, signingKeyText, issuer, audience);
        MapAgentConnectivityEndpoints(agentApi, corsOrigins);
        return agentApi;
    }
}
