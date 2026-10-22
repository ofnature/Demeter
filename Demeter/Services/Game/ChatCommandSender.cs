using System;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Demeter.Services.Game;

/// <summary>
/// Sends a slash command as though the user typed it.
///
/// <para>
/// Needed because some things Demeter does have no IPC at all — Repair is a general action sent as a
/// slash command, and so is the home trip. They funnel through
/// here so there is exactly one place doing this.
/// </para>
///
/// <para>
/// <b>Commands only.</b> Anything not starting with a slash is refused. Route files are the user's
/// own imports and a chat command from one is no more privileged than a macro they wrote, but
/// "send arbitrary text as the player" is not a capability worth having lying around — especially
/// once fleet messages start arriving over the relay.
/// </para>
/// </summary>
public sealed unsafe class ChatCommandSender
{
    private readonly Action<string> _log;

    public ChatCommandSender(Action<string> log) => _log = log;

    public void Send(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || !command.TrimStart().StartsWith('/'))
            return;

        try
        {
            var module = UIModule.Instance();
            if (module is null)
                return;

            var message = Utf8String.FromString(command.Trim());
            module->ProcessChatBoxEntry(message, nint.Zero, false);
            message->Dtor(true);
        }
        catch (Exception ex)
        {
            _log($"Chat command \"{command}\" failed: {ex.Message}");
        }
    }
}
