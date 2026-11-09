using STranslate.Plugin;
using STranslate.Plugin.Translate.OpenAI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace STranslate.Tests;

public class OpenAITemperatureTests
{
    [Fact]
    public void ViewModelSavesSwitchAndRestoresIt()
    {
        var context = System.Reflection.DispatchProxy.Create<IPluginContext, StorageProxy>();
        var storage = (StorageProxy)(object)context;
        var settings = new Settings();
        using (var vm = new STranslate.Plugin.Translate.OpenAI.ViewModel.SettingsViewModel(context, settings, new Main()))
        {
            Assert.False(vm.SendTemperature);
            vm.SendTemperature = true;
            Assert.True(settings.SendTemperature);
            Assert.Equal(1, storage.Saves);
        }
        using var restored = new STranslate.Plugin.Translate.OpenAI.ViewModel.SettingsViewModel(context, settings, new Main());
        Assert.True(restored.SendTemperature);
        restored.SendTemperature = false;
        Assert.False(settings.SendTemperature);
        Assert.Equal(2, storage.Saves);
    }

    public class StorageProxy : System.Reflection.DispatchProxy
    {
        public int Saves;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "SaveSettingStorage") Saves++;
            return null;
        }
    }

    [Theory]
    [InlineData(OpenAIApiMode.ChatCompletions, false)]
    [InlineData(OpenAIApiMode.Responses, false)]
    [InlineData(OpenAIApiMode.ChatCompletions, true)]
    [InlineData(OpenAIApiMode.Responses, true)]
    public void RequestHonorsSwitch(OpenAIApiMode mode, bool enabled)
    {
        var request = (JsonObject)OpenAIProtocol.CreateRequest(mode, "test", new List<PromptItem>(), 0.7, "{\"top_p\":0.9}", enabled);
        Assert.Equal(enabled, request.ContainsKey("temperature"));
        if (enabled) Assert.Equal(0.7, request["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, request["top_p"]!.GetValue<double>());
    }

    [Fact]
    public void LegacySettingsDefaultOffAndSwitchPersists()
    {
        var settings = JsonSerializer.Deserialize<Settings>("{\"Temperature\":0.4}")!;
        Assert.False(settings.SendTemperature);
        Assert.Equal(0.4, settings.Temperature);
        settings.SendTemperature = true;
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        Assert.True(restored.SendTemperature);
        Assert.Equal(0.4, restored.Temperature);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("Temperature")]
    public void AdditionalParametersCannotBypassSwitch(string name)
    {
        Assert.Throws<FormatException>(() => OpenAIProtocol.CreateRequest(OpenAIApiMode.ChatCompletions, "test", new List<PromptItem>(), 0.7, "{\"" + name + "\":1}"));
    }
}
