using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SteamLinkVRSettings.Controls;

// Slider, ComboBox and NumberBox all consume PointerWheelChanged to adjust their own
// value and mark the event handled, so it never bubbles to the parent ScrollViewer.
// Every settings row hosts one of them on the right-hand side, which makes wheel /
// two-finger scrolling work over the header text but die over the controls — dragging
// the scrollbar always works, because that path doesn't involve these controls.
//
// Overriding OnPointerWheelChanged without calling base skips the value adjustment and
// leaves Handled false, so the event bubbles up and the ScrollViewer scrolls as usual.

public partial class NoWheelSlider : Slider
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.Handled = false;
    }
}

public partial class NoWheelComboBox : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        // Only swallow the wheel while the drop-down is open, where scrolling the
        // item list is what the user actually means.
        if (IsDropDownOpen)
        {
            base.OnPointerWheelChanged(e);
            return;
        }

        e.Handled = false;
    }
}

public partial class NoWheelNumberBox : NumberBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        e.Handled = false;
    }
}
