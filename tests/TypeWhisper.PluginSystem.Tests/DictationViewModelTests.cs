using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictationViewModelTests
{
    [Theory]
    [InlineData(HotkeyMode.PushToTalk, true)]
    [InlineData(HotkeyMode.Toggle, false)]
    public void ShouldReleaseCaptureAfterStop_OnlyForPushToTalk(HotkeyMode hotkeyMode, bool expected)
    {
        Assert.Equal(expected, DictationViewModel.ShouldReleaseCaptureAfterStop(hotkeyMode));
    }

    [Fact]
    public void ShouldReleaseCaptureAfterStop_FalseWhenModeUnknown()
    {
        Assert.False(DictationViewModel.ShouldReleaseCaptureAfterStop(null));
    }
}
