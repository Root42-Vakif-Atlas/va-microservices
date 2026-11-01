using StackExchange.Redis;
using VakifAtlas.Assistant.Domain;

namespace VakifAtlas.Assistant.Api.Infrastructure;

/// <summary>
/// Sohbet bağlamı (son konuşulan eser) sunucuda 30 dk (kayan) tutulur; cihaza ve loglara
/// yazılmaz. Soru metni saklanmaz.
/// </summary>
public interface IConversationStore
{
    Task<(bool Exists, string? FocusMonumentId)> GetAsync(string conversationId, CancellationToken cancellationToken);

    Task SaveAsync(string conversationId, string? focusMonumentId, CancellationToken cancellationToken);

    Task DeleteAsync(string conversationId, CancellationToken cancellationToken);
}

internal sealed class RedisConversationStore(IConnectionMultiplexer redis) : IConversationStore
{
    private const string NoFocus = "-";

    public async Task<(bool Exists, string? FocusMonumentId)> GetAsync(string conversationId, CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetSetExpiryAsync(Key(conversationId), GuideRules.ConversationTtl);
        return value.IsNull ? (false, null) : (true, value == NoFocus ? null : value.ToString());
    }

    public Task SaveAsync(string conversationId, string? focusMonumentId, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(Key(conversationId), focusMonumentId ?? NoFocus, GuideRules.ConversationTtl);

    public Task DeleteAsync(string conversationId, CancellationToken cancellationToken) =>
        redis.GetDatabase().KeyDeleteAsync(Key(conversationId));

    private static string Key(string conversationId) => $"assistant:conversation:{conversationId}";
}

/// <summary>
/// Yanıt üretici portu. Varsayılan kural tabanlıdır; LLM (RAG: hibrit arama → yalnızca
/// bulunan kayıtlarla üretim → çıktı moderasyonu) aynı sözleşmeyle bu arayüzün arkasına takılır.
/// </summary>
public interface IAnswerGenerator
{
    Task<GuideAnswer> AnswerAsync(string question, string language, string? contextMonumentId, CancellationToken cancellationToken);
}

internal sealed class RuleBasedAnswerGenerator(KnowledgeBase knowledge) : IAnswerGenerator
{
    public async Task<GuideAnswer> AnswerAsync(string question, string language, string? contextMonumentId,
        CancellationToken cancellationToken) =>
        (await knowledge.EngineAsync(cancellationToken)).Answer(question, language, contextMonumentId);
}
