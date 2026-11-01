using VakifAtlas.Assistant.Api.Infrastructure;
using VakifAtlas.Assistant.Domain;
using VakifAtlas.ServiceDefaults.Http;
using VakifAtlas.ServiceDefaults.Redis;
using VakifAtlas.SharedKernel;

namespace VakifAtlas.Assistant.Api.Features;

public sealed record AskRequest(string? Message, string? ConversationId);

public sealed record SourceResponse(string MonumentId, string Name, string City);

public sealed record MessageResponse(string Id, string Text, DateTimeOffset CreatedAt, string? Notice,
    IReadOnlyList<SourceResponse> Sources, IReadOnlyList<object> Actions, IReadOnlyList<string> Suggestions);

public sealed record AskResponse(string ConversationId, MessageResponse Message);

/// <summary>
/// Dijital Rehber (ADR 37): oturum gerekmez. Eylemler tipli ve izinli listededir (en çok 4);
/// sunucu serbest URL döndürmez. Sorular loglanmaz.
/// </summary>
public static class AssistantEndpoints
{
    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("messages", Ask);
        group.MapDelete("conversations/{id}", async (string id, IConversationStore conversations, CancellationToken cancellationToken) =>
        {
            await conversations.DeleteAsync(id, cancellationToken);
            return TypedResults.NoContent();
        });
        return group.AllowAnonymous();
    }

    private static async Task<IResult> Ask(AskRequest request, HttpContext context, IConversationStore conversations,
        IAnswerGenerator generator, IUsageLimiter limiter, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!GuideRules.IsValidQuestion(request.Message) || request.ConversationId is { Length: > 64 })
        {
            return ApiError.BadRequest().ToHttpResult();
        }

        // Bilinmeyen / süresi dolmuş sohbet: hata değil, yeni sohbet.
        var (exists, focus) = request.ConversationId is { } requested
            ? await conversations.GetAsync(requested, cancellationToken)
            : (false, null);
        var conversationId = exists ? request.ConversationId! : Ids.New("conv");

        var quota = await limiter.TryConsumeAsync($"assistant:{conversationId}", GuideRules.MaxQuestionsPerMinute,
            TimeSpan.FromMinutes(1), cancellationToken);
        // `conversationId` gönderilmezse yeni sohbet açılır; sohbet başına sınır tek başına yeterli
        // değildir (gerçek LLM'de maliyet), istemci adresi başına daha geniş bir sınır da uygulanır.
        if (quota.Allowed && context.Connection.RemoteIpAddress is { } address)
        {
            quota = await limiter.TryConsumeAsync($"assistant:ip:{address}", GuideRules.MaxQuestionsPerMinute * 3,
                TimeSpan.FromMinutes(1), cancellationToken);
        }

        if (!quota.Allowed)
        {
            return ApiError.TooManyRequests(quota.RetryAfterSeconds).ToHttpResult();
        }

        var language = RequestLanguage.Resolve(context.Request);
        var answer = await generator.AnswerAsync(request.Message!.Trim(), language, focus, cancellationToken);
        await conversations.SaveAsync(conversationId, answer.FocusMonumentId ?? focus, cancellationToken);

        return TypedResults.Ok(new AskResponse(conversationId, new MessageResponse(
            Ids.New("msg"),
            answer.Text,
            clock.GetUtcNow(),
            answer.Notice,
            answer.Sources.Select(s => new SourceResponse(s.Id, s.Name.Resolve(language), s.City)).ToArray(),
            answer.Actions.Take(GuideRules.MaxActions).Cast<object>().ToArray(),
            answer.Suggestions)));
    }
}
