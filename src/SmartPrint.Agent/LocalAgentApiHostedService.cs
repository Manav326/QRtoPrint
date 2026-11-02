    private async Task RecordCompletedLocalUploadRetryAsync(
        Guid ticketId,
        string uploadGrant,
        AgentCredentials credentials,
        HttpClient client,
        HttpContext context,
        LocalUploadedPrintResult uploadResult,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType || attemptNumber <= 0)
            return;

        byte[]? receivedBytes = null;
        try
        {
            var transferStartedUtc = DateTimeOffset.UtcNow;
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var document = uploadResult.Document;
            if (form.Files.Count != 1 || form.Files[0].Length != document.SizeBytes)
                return;

            await using var memory = new MemoryStream(checked((int)document.SizeBytes));
            await using (var input = form.Files[0].OpenReadStream())
            {
                await input.CopyToAsync(memory, cancellationToken);
            }

            if (memory.Length != document.SizeBytes)
                return;

            receivedBytes = memory.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(receivedBytes)).ToLowerInvariant();
            if (!string.Equals(hash, document.Sha256, StringComparison.OrdinalIgnoreCase))
                return;

            var analysis = DocumentAnalyzer.Analyse(
                receivedBytes,
                Path.GetExtension(document.OriginalFileName));
            var durationMilliseconds = Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - transferStartedUtc).TotalMilliseconds);

            using var completionRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"api/v1/agent/local-uploads/{ticketId}/complete")
            {
                Content = JsonContent.Create(new LocalPrintUploadCompletionRequest(
                    analysis.PageCount,
                    analysis.PageSizesJson,
                    analysis.Extension,
                    analysis.MediaType,
                    document.SizeBytes,
                    document.Sha256,
                    receivedBytes.LongLength,
                    durationMilliseconds,
                    attemptNumber))
            };
            AddAgentHeaders(completionRequest, credentials, uploadGrant);
            using var completionResponse = await client.SendAsync(completionRequest, cancellationToken);
            if (!completionResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "The already-completed local upload retry could not be recorded for ticket {TicketId} (HTTP {StatusCode}); the existing completed print job remains unchanged.",
                    ticketId,
                    (int)completionResponse.StatusCode);
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or JsonException or
                DocumentValidationException or HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Could not measure an idempotent resend for completed local upload ticket {TicketId}; the existing completed print job remains unchanged.",
                ticketId);
        }
        finally
        {
            if (receivedBytes is not null)
                CryptographicOperations.ZeroMemory(receivedBytes);
        }
    }

    private static void AddAgentHeaders(HttpRequestMessage request, AgentCredentials credentials, string uploadGrant)
    {