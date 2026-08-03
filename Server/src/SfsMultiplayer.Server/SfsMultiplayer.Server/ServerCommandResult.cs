namespace SfsMultiplayer.Server;

public readonly record struct ServerCommandResult(bool RequestShutdown, string Message);
