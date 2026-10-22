using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Demeter.Services.Ipc;

/// <summary>
/// What Demeter asks of Daedalus. Fails open — a missing or older Daedalus is a degraded feature,
/// never an exception on our side. (Trimmed from Odysseus's client: Demeter never targets or holds
/// actions, it only needs to know whether anyone will fight for it.)
/// </summary>
public sealed class DaedalusIpc
{
    private const string IsDisabledByUserGate = "Daedalus.IsDisabledByUser";

    private readonly IDalamudPluginInterface _pluginInterface;
    private ICallGateSubscriber<bool>? _isDisabledByUser;

    public DaedalusIpc(IDalamudPluginInterface pluginInterface) => _pluginInterface = pluginInterface;

    /// <summary>
    /// The user switched Daedalus OFF, so it will not fight for us. Not the same as "not enabled":
    /// Daedalus's switch starts off and still fights for automation until someone presses Disable.
    /// False when Daedalus is missing or too old to say — travel then holds for the fight as before.
    /// </summary>
    public bool IsDisabledByUser()
    {
        try
        {
            _isDisabledByUser ??= _pluginInterface.GetIpcSubscriber<bool>(IsDisabledByUserGate);
            return _isDisabledByUser.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }
}
