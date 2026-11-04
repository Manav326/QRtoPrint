using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SmartPrint.Api.Endpoints;

public static partial class AgentEndpoints
{
    private static void MapAgentJobLifecycleEndpoints(RouteGroupBuilder agentApi, string signingKeyText, string issuer, string audience)
    {
        MapAgentPrintClaimEndpoint(agentApi);
        MapAgentSpoolerLifecycleEndpoints(agentApi);
        MapAgentDocumentPreviewEndpoint(agentApi, signingKeyText, issuer, audience);
    }
}
