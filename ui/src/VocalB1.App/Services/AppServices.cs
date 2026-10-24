using System.IO;
using VocalB1.Core.Abstractions;
using VocalB1.Core.Configuration;
using VocalB1.Engine.Bridge;

namespace VocalB1.App.Services;

/// <summary>
/// Composition root: explicitly creates and holds all services so the UI layer does not scatter new calls.
/// Zero third-party dependencies — everything uses BCL / system APIs.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    public EngineOptions Options { get; }
    public IRenderEngine Engine { get; }
    public IAudioPlayback Playback { get; }

    private AppServices(EngineOptions options, IRenderEngine engine, IAudioPlayback playback)
    {
        Options = options;
        Engine = engine;
        Playback = playback;
    }

    public static AppServices Create()
    {
        var options = EngineOptions.Discover();

        // Prefer the editable configuration inside the repository (ui/engine.settings.json)
        var settingsPath = Path.Combine(options.RepositoryRoot, "ui", "engine.settings.json");
        if (File.Exists(settingsPath))
            options = EngineOptions.Load(settingsPath);

        return new AppServices(
            options,
            new PythonEngineClient(options),
            new MciPlayback());
    }

    public async ValueTask DisposeAsync()
    {
        if (Playback is IDisposable disposable)
            disposable.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
    }
}
