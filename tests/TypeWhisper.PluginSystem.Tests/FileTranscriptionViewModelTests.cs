using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class FileTranscriptionViewModelTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly Mock<IDictionaryService> _dictionary = new();
    private readonly Mock<IVocabularyBoostingService> _vocabularyBoosting = new();
    private readonly Mock<IPostProcessingPipeline> _pipeline = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private readonly AudioFileService _audioFile = new();
    private readonly FileVocalIsolationService _vocalIsolation;
    private readonly FileSpeechSegmentationService _speechSegmentation;
    private readonly FileSpeakerDiarizationService _speakerDiarization = new();

    public FileTranscriptionViewModelTests()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        _vocalIsolation = new FileVocalIsolationService(_audioFile);
        _speechSegmentation = new FileSpeechSegmentationService(_audioFile);
        _profiles.Setup(p => p.Profiles).Returns([]);
        _dictionary.Setup(d => d.ApplyCorrections(It.IsAny<string>())).Returns<string>(text => text);
        _vocabularyBoosting.Setup(v => v.Apply(It.IsAny<string>())).Returns<string>(text => text);
    }

    public void Dispose()
    {
        _vocalIsolation.Dispose();
        _speakerDiarization.Dispose();
    }

    [Fact]
    public void Constructor_LoadsSavedPreprocessingOptions_WithoutSavingAgain()
    {
        _settings.Setup(s => s.Current).Returns(AppSettings.Default with
        {
            FileTranscriptionVadEnabled = false,
            FileTranscriptionSpeakerDiarizationEnabled = true,
        });

        var sut = CreateSubject();

        Assert.False(sut.UseVoiceActivityDetection);
        Assert.True(sut.UseSpeakerDiarization);
        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public void ChangingVadToggle_SavesUpdatedSetting()
    {
        AppSettings? saved = null;
        _settings.Setup(s => s.Current).Returns(AppSettings.Default);
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(settings => saved = settings);

        var sut = CreateSubject();
        sut.UseVoiceActivityDetection = false;

        Assert.NotNull(saved);
        Assert.False(saved!.FileTranscriptionVadEnabled);
    }

    [Fact]
    public void ChangingVocalIsolationToggle_SavesUpdatedSetting()
    {
        AppSettings? saved = null;
        _settings.Setup(s => s.Current).Returns(AppSettings.Default);
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(settings => saved = settings);

        var sut = CreateSubject();
        sut.UseVocalIsolation = true;

        Assert.NotNull(saved);
        Assert.True(saved!.FileTranscriptionVocalIsolationEnabled);
    }

    [Fact]
    public void ChangingSpeakerDiarizationToggle_SavesUpdatedSetting()
    {
        AppSettings? saved = null;
        _settings.Setup(s => s.Current).Returns(AppSettings.Default);
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(settings => saved = settings);

        var sut = CreateSubject();
        sut.UseSpeakerDiarization = true;

        Assert.NotNull(saved);
        Assert.True(saved!.FileTranscriptionSpeakerDiarizationEnabled);
    }

    [Fact]
    public void CombineSegmentsText_MergesConsecutiveSpeakerLines()
    {
        var segments = new[]
        {
            new TranscriptionSegment("Speaker 1: Hello", 0, 1),
            new TranscriptionSegment("Speaker 1: there", 1, 2),
            new TranscriptionSegment("Speaker 2: General", 2, 3),
            new TranscriptionSegment("Speaker 2: Kenobi", 3, 4),
        };

        var text = FileTranscriptionViewModel.CombineSegmentsText(segments, [0, 0, 1, 1], useLineBreaks: true);

        Assert.Equal($"Speaker 1: Hello there{Environment.NewLine}Speaker 2: General Kenobi", text);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void UsesSpeechSegmentation_MatchesPreprocessingOptions(bool useVad, bool useSpeakerDiarization, bool expected)
    {
        var usesSpeechSegmentation = FileTranscriptionMemoryPolicy.UsesSpeechSegmentation(useVad, useSpeakerDiarization);

        Assert.Equal(expected, usesSpeechSegmentation);
    }

    [Theory]
    [InlineData(179.9, false, false)]
    [InlineData(180.0, false, true)]
    [InlineData(600.0, false, true)]
    [InlineData(180.0, true, false)]
    [InlineData(600.0, true, false)]
    public void ShouldUseFileBackedTranscription_OnlyForLongFilesWithoutDiarization(
        double durationSeconds,
        bool useSpeakerDiarization,
        bool expected)
    {
        var useFileBackedTranscription = FileTranscriptionMemoryPolicy.ShouldUseFileBackedTranscription(
            durationSeconds,
            useSpeakerDiarization);

        Assert.Equal(expected, useFileBackedTranscription);
    }

    private FileTranscriptionViewModel CreateSubject()
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object,
            []);
        var modelManager = new ModelManagerService(pluginManager, _settings.Object);

        return new FileTranscriptionViewModel(
            modelManager,
            _settings.Object,
            _audioFile,
            _vocalIsolation,
            _speechSegmentation,
            _speakerDiarization,
            _dictionary.Object,
            _vocabularyBoosting.Object,
            _pipeline.Object);
    }
}
