namespace Aipod.Tray;

/// <summary>
/// The tray's brain, with no window in it: it keeps the latest status, feeds the message counters to the blinking lights, and runs the commands the menu
/// and the window ask for. The user interface only calls it and draws what it says.
/// </summary>
public sealed class TrayController(StatusReader reader, TrayActions actions, LedBlinker blinker)
{
    public TrayStatus Status { get; private set; } = new("", false, false, null, "starting", [], null, 0, 0);

    /// <summary>What the last command said, for the window to show.</summary>
    public string LastResult { get; private set; } = "";

    public bool Busy { get; private set; }

    public Leds Leds => blinker.Now();

    /// <summary>Raised after the status or a command's result changed.</summary>
    public event Action? Changed;

    /// <summary>Reads everything (the window and the menu need it).</summary>
    public void Refresh()
    {
        Status = reader.Read();
        blinker.Observe(Status.Incoming, Status.Outgoing);
        Changed?.Invoke();
    }

    /// <summary>Reads only the counters, so a message shows in the lights within a fraction of a second.</summary>
    public void RefreshLights()
    {
        var (incoming, outgoing) = reader.ReadCounters();
        blinker.Observe(incoming, outgoing);
    }

    public async Task RunAsync(TrayCommand command, string? server, CancellationToken ct)
    {
        if (Busy) return;
        Busy = true;
        LastResult = "…";
        Changed?.Invoke();
        try
        {
            var result = await actions.RunAsync(command, server, ct);
            LastResult = result.Text;
        }
        finally
        {
            Busy = false;
        }

        Refresh();
    }
}
