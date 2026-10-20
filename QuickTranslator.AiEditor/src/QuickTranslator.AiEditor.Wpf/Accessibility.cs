using System.Windows;
using System.Windows.Automation.Peers;

namespace QuickTranslator.AiEditor.Wpf;

internal static class Accessibility
{
    internal static void Announce(FrameworkElement element)
    {
        try
        {
            AutomationPeer? peer = UIElementAutomationPeer.FromElement(element)
                ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch
        {
            // Accessibility notification failures must never interrupt the UI operation.
        }
    }
}
