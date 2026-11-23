using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

/// <summary>
/// Decide si una sesion se reencola al arrancar. Un transcript valido o vacio ya
/// termina la sesion; un transcript invalido solo se recupera si el estado durable
/// conserva una fase recuperable.
/// </summary>
internal static class ResumePolicy
{
    public static bool ShouldEnqueue(bool hasMetadata, bool hasTranscript, SessionStatus? status)
    {
        return ShouldEnqueue(
            hasMetadata,
            hasTranscript ? TranscriptReadStatus.Valid : TranscriptReadStatus.Missing,
            status);
    }

    public static bool ShouldEnqueue(
        bool hasMetadata,
        TranscriptReadStatus transcriptStatus,
        SessionStatus? status)
    {
        if (!hasMetadata || transcriptStatus is TranscriptReadStatus.Valid or TranscriptReadStatus.Empty)
        {
            return false;
        }

        if (status?.State == SessionStatus.Failed)
        {
            return false;
        }

        return transcriptStatus == TranscriptReadStatus.Missing ||
            status?.State is SessionStatus.Queued or SessionStatus.Running or SessionStatus.Disabled;
    }
}
