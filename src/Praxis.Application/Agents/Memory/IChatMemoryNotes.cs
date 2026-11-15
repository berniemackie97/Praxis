namespace Praxis.Application.Agents.Memory;

public interface IChatMemoryNotes
{
    IReadOnlyList<ChatMemoryNote> Find(string query, CancellationToken cancellationToken);
    ChatMemoryNote Open(Guid sourceId, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(ChatMemoryNote current, string markdown, CancellationToken cancellationToken);
}
