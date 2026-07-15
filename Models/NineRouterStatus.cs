namespace CodexAccountBar.Models;
public enum NineRouterKind { None, WindowsService, Docker, NpmCli }
public sealed record NineRouterStatus(bool Installed, bool Running, NineRouterKind Kind, string Detail, string? ServiceName = null, int? ProcessId = null, string? Executable = null);
