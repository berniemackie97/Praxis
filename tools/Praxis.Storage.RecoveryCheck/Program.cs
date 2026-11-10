using Praxis.Storage.RecoveryCheck.Verification;

if (args.Length != 2 || args[0] is not ("--create" or "--verify"))
{
    Console.Error.WriteLine("Usage: Praxis.Storage.RecoveryCheck --create <scratch-directory> | --verify <check-workspace>");
    return 2;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (args[0] == "--create")
    {
        string workspace = await RecoveryCheck.CreateAsync(args[1], cancellation.Token);
        Console.WriteLine("Created and verified: " + workspace);
    }
    else
    {
        await RecoveryCheck.VerifyAsync(args[1], cancellation.Token);
        Console.WriteLine("Backup and restored catalog verified.");
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
