using Praxis.Application.Intake;
using Praxis.Application.Originals;
using Praxis.Application.Sources;
using Praxis.Domain.Common;
using Praxis.Domain.Intake;
using Praxis.Infrastructure.FileSystem;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// Supervises a bounded set of individually approved intake roots. Each root retains its own
/// watcher, rename evidence and health, while scans share one catalog and one hashing lane.
/// </summary>
public sealed class WatchedFolderIntakeGroup : IIntakeSupervisor
{
    public const int MaximumRoots = 4;

    private readonly WatchedFolderIntake[] monitors;

    public WatchedFolderIntakeGroup(ISourceRepository sources, IOriginalStore originals, string catalogDirectory, IReadOnlyList<string> approvedRoots,
        Action<string>? status = null, Action? onCaptured = null,
        TimeSpan? quietPeriod = null, TimeSpan? rescanInterval = null, TimeProvider? clock = null,
        IIntakeIncidentJournal? incidents = null, IReadOnlySet<string>? pendingRoots = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(originals);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentNullException.ThrowIfNull(approvedRoots);
        if (approvedRoots.Count is < 1 or > MaximumRoots)
        {
            throw new ArgumentOutOfRangeException(nameof(approvedRoots), "Select between one and four watched folders.");
        }

        string dataRoot = FileSystemPaths.Canonicalize(catalogDirectory);
        List<(string Path, bool Pending)> roots = new(approvedRoots.Count);
        foreach (string candidate in approvedRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
            if (!Path.IsPathFullyQualified(candidate))
            {
                throw new ArgumentException("Every approved intake directory must be absolute.", nameof(approvedRoots));
            }

            bool pending = pendingRoots?.Contains(candidate) == true;
            // Pending paths identify the original configured location. Do not follow a
            // replacement link and accidentally register its target as a watched folder.
            string path = pending ? Path.GetFullPath(candidate) : FileSystemPaths.Canonicalize(candidate);
            if (PathScope.Contains(dataRoot, path) || PathScope.Contains(path, dataRoot) ||
                roots.Any(existing => PathScope.Contains(existing.Path, path) || PathScope.Contains(path, existing.Path)))
            {
                throw new ArgumentException("Watched folders cannot overlap each other or the private catalog.", nameof(approvedRoots));
            }

            if (!pending && Directory.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException("An approved watched folder cannot be a symbolic link.", nameof(approvedRoots));
            }

            roots.Add((path, pending));
        }

        SemaphoreSlim sharedScanLock = new(1, 1);
        monitors = roots.Select(root => new WatchedFolderIntake(sources, originals, catalogDirectory, root.Path, quietPeriod, rescanInterval,
            status, onCaptured, sharedScanLock, clock,
            state => incidents?.RecordState(root.Path, state), requireInitialApproval: root.Pending)).ToArray();
    }

    public IReadOnlyList<string> AvailableRoots() =>
        monitors.Where(monitor => monitor.IsApprovedAndAvailable).Select(monitor => monitor.RootPath).ToArray();

    public IReadOnlyList<WatchedRootStatus> RootStatuses() =>
        monitors.Select(monitor => new WatchedRootStatus(monitor.RootPath, monitor.Snapshot())).ToArray();

    public IntakeStatus Snapshot() => Aggregate(RootStatuses());

    public IntakeStatus SetPaused(bool paused)
    {
        foreach (WatchedFolderIntake monitor in monitors)
        {
            monitor.SetPaused(paused);
        }

        return Snapshot();
    }

    public void EnsurePrimaryRootApproved()
    {
        if (!monitors[0].IsApprovedAndAvailable)
        {
            throw new InvalidOperationException("The primary Inbox is offline or requires folder reapproval.");
        }
    }

    public IntakeStatus ApproveRoot(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        WatchedFolderIntake? monitor = monitors.FirstOrDefault(candidate =>
            Path.IsPathFullyQualified(selectedPath) &&
            FileSystemPaths.Canonicalize(selectedPath) == candidate.RootPath);
        if (monitor is null)
        {
            throw new ArgumentException("The selected folder is not approved for intake.", nameof(selectedPath));
        }

        monitor.ApproveRoot(selectedPath);
        return Snapshot();
    }

    public IntakeStatus RequestRescan()
    {
        foreach (WatchedFolderIntake monitor in monitors)
        {
            monitor.RequestRescan();
        }

        return Snapshot();
    }

    public async Task<int> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        int imported = 0;
        foreach (WatchedFolderIntake monitor in monitors)
        {
            imported = checked(imported + await monitor.ReconcileAsync(now, cancellationToken));
        }

        return imported;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] running = monitors.Select(monitor => monitor.RunAsync(lifetime.Token)).ToArray();
        Task first = await Task.WhenAny(running);
        await lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) when (!first.IsFaulted)
        {
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("A supervised intake watcher stopped unexpectedly.");
        }
    }

    private static IntakeStatus Aggregate(IReadOnlyList<WatchedRootStatus> roots)
    {
        IntakeStatus[] states = roots.Select(root => root.Status).ToArray();
        bool paused = states.All(status => status.Paused);
        string state = paused ? "paused" :
            states.All(status => status.State == "offline") ? "offline" :
            states.Any(status => status.State is "offline" or "degraded" or "reapproval_required" or "stopped") ? "degraded" :
            states.Any(status => status.State == "scanning") ? "scanning" :
            states.Any(status => status.State is "starting" or "resuming") ? "starting" : "watching";
        int affected = states.Count(status => status.State is "offline" or "degraded" or "reapproval_required" or "stopped");
        DateTimeOffset? lastScan = states.All(status => status.LastSuccessfulScanUtc is not null)
            ? states.Min(status => status.LastSuccessfulScanUtc) : null;
        return new IntakeStatus(
            state, paused, lastScan,
            states.Sum(status => status.ObservedFiles),
            states.Sum(status => status.ImportedSinceStart),
            states.Sum(status => status.ErrorCount),
            states.SelectMany(status => status.FileErrors).Take(8).ToArray(),
            affected == 0 ? null : $"{affected} watched folder(s) need attention.");
    }
}
