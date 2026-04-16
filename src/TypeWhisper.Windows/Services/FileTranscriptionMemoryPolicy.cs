namespace TypeWhisper.Windows.Services;

internal static class FileTranscriptionMemoryPolicy
{
    internal const double FileBackedTranscriptionThresholdSeconds = 180;
    internal const int FileBackedTranscriptionChunkSamples = 16000 * 60;

    internal static bool UsesSpeechSegmentation(bool useVoiceActivityDetection, bool useSpeakerDiarization) =>
        useVoiceActivityDetection || useSpeakerDiarization;

    internal static bool ShouldUseFileBackedTranscription(double durationSeconds, bool useSpeakerDiarization) =>
        !useSpeakerDiarization && durationSeconds >= FileBackedTranscriptionThresholdSeconds;
}
