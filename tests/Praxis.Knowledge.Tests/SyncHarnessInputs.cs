namespace Praxis.Knowledge.Tests;

internal static class SyncHarnessInputs
{
    internal static readonly Guid Epoch = Guid.Parse("55555555-5555-4555-8555-555555555555");
    internal static readonly Guid FirstDevice = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    internal static readonly Guid SecondDevice = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    internal static readonly Guid ThirdDevice = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    internal static readonly Guid SharedRecord = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");

    internal static SyncMutation NewMutation(
        Guid device, Guid operation, Guid record, long revision,
        SyncAction action, string? path, string? body)
        => new(1, Epoch, device, operation, record, revision,
            action, path, body, DateTimeOffset.UnixEpoch);

    internal static Guid TestOperationId(int number)
        => Guid.Parse($"77777777-7777-4777-8777-{number:D12}");

    internal static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    internal static void Shuffle<T>(IList<T> items, Random random)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }
}
