namespace BrutalTestEngine.Commands;

/// <summary>
/// Applies commands. One implementation per thread that owns state, so a handler only ever sees the
/// commands routed to it and never has to think about other threads.
/// </summary>
/// <remarks>
/// <para>
/// Commands are applied through a visitor rather than a switch in the business logic or a dictionary of
/// delegates. The compiler then enforces that adding a new command type makes every handler decide what
/// it does with it. A switch over a type hierarchy would silently drop new cases, and a dictionary of
/// delegates would only fail at the moment a command arrived.
/// </para>
/// <para>
/// A handler is invoked by exactly one thread, which is why it may hold plain mutable state with no
/// synchronisation.
/// </para>
/// </remarks>
public interface ICommandHandler
{
    void Handle(QuitCommand command);

    void Handle(HelpCommand command);

    void Handle(SetWindowModeCommand command);

    void Handle(ToggleWindowModeCommand command);

    void Handle(SetTickRateCommand command);

    void Handle(InjectInputCommand command);

    void Handle(KeyCommand command);

    void Handle(MouseButtonCommand command);

    void Handle(CursorMoveCommand command);

    void Handle(ScrollCommand command);

    void Handle(TextInputCommand command);

    void Handle(FocusCommand command);

    void Handle(StepSimulationCommand command);

    void Handle(SetPausedCommand command);

    void Handle(ResetSimulationCommand command);

    void Handle(CaptureFrameCommand command);

    void Handle(SetLogLevelCommand command);

    void Handle(AddBodyCommand command);

    void Handle(SetGravityCommand command);

    void Handle(SetVolumeCommand command);

    void Handle(UnhandledCommand command);
}

/// <summary>
/// Dispatches a command to the overload matching its concrete type.
/// </summary>
public static class CommandDispatcher
{
    public static void Dispatch(ICommandHandler handler, Command command)
    {
        switch (command)
        {
            case QuitCommand quit:
                handler.Handle(quit);
                break;
            case HelpCommand help:
                handler.Handle(help);
                break;
            case SetWindowModeCommand setWindowMode:
                handler.Handle(setWindowMode);
                break;
            case ToggleWindowModeCommand toggleWindowMode:
                handler.Handle(toggleWindowMode);
                break;
            case SetTickRateCommand setTickRate:
                handler.Handle(setTickRate);
                break;
            case InjectInputCommand injectInput:
                handler.Handle(injectInput);
                break;
            case KeyCommand key:
                handler.Handle(key);
                break;
            case MouseButtonCommand mouseButton:
                handler.Handle(mouseButton);
                break;
            case CursorMoveCommand cursorMove:
                handler.Handle(cursorMove);
                break;
            case ScrollCommand scroll:
                handler.Handle(scroll);
                break;
            case TextInputCommand textInput:
                handler.Handle(textInput);
                break;
            case FocusCommand focus:
                handler.Handle(focus);
                break;
            case StepSimulationCommand step:
                handler.Handle(step);
                break;
            case SetPausedCommand paused:
                handler.Handle(paused);
                break;
            case ResetSimulationCommand reset:
                handler.Handle(reset);
                break;
            case CaptureFrameCommand capture:
                handler.Handle(capture);
                break;
            case SetLogLevelCommand logLevel:
                handler.Handle(logLevel);
                break;
            case AddBodyCommand addBody:
                handler.Handle(addBody);
                break;
            case SetGravityCommand setGravity:
                handler.Handle(setGravity);
                break;
            case SetVolumeCommand setVolume:
                handler.Handle(setVolume);
                break;
            case UnhandledCommand unhandled:
                handler.Handle(unhandled);
                break;
            default:
                // Every Command subclass has an arm above. A command can only reach here from a future
                // type nobody taught this dispatcher about, which is exactly the case the visitor
                // exists to make visible.
                throw new NotSupportedException($"No handler for command type {command.GetType().Name}.");
        }
    }
}
