using Avalonia.Controls;
using Perch.Avalonia.Windows;
using Perch.Remote;

namespace Perch.Avalonia;

// Remote workers (docs/remote-workers-plan.md): the app-side prompts the WorkerHost needs a window for.
public partial class App
{
    // A machine asked to pair while pairing was open here. The code is what makes it safe: a machine in the middle
    // would show a different one on each side, so the user only allows when both screens match.
    private async Task<bool> AskToPairController(PairingRequest request)
    {
        Window? owner = (Window?)_settings ?? _overlay;
        if (owner is null) return false;
        return await ConfirmDialog.ShowAsync(owner,
            $"Let {request.Name} control this machine?",
            $"{request.Name} should be showing the code {request.Code}. Allow only if it matches exactly. Once paired, it " +
            "can send prompts to this machine's Perch sessions, answer their permission prompts and interrupt them. " +
            "You can remove it any time in Settings → Workers.",
            "Allow", "Don't allow");
    }
}
