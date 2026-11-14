namespace Praxis.Application.Media;

/// <summary>On-device speech recognition over an audio or video file. Nothing leaves the Mac.</summary>
public interface ITranscriptionEngine
{
    /// <summary>False when this system cannot transcribe (no helper, or an older macOS).</summary>
    bool IsAvailable { get; }

    /// <summary>Transcribes the file at <paramref name="path"/>, whose extension names its container, within the budget.</summary>
    Task<TranscriptionResult> TranscribeAsync(string path, TimeSpan budget, CancellationToken cancellationToken);
}
