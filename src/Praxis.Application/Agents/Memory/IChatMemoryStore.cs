namespace Praxis.Application.Agents.Memory;

public interface IChatMemoryStore
{
    ChatMemoryWork? Next();
    bool IsPending(ChatMemoryWork work);
    bool SaveRecap(ChatMemoryWork work, string recap);
    void Retry(ChatMemoryWork work);
    string? ReadRecap(Guid conversationId);
    string? ReadName();
    void SaveName(string name);
}
