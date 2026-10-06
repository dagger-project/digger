using System;

namespace Digger.Cli;

/// <summary>A mistake in what the user typed; reported as "Command failed: ...".</summary>
internal sealed class CommandException(string message) : Exception(message);
