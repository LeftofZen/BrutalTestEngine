using System.Diagnostics;
using Brutal.Logging;
using BrutalTestEngine.Audio;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Simulation;

/// <summary>
/// Owns the audio thread, which keeps the mixer running and applies audio commands.
/// </summary>
/// <remarks>
/// <para>
/// The mixer runs on its own thread because it has its own clock. A game tick must never block on a sound
/// device, and a sound device must never be starved because a tick took too long; giving audio its own thread
/// and its own period is what lets both hold their own deadlines. This is the standard split in engines that
/// mix in software, and it is the reason the audio thread deliberately does not read simulation state
/// directly — it receives commands like every other consumer.
/// </para>
/// <para>
/// The thread updates at a modest fixed rate rather than at the mixer's internal buffer rate. FMOD's system is
/// asynchronous: <c>Update</c> services its queue and lets the device's own thread do the streaming, so calling
/// it a few hundred times a second is what the API expects, and calling it in a tight loop would only burn a
/// core.
/// </para>
/// </remarks>
public sealed class AudioThread : IDisposable, ICommandHandler
{
    /// <summary>
    /// How often the mixer is updated.
    /// </summary>
    /// <remarks>
    /// 10 ms is a compromise: fast enough that a command changing a volume is audible promptly, slow enough that
    /// servicing the queue costs nothing measurable. The device's own callback rate is the real audio clock.
    /// </remarks>
    private const int UpdateIntervalMilliseconds = 10;

    private static readonly LogCategory Log = EngineLog.Categories.Engine;

    private readonly CommandBus _bus;
    private readonly IAudioSystem _audio;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ThreadMetrics _updateMetrics;
    private readonly ThreadMetrics _drainMetrics;

    private long _commandsApplied;
    private long _rejectedCommands;
    private volatile bool _disposed;

    public AudioThread(CommandBus bus, IAudioSystem audio, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
        _audio = audio;
        _updateMetrics = timers.Get(ThreadTimerRegistry.Keys.AudioUpdate);
        _drainMetrics = timers.Get(ThreadTimerRegistry.Keys.AudioDrain);

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"audio thread started with backend '{audio.Name}' (available: {audio.IsAvailable})");

        _thread = new Thread(Run)
        {
            Name = "Audio",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The audio system this thread owns.</summary>
    public IAudioSystem Audio => _audio;

    /// <summary>Commands applied on this thread.</summary>
    public long CommandsApplied => Interlocked.Read(ref _commandsApplied);

    /// <summary>Commands this thread refused because audio is unavailable.</summary>
    public long RejectedCommands => Interlocked.Read(ref _rejectedCommands);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _audio.Dispose();
        _cancellation.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            var drainStartedAt = Stopwatch.GetTimestamp();
            _bus.Drain(CommandTarget.Audio, this, MaxCommandsPerDrain);
            _drainMetrics.RecordSince(drainStartedAt);

            var updateStartedAt = Stopwatch.GetTimestamp();
            _audio.Update();
            _updateMetrics.RecordSince(updateStartedAt);

            token.WaitHandle.WaitOne(UpdateIntervalMilliseconds);
        }

        // One last service call so a command applied just before shutdown has reached the device, rather than
        // being dropped because the thread stopped between draining and updating.
        _audio.Update();
    }

    private const int MaxCommandsPerDrain = 32;

    /// <summary>Handles <see cref="SetVolumeCommand"/>.</summary>
    void ICommandHandler.Handle(SetVolumeCommand command)
    {
        if (!_audio.IsAvailable)
        {
            Interlocked.Increment(ref _rejectedCommands);
            return;
        }

        _audio.SetMasterVolume(command.Volume);
        Interlocked.Increment(ref _commandsApplied);
    }

    // Audio commands are the only ones this thread owns, so the handlers below only acknowledge the fact.

    void ICommandHandler.Handle(QuitCommand command) => Ignore();

    void ICommandHandler.Handle(HelpCommand command) => Ignore();

    void ICommandHandler.Handle(SetWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(ToggleWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(SetTickRateCommand command) => Ignore();

    void ICommandHandler.Handle(InjectInputCommand command) => Ignore();

    void ICommandHandler.Handle(KeyCommand command) => Ignore();

    void ICommandHandler.Handle(MouseButtonCommand command) => Ignore();

    void ICommandHandler.Handle(CursorMoveCommand command) => Ignore();

    void ICommandHandler.Handle(ScrollCommand command) => Ignore();

    void ICommandHandler.Handle(TextInputCommand command) => Ignore();

    void ICommandHandler.Handle(FocusCommand command) => Ignore();

    void ICommandHandler.Handle(StepSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(SetPausedCommand command) => Ignore();

    void ICommandHandler.Handle(ResetSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(CaptureFrameCommand command) => Ignore();

    void ICommandHandler.Handle(SetLogLevelCommand command) => Ignore();

    void ICommandHandler.Handle(AddBodyCommand command) => Ignore();

    void ICommandHandler.Handle(SetGravityCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => Ignore();

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
    }
}
