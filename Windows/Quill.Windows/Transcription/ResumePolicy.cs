using Quill.Windows.Sessions;

namespace Quill.Windows.Transcription;

/// <summary>
/// Decide si una sesion se reencola al arrancar. Solo un "failed" explicito
/// detiene el reintento; cualquier otro estado se reintenta, igual que antes.
/// </summary>
internal static class ResumePolicy
{
    public static bool ShouldEnqueue(bool hasMetadata, bool hasTranscript, SessionStatus? status)
    {
        if (!hasMetadata || hasTranscript)
        {
            return false;
        }

        return status?.State != SessionStatus.Failed;
    }
}
