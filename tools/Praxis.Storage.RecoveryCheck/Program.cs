using Praxis.Storage.RecoveryCheck.Verification;

if (args.Length != 2 || args[0] is not ("--create" or "--verify" or "--set-create" or "--set-verify"))
{
    Console.Error.WriteLine("Usage: Praxis.Storage.RecoveryCheck --create <scratch-directory> | --verify <check-workspace> | --set-create <private-scratch-directory> | --set-verify <set-rehearsal-workspace>");
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
    else if (args[0] == "--verify")
    {
        await RecoveryCheck.VerifyAsync(args[1], cancellation.Token);
        Console.WriteLine("Backup and restored catalog verified.");
    }
    else if (args[0] == "--set-create")
    {
        string workspace = await RecoverySetCheck.CreateAsync(args[1], cancellation.Token);
        Console.WriteLine("Coordinated recovery rehearsal completed: " + workspace);
    }
    else
    {
        await RecoverySetCheck.VerifyAsync(args[1], cancellation.Token);
        Console.WriteLine("Independent coordinated recovery verification passed.");
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
