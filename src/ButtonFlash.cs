using Godot;

// Generic click feedback, opt-in via AttachClickFlash: any BaseButton (Button, TextureButton,
// etc.) briefly flashes white on a completed click (Pressed), regardless of what the button
// itself looks like. Implemented as a full-rect white overlay whose alpha ramps 0 -> 1 -> 0
// linearly, rather than tweening the button's own Modulate - Modulate only multiplies a
// texture's existing colors (so it can darken, but can't reliably brighten arbitrary content
// all the way to white), whereas an overlay works identically for any button's appearance.
public static class ButtonFlash
{
    private const float FlashSeconds = 0.2f;

    // How many buttons currently have a flash animation in flight. Exposed so the app's
    // framerate-throttling logic can stay unthrottled while a flash is running - otherwise the
    // Tween driving it would only advance at the throttled idle rate and visibly stutter.
    private static int _activeCount;
    public static bool IsAnyFlashing => _activeCount > 0;

    public static void AttachClickFlash(this BaseButton button)
    {
        var overlay = new ColorRect
        {
            Color = new Color(1f, 1f, 1f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(overlay);

        Tween tween = null;
        bool isActive = false;

        button.Pressed += () =>
        {
            // Restart cleanly if a previous flash is still running (e.g. rapid clicking),
            // rather than letting two tweens fight over the same property. Kill() doesn't fire
            // the old tween's Finished, so _activeCount is only touched on the true start/end
            // of a (possibly restarted) flash, never per-click.
            tween?.Kill();
            if (!isActive)
            {
                isActive = true;
                _activeCount++;
            }
            overlay.Color = new Color(1f, 1f, 1f, 0f);
            tween = button.CreateTween();
            tween.TweenProperty(overlay, "color:a", 1f, FlashSeconds / 2f).SetTrans(Tween.TransitionType.Linear);
            tween.TweenProperty(overlay, "color:a", 0f, FlashSeconds / 2f).SetTrans(Tween.TransitionType.Linear);
            tween.Finished += () =>
            {
                isActive = false;
                _activeCount--;
            };
        };
    }
}
